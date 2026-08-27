using System.Net;
using System.Net.Sockets;
using System.Text;
using ProxyToAnyConnect.Configuration;
using ProxyToAnyConnect.Network;
using ProxyToAnyConnect.Proxy;

namespace ProxyToAnyConnect.SelfTests;

internal static class ProxyHttpRequestLineSelfTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public static async Task<int> RunAsync()
    {
        try
        {
            ParserRejectsInvalidMethodsAndVersions();
            ParserAcceptsSupportedHttpVersions();
            await InvalidAbsoluteTargetAndVersionDoNotRouteAsync();
            Console.WriteLine("PASS: HTTP request-line and absolute-target normalization is fail-closed");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: HTTP request-line normalization regression: {ex}");
            return 1;
        }
    }

    private static void ParserRejectsInvalidMethodsAndVersions()
    {
        AssertParseRejected("G/ET http://example.test/ HTTP/1.1\r\nHost: example.test\r\n\r\n");
        AssertParseRejected("GE\tT http://example.test/ HTTP/1.1\r\nHost: example.test\r\n\r\n");
        AssertParseRejected("GET http://example.test/ HTTP/2.0\r\nHost: example.test\r\n\r\n");
        AssertParseRejected("GET http://example.test/ http/1.1\r\nHost: example.test\r\n\r\n");
        AssertParseRejected("CONNECT example.test:443 HTTP/9.9\r\n\r\n");
        AssertParseRejected("GET  http://example.test/ HTTP/1.1\r\nHost: example.test\r\n\r\n");
        AssertParseRejected("GET http://example.test/  HTTP/1.1\r\nHost: example.test\r\n\r\n");
        AssertParseRejected(" GET http://example.test/ HTTP/1.1\r\nHost: example.test\r\n\r\n");
        AssertParseRejected("GET http://example.test/ HTTP/1.1 \r\nHost: example.test\r\n\r\n");
        AssertParseRejected("GET\thttp://example.test/\tHTTP/1.1\r\nHost: example.test\r\n\r\n");
        AssertParseRejected("GET\vhttp://example.test/\vHTTP/1.1\r\nHost: example.test\r\n\r\n");
        AssertParseRejected("GET\fhttp://example.test/\fHTTP/1.1\r\nHost: example.test\r\n\r\n");
        AssertParseRejected("GET\rhttp://example.test/\rHTTP/1.1\r\nHost: example.test\r\n\r\n");
        AssertParseRejected("CONNECT  example.test:443 HTTP/1.1\r\n\r\n");
    }

    private static void ParserAcceptsSupportedHttpVersions()
    {
        foreach (var version in new[] { "HTTP/1.0", "HTTP/1.1" })
        {
            var plain = Parse($"GET http://example.test/path?q=1 {version}\r\nHost: example.test\r\n\r\n");
            if (!plain.Version.Equals(version, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Supported version {version} was not retained.");
            }

            var connect = Parse($"CONNECT example.test:443 {version}\r\n\r\n");
            if (!connect.Version.Equals(version, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Supported CONNECT version {version} was not retained.");
            }
        }
    }

    private static async Task InvalidAbsoluteTargetAndVersionDoNotRouteAsync()
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
            await AssertBadRequestAsync(port,
                "GET http://example.test/path#fragment HTTP/1.1\r\nHost: example.test\r\n\r\n",
                timeout.Token);
            await AssertBadRequestAsync(port,
                "GET http://user:pass@example.test/path HTTP/1.1\r\nHost: example.test\r\n\r\n",
                timeout.Token);
            await AssertBadRequestAsync(port,
                "GET http://example.test/path HTTP/2.0\r\nHost: example.test\r\n\r\n",
                timeout.Token);
            await AssertBadRequestAsync(port,
                "GET  http://example.test/path HTTP/1.1\r\nHost: example.test\r\n\r\n",
                timeout.Token);
            await AssertBadRequestAsync(port,
                "GET\thttp://example.test/path\tHTTP/1.1\r\nHost: example.test\r\n\r\n",
                timeout.Token);
            await AssertBadRequestAsync(port,
                "CONNECT  example.test:443 HTTP/1.1\r\n\r\n",
                timeout.Token);

            if (factory.ConnectCount != 0)
            {
                throw new InvalidOperationException(
                    $"Invalid request-line/target forms opened {factory.ConnectCount} outbound connection(s).");
            }
        }
        finally
        {
            proxyCancellation.Cancel();
            await proxyTask;
        }
    }

    private static async Task AssertBadRequestAsync(
        int proxyPort,
        string request,
        CancellationToken cancellationToken)
    {
        using var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync(IPAddress.Loopback, proxyPort, cancellationToken);
        await using var stream = client.GetStream();
        await stream.WriteAsync(Encoding.Latin1.GetBytes(request), cancellationToken);
        var response = Encoding.Latin1.GetString(await ReadToEndAsync(stream, cancellationToken));
        if (!response.StartsWith("HTTP/1.1 400 Bad Request\r\n", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Invalid request was not rejected as 400:\n{response}");
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

        throw new InvalidOperationException($"Invalid request line was accepted: {request.Split("\r\n")[0]}");
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
            throw new InvalidOperationException("Invalid request-line/target must be rejected before outbound connect.");
        }
    }
}
