using System.Net;
using System.Net.Sockets;
using System.Text;
using ProxyToAnyConnect.Configuration;
using ProxyToAnyConnect.Network;
using ProxyToAnyConnect.Proxy;

namespace ProxyToAnyConnect.SelfTests;

internal static class ProxyConnectPortGrammarSelfTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public static async Task<int> RunAsync()
    {
        try
        {
            HelperGrammarIsAsciiDecimalOnly();
            await MalformedPortsDoNotRouteAsync();
            Console.WriteLine("PASS: CONNECT port grammar accepts ASCII decimal only before outbound routing");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: CONNECT port grammar regression: {ex}");
            return 1;
        }
    }

    private static void HelperGrammarIsAsciiDecimalOnly()
    {
        AssertAccepted("example.test:1", 1);
        AssertAccepted("example.test:443", 443);
        AssertAccepted("example.test:00443", 443);
        AssertAccepted("example.test:65535", 65535);

        foreach (var authority in new[]
        {
            "example.test:",
            "example.test:+443",
            "example.test:-443",
            "example.test: 443",
            "example.test:443 ",
            "example.test:\t443",
            "example.test:443\t",
            "example.test:\u00a0443",
            "example.test:٤٤٣",
            "example.test:０４４３",
            "example.test:0",
            "example.test:65536",
            "example.test:999999999999999999999999999"
        })
        {
            AssertRejected(authority);
        }
    }

    private static async Task MalformedPortsDoNotRouteAsync()
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
            foreach (var authority in new[]
            {
                "example.test:+443",
                "example.test:\t443",
                "example.test:443\t"
            })
            {
                using var client = new TcpClient { NoDelay = true };
                await client.ConnectAsync(IPAddress.Loopback, proxyPort, timeout.Token);
                await using var stream = client.GetStream();
                var request = Encoding.Latin1.GetBytes(
                    $"CONNECT {authority} HTTP/1.1\r\nHost: example.test\r\n\r\n");
                await stream.WriteAsync(request, timeout.Token);
                var response = Encoding.Latin1.GetString(
                    await ReadToEndAsync(stream, timeout.Token));
                if (!response.StartsWith("HTTP/1.1 400 Bad Request\r\n", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Malformed CONNECT port '{authority}' was not rejected as 400:\n{response}");
                }
            }

            if (factory.ConnectCount != 0)
            {
                throw new InvalidOperationException(
                    $"Malformed CONNECT ports opened {factory.ConnectCount} outbound connection(s).");
            }
        }
        finally
        {
            proxyCancellation.Cancel();
            await proxyTask;
        }
    }

    private static void AssertAccepted(string authority, int expectedPort)
    {
        var (host, port) = ProxyServer.ParseAuthority(authority, 443);
        if (!host.Equals("example.test", StringComparison.Ordinal) || port != expectedPort)
        {
            throw new InvalidOperationException(
                $"CONNECT authority '{authority}' parsed as {host}:{port}, expected example.test:{expectedPort}.");
        }
    }

    private static void AssertRejected(string authority)
    {
        try
        {
            _ = ProxyServer.ParseAuthority(authority, 443);
        }
        catch (InvalidDataException)
        {
            return;
        }

        throw new InvalidOperationException(
            $"CONNECT authority accepted non-ASCII/non-decimal port grammar: '{authority}'.");
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
            throw new InvalidOperationException(
                $"Malformed CONNECT port reached outbound routing as {host}:{port}.");
        }
    }
}
