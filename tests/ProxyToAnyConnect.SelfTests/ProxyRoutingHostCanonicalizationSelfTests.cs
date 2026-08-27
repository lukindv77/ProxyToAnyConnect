using System.Net;
using System.Net.Sockets;
using System.Text;
using ProxyToAnyConnect.Configuration;
using ProxyToAnyConnect.Network;
using ProxyToAnyConnect.Proxy;

namespace ProxyToAnyConnect.SelfTests;

internal static class ProxyRoutingHostCanonicalizationSelfTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public static async Task<int> RunAsync()
    {
        try
        {
            var legacyForms = DetectRuntimeLegacyIpv4Forms();
            SharedNormalizerRejectsAmbiguousIpv4(legacyForms);
            ProxyTargetsRejectAmbiguousIpv4(legacyForms);
            await LiveRequestsRejectBeforeOutboundAsync(legacyForms);
            Console.WriteLine(
                $"PASS: routing host canonicalization rejects {legacyForms.Length} runtime-recognized legacy IPv4 form(s) before outbound ownership");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: routing-host IPv4 canonicalization regression: {ex}");
            return 1;
        }
    }

    private static string[] DetectRuntimeLegacyIpv4Forms()
    {
        string[] candidates =
        [
            "127.1",
            "127.0.1",
            "2130706433",
            "0x7f000001",
            "017700000001",
            "0177.0.0.1",
            "127.000.000.001"
        ];

        var detected = candidates
            .Where(candidate =>
                IPAddress.TryParse(candidate, out var address) &&
                address.AddressFamily == AddressFamily.InterNetwork &&
                !candidate.Equals(address.ToString(), StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (detected.Length == 0)
        {
            throw new InvalidOperationException(
                "The current Windows/.NET runtime did not recognize any legacy IPv4 form from the regression matrix; the ambiguity test would be vacuous.");
        }

        return detected;
    }

    private static void SharedNormalizerRejectsAmbiguousIpv4(string[] legacyForms)
    {
        AssertNormalized("127.0.0.1", "127.0.0.1");
        AssertNormalized("EXAMPLE.TEST.", "example.test");
        AssertNormalized("münich.example.", "xn--mnich-kva.example");

        foreach (var legacy in legacyForms)
        {
            AssertRoutingHostRejected(legacy);
        }

        // A terminal root dot is supported for DNS names only. Treating a dotted
        // IPv4 literal as an FQDN would bypass exact textual IPv4 canonicality.
        AssertRoutingHostRejected("127.0.0.1.");
    }

    private static void ProxyTargetsRejectAmbiguousIpv4(string[] legacyForms)
    {
        foreach (var legacy in legacyForms.Append("127.0.0.1."))
        {
            AssertRejected(() => ProxyServer.ParseAuthority($"{legacy}:443", 443), legacy, "CONNECT");
            AssertRejected(() => ProxyServer.ParseHttpTarget($"http://{legacy}/"), legacy, "plain HTTP");
        }
    }

    private static async Task LiveRequestsRejectBeforeOutboundAsync(string[] legacyForms)
    {
        using var timeout = new CancellationTokenSource(Timeout);
        var factory = new CountingRejectingFactory();
        var proxyPort = ReserveLoopbackPort();
        using var proxyCancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var proxy = new ProxyServer(
            new ProxyOptions
            {
                ListenAddress = "127.0.0.1",
                ListenPort = proxyPort,
                MaxConcurrentConnections = 8,
                MaxHeaderBytes = 65536,
                ClientHeaderTimeoutSeconds = 5
            },
            factory);
        var proxyTask = proxy.RunAsync(proxyCancellation.Token);
        await proxy.WaitUntilListeningAsync(timeout.Token);

        try
        {
            foreach (var legacy in legacyForms.Append("127.0.0.1."))
            {
                var connectResponse = await SendRawRequestAsync(
                    proxyPort,
                    $"CONNECT {legacy}:443 HTTP/1.1\r\nHost: {legacy}:443\r\n\r\n",
                    timeout.Token);
                AssertBadRequest(connectResponse, legacy, "CONNECT");

                var httpResponse = await SendRawRequestAsync(
                    proxyPort,
                    $"GET http://{legacy}/ HTTP/1.1\r\nHost: attacker.invalid\r\nConnection: close\r\n\r\n",
                    timeout.Token);
                AssertBadRequest(httpResponse, legacy, "plain HTTP");
            }

            if (factory.ConnectCount != 0)
            {
                throw new InvalidOperationException(
                    $"Ambiguous routing hosts opened {factory.ConnectCount} outbound connection(s).");
            }
        }
        finally
        {
            proxyCancellation.Cancel();
            await proxyTask;
        }
    }

    private static void AssertNormalized(string input, string expected)
    {
        var actual = ProxyServer.NormalizeRoutingHost(input);
        if (!actual.Equals(expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Routing host '{input}' normalized to '{actual}', expected '{expected}'.");
        }
    }

    private static void AssertRoutingHostRejected(string input)
    {
        AssertRejected(() => ProxyServer.NormalizeRoutingHost(input), input, "shared normalizer");
    }

    private static void AssertRejected(Action action, string value, string path)
    {
        try
        {
            action();
        }
        catch (InvalidDataException)
        {
            return;
        }
        catch (NotSupportedException)
        {
            return;
        }

        throw new InvalidOperationException(
            $"{path} accepted ambiguous/non-canonical routing host '{value}'.");
    }

    private static void AssertBadRequest(string response, string host, string path)
    {
        if (!response.StartsWith("HTTP/1.1 400 Bad Request\r\n", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Live {path} request for '{host}' was not rejected as 400 before routing: {response}");
        }
    }

    private static async Task<string> SendRawRequestAsync(
        int proxyPort,
        string request,
        CancellationToken cancellationToken)
    {
        using var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync(IPAddress.Loopback, proxyPort, cancellationToken);
        await using var stream = client.GetStream();
        await stream.WriteAsync(Encoding.Latin1.GetBytes(request), cancellationToken);
        return Encoding.Latin1.GetString(await ReadToEndAsync(stream, cancellationToken));
    }

    private static async Task<byte[]> ReadToEndAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken);
            if (read == 0)
            {
                return buffer.ToArray();
            }

            buffer.Write(chunk, 0, read);
        }
    }

    private static int ReserveLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private sealed class CountingRejectingFactory : IProxyOutboundConnectionFactory
    {
        private int _connectCount;
        public int ConnectCount => Volatile.Read(ref _connectCount);

        public Task<IProxyOutboundConnection> ConnectAsync(
            string host,
            int port,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _connectCount);
            throw new InvalidOperationException(
                $"Ambiguous routing host reached outbound connection factory as {host}:{port}.");
        }
    }
}
