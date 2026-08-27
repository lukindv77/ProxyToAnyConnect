using System.Net;
using System.Net.Sockets;
using System.Text;
using ProxyToAnyConnect.Configuration;
using ProxyToAnyConnect.Network;
using ProxyToAnyConnect.Proxy;

namespace ProxyToAnyConnect.SelfTests;

internal static class ProxyConnectionOptionSelfTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public static async Task<int> RunAsync()
    {
        try
        {
            ValidConnectionOptionsPreserveHopByHopFiltering();
            InvalidConnectionOptionsAreRejected();
            OverflowConnectionOptionsStillRemoveNominatedHeaders();
            await InvalidConnectionOptionsDoNotRouteAsync();
            Console.WriteLine("PASS: plain-HTTP Connection options are token-validated before outbound routing");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: Connection option grammar regression: {ex}");
            return 1;
        }
    }

    private static void ValidConnectionOptionsPreserveHopByHopFiltering()
    {
        var request = Parse(
            "GET http://example.test/path HTTP/1.1\r\n" +
            "Host: attacker.invalid\r\n" +
            "Connection: \tX-One , X-Two\t\r\n" +
            "X-One: remove-one\r\n" +
            "X-Two: remove-two\r\n" +
            "X-Keep: yes\r\n\r\n");
        var outbound = Encoding.Latin1.GetString(request.BuildOriginHeader("/path", "example.test"));
        if (outbound.Contains("X-One:", StringComparison.OrdinalIgnoreCase) ||
            outbound.Contains("X-Two:", StringComparison.OrdinalIgnoreCase) ||
            outbound.Contains("attacker.invalid", StringComparison.OrdinalIgnoreCase) ||
            !outbound.Contains("Host: example.test\r\n", StringComparison.Ordinal) ||
            !outbound.Contains("X-Keep: yes\r\n", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Valid Connection options changed hop-by-hop or generated-Host filtering.");
        }
    }

    private static void InvalidConnectionOptionsAreRejected()
    {
        foreach (var value in new[]
        {
            "",
            " ",
            ",X-One",
            "X-One,",
            "X-One,,X-Two",
            "X-One;foo",
            "X-One=foo",
            "X/One",
            "X One",
            "X-One\u00a0",
            "\u00a0X-One"
        })
        {
            AssertParseRejected(
                "GET http://example.test/ HTTP/1.1\r\n" +
                "Host: example.test\r\n" +
                $"Connection:{value}\r\n" +
                "X-One: secret\r\n\r\n");
        }
    }

    private static void OverflowConnectionOptionsStillRemoveNominatedHeaders()
    {
        var options = Enumerable.Range(1, 12).Select(i => $"X-Hop-{i}").ToArray();
        var builder = new StringBuilder();
        builder.Append("GET http://example.test/overflow HTTP/1.1\r\n");
        builder.Append("Host: attacker.invalid\r\n");
        builder.Append("Connection: ").Append(string.Join(", ", options)).Append("\r\n");
        foreach (var option in options)
        {
            builder.Append(option).Append(": remove\r\n");
        }
        builder.Append("X-Keep: yes\r\n\r\n");

        var request = Parse(builder.ToString());
        var outbound = Encoding.Latin1.GetString(request.BuildOriginHeader("/overflow", "example.test"));
        foreach (var option in options)
        {
            if (outbound.Contains(option + ":", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Overflow Connection option '{option}' was forwarded.");
            }
        }

        if (!outbound.Contains("X-Keep: yes\r\n", StringComparison.Ordinal) ||
            !outbound.Contains("Host: example.test\r\n", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Overflow Connection filtering removed an unrelated header or generated Host.");
        }
    }

    private static async Task InvalidConnectionOptionsDoNotRouteAsync()
    {
        using var timeout = new CancellationTokenSource(Timeout);
        var factory = new CountingRejectingFactory();
        var port = ReserveLoopbackPort();
        using var proxyCancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var proxy = new ProxyServer(
            new ProxyOptions
            {
                ListenAddress = "127.0.0.1",
                ListenPort = port,
                MaxConcurrentConnections = 8,
                MaxHeaderBytes = 65536,
                ClientHeaderTimeoutSeconds = 5
            },
            factory);
        var proxyTask = proxy.RunAsync(proxyCancellation.Token);
        await proxy.WaitUntilListeningAsync(timeout.Token);

        try
        {
            foreach (var value in new[] { "X-Hop;bad", ",X-Hop", "X-Hop,", "X-Hop,,X-Two", "X-Hop\u00a0" })
            {
                using var client = new TcpClient { NoDelay = true };
                await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
                await using var stream = client.GetStream();
                var raw = Encoding.Latin1.GetBytes(
                    "GET http://example.test/ HTTP/1.1\r\n" +
                    "Host: example.test\r\n" +
                    $"Connection: {value}\r\n" +
                    "X-Hop: secret\r\n\r\n");
                await stream.WriteAsync(raw, timeout.Token);
                var response = Encoding.Latin1.GetString(await ReadToEndAsync(stream, timeout.Token));
                if (!response.StartsWith("HTTP/1.1 400 Bad Request\r\n", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"Malformed Connection value '{value}' was not rejected as 400: {response}");
                }
            }

            if (factory.ConnectCount != 0)
            {
                throw new InvalidOperationException(
                    $"Malformed Connection options opened {factory.ConnectCount} outbound connection(s).");
            }
        }
        finally
        {
            proxyCancellation.Cancel();
            await proxyTask;
        }
    }

    private static ProxyServer.ParsedProxyRequest Parse(string request) =>
        ProxyServer.ParsedProxyRequest.Parse(Encoding.Latin1.GetBytes(request));

    private static void AssertParseRejected(string request)
    {
        try
        {
            _ = Parse(request);
        }
        catch (InvalidDataException)
        {
            return;
        }

        throw new InvalidOperationException("Malformed Connection option was accepted by the parser.");
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
            throw new InvalidOperationException("Malformed Connection options must be rejected before outbound connect.");
        }
    }
}
