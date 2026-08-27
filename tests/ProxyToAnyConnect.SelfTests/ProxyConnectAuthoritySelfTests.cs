using System.Net;
using System.Net.Sockets;
using System.Text;
using ProxyToAnyConnect.Configuration;
using ProxyToAnyConnect.Network;
using ProxyToAnyConnect.Proxy;

namespace ProxyToAnyConnect.SelfTests;

internal static class ProxyConnectAuthoritySelfTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public static async Task<int> RunAsync()
    {
        try
        {
            AcceptedAuthoritiesAreCanonicalized();
            MalformedAuthoritiesAreRejected();
            await MalformedConnectDoesNotRouteAsync();
            Console.WriteLine("PASS: CONNECT authority grammar is strict and rejected before outbound routing");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: CONNECT authority grammar regression: {ex}");
            return 1;
        }
    }

    private static void AcceptedAuthoritiesAreCanonicalized()
    {
        AssertAuthority("example.test", "example.test", 443);
        AssertAuthority("EXAMPLE.TEST:8443", "example.test", 8443);
        AssertAuthority("127.0.0.1:9443", "127.0.0.1", 9443);
        AssertAuthority("münich.example:443", "xn--mnich-kva.example", 443);
    }

    private static void MalformedAuthoritiesAreRejected()
    {
        var longLabel = new string('a', 64) + ".example";
        var longName = string.Join('.', Enumerable.Repeat(new string('a', 63), 4));
        foreach (var authority in new[]
        {
            "",
            " ",
            ":443",
            "example.test:0",
            "example.test:65536",
            "example.test:notaport",
            "user@example.test:443",
            "example.test/path:443",
            "example.test?x:443",
            "example.test#x:443",
            "example.test\\x:443",
            "example..test:443",
            ".example.test:443",
            "example.test.:443",
            "-example.test:443",
            "example-.test:443",
            "bad_host.test:443",
            "example test:443",
            "example\ttest:443",
            "example\u0000test:443",
            "[2001:db8::1]:443",
            "2001:db8::1:443",
            longLabel + ":443",
            longName + ":443"
        })
        {
            AssertAuthorityRejected(authority);
        }
    }

    private static async Task MalformedConnectDoesNotRouteAsync()
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
            foreach (var authority in new[]
            {
                "user@example.test:443",
                "example.test/path:443",
                "example.test?x:443",
                "example.test#x:443",
                "example.test\\x:443",
                "example..test:443",
                "bad_host.test:443",
                "2001:db8::1:443"
            })
            {
                await AssertBadRequestAsync(port, authority, timeout.Token);
            }

            if (factory.ConnectCount != 0)
            {
                throw new InvalidOperationException(
                    $"Malformed CONNECT authority opened {factory.ConnectCount} outbound connection(s).");
            }
        }
        finally
        {
            proxyCancellation.Cancel();
            await proxyTask;
        }
    }

    private static void AssertAuthority(string authority, string expectedHost, int expectedPort)
    {
        var (host, port) = ProxyServer.ParseAuthority(authority, 443);
        if (!host.Equals(expectedHost, StringComparison.Ordinal) || port != expectedPort)
        {
            throw new InvalidOperationException(
                $"Authority '{authority}' normalized to '{host}:{port}', expected '{expectedHost}:{expectedPort}'.");
        }
    }

    private static void AssertAuthorityRejected(string authority)
    {
        try
        {
            _ = ProxyServer.ParseAuthority(authority, 443);
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
        {
            return;
        }

        throw new InvalidOperationException($"Malformed CONNECT authority was accepted: '{authority}'.");
    }

    private static async Task AssertBadRequestAsync(
        int proxyPort,
        string authority,
        CancellationToken cancellationToken)
    {
        using var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync(IPAddress.Loopback, proxyPort, cancellationToken);
        await using var stream = client.GetStream();
        var request = Encoding.Latin1.GetBytes(
            $"CONNECT {authority} HTTP/1.1\r\nHost: {authority}\r\n\r\n");
        await stream.WriteAsync(request, cancellationToken);
        var response = Encoding.Latin1.GetString(await ReadToEndAsync(stream, cancellationToken));
        if (!response.StartsWith("HTTP/1.1 400 Bad Request\r\n", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Malformed CONNECT authority '{authority}' was not rejected as 400:\n{response}");
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
            throw new InvalidOperationException("Malformed CONNECT authority must be rejected before outbound connect.");
        }
    }
}
