using System.Net;
using System.Net.Sockets;
using System.Text;
using ProxyToAnyConnect.Configuration;
using ProxyToAnyConnect.Network;
using ProxyToAnyConnect.Proxy;

namespace ProxyToAnyConnect.SelfTests;

internal static class ProxyHttpHostAuthoritySelfTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    public static async Task<int> RunAsync()
    {
        var failures = 0;

        void Run(string name, Action test)
        {
            try
            {
                test();
                Console.WriteLine($"PASS: {name}");
            }
            catch (Exception ex)
            {
                failures++;
                Console.Error.WriteLine($"FAIL: {name}: {ex}");
            }
        }

        async Task RunAsyncTest(string name, Func<Task> test)
        {
            try
            {
                await test();
                Console.WriteLine($"PASS: {name}");
            }
            catch (Exception ex)
            {
                failures++;
                Console.Error.WriteLine($"FAIL: {name}: {ex}");
            }
        }

        Run("plain HTTP origin Host is regenerated from absolute target", GeneratedHostOverridesReceivedHost);
        await RunAsyncTest("plain HTTP mismatched Host cannot reach loopback origin", MismatchedHostCannotReachOriginAsync);

        return failures == 0 ? 0 : 1;
    }

    private static void GeneratedHostOverridesReceivedHost()
    {
        var raw = Encoding.ASCII.GetBytes(
            "GET http://example.com:8080/path HTTP/1.1\r\n" +
            "Host: attacker.invalid\r\n" +
            "Host: second.invalid:9999\r\n" +
            "X-Keep: yes\r\n\r\n");

        var request = ProxyServer.ParsedProxyRequest.Parse(raw);
        var authority = ProxyServer.BuildHttpHostAuthority(new Uri(request.Target, UriKind.Absolute));
        var outbound = Encoding.Latin1.GetString(request.BuildOriginHeader("/path", authority));

        AssertEqual("example.com:8080", authority, "non-default HTTP authority");
        AssertEqual(1, CountHeader(outbound, "Host"), "forwarded Host count");
        AssertContains(outbound, "\r\nHost: example.com:8080\r\n");
        AssertNotContains(outbound, "attacker.invalid");
        AssertNotContains(outbound, "second.invalid");
        AssertContains(outbound, "\r\nX-Keep: yes\r\n");

        AssertEqual(
            "example.com",
            ProxyServer.BuildHttpHostAuthority(new Uri("http://example.com/path", UriKind.Absolute)),
            "default HTTP authority");
        AssertEqual(
            "xn--bcher-kva.example:8080",
            ProxyServer.BuildHttpHostAuthority(new Uri("http://bücher.example:8080/path", UriKind.Absolute)),
            "IDN HTTP authority");
    }

    private static async Task MismatchedHostCannotReachOriginAsync()
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        using var originListener = new TcpListener(IPAddress.Loopback, 0);
        originListener.Start();
        var originPort = ((IPEndPoint)originListener.LocalEndpoint).Port;

        var factory = new LoopbackOutboundFactory(originPort);
        var proxyPort = ReserveLoopbackPort();
        using var proxyCancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var proxy = new ProxyServer(
            new ProxyOptions
            {
                ListenAddress = "127.0.0.1",
                ListenPort = proxyPort,
                MaxHeaderBytes = 65536
            },
            factory);
        var proxyTask = proxy.RunAsync(proxyCancellation.Token);
        await proxy.WaitUntilListeningAsync(timeout.Token);

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, proxyPort, timeout.Token);
            await using var clientStream = client.GetStream();

            var request = Encoding.ASCII.GetBytes(
                $"GET http://example.com:{originPort}/path?q=1 HTTP/1.1\r\n" +
                "Host: attacker.invalid\r\n" +
                "Host: second.invalid\r\n" +
                "Proxy-Authorization: Basic secret\r\n" +
                "Connection: X-Remove\r\n" +
                "X-Remove: no\r\n" +
                "X-Keep: yes\r\n\r\n");
            await clientStream.WriteAsync(request, timeout.Token);

            using var originClient = await originListener.AcceptTcpClientAsync(timeout.Token);
            await using var originStream = originClient.GetStream();
            var originHeader = Encoding.Latin1.GetString(await ReadHeaderAsync(originStream, timeout.Token));

            AssertStartsWith(originHeader, "GET /path?q=1 HTTP/1.1\r\n");
            AssertEqual(1, CountHeader(originHeader, "Host"), "loopback Host count");
            AssertContains(originHeader, $"\r\nHost: example.com:{originPort}\r\n");
            AssertNotContains(originHeader, "attacker.invalid");
            AssertNotContains(originHeader, "second.invalid");
            AssertNotContains(originHeader, "Proxy-Authorization");
            AssertNotContains(originHeader, "X-Remove:");
            AssertContains(originHeader, "\r\nX-Keep: yes\r\n");

            var response = Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nOK");
            await originStream.WriteAsync(response, timeout.Token);
            originClient.Client.Shutdown(SocketShutdown.Send);

            var clientResponse = Encoding.Latin1.GetString(await ReadToEndAsync(clientStream, timeout.Token));
            AssertContains(clientResponse, "200 OK");
            if (!clientResponse.EndsWith("OK", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Unexpected proxy response:\n{clientResponse}");
            }

            factory.AssertLastTarget("example.com", originPort);
        }
        finally
        {
            proxyCancellation.Cancel();
            await proxyTask;
        }
    }

    private static int CountHeader(string headerBlock, string headerName)
    {
        var count = 0;
        foreach (var line in headerBlock.Split("\r\n", StringSplitOptions.None))
        {
            if (line.StartsWith(headerName + ":", StringComparison.OrdinalIgnoreCase))
            {
                count++;
            }
        }

        return count;
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

    private static async Task<byte[]> ReadHeaderAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var oneByte = new byte[1];
        while (buffer.Length < 65536)
        {
            var read = await stream.ReadAsync(oneByte, cancellationToken);
            if (read == 0)
            {
                throw new IOException("Stream ended before HTTP header was complete.");
            }

            buffer.WriteByte(oneByte[0]);
            if (buffer.Length < 4)
            {
                continue;
            }

            var data = buffer.GetBuffer();
            var length = checked((int)buffer.Length);
            if (data[length - 4] == (byte)'\r' &&
                data[length - 3] == (byte)'\n' &&
                data[length - 2] == (byte)'\r' &&
                data[length - 1] == (byte)'\n')
            {
                return buffer.ToArray();
            }
        }

        throw new IOException("HTTP header exceeded test limit.");
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

    private static void AssertContains(string value, string expected)
    {
        if (!value.Contains(expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Expected text was not found: '{expected}'.\nActual:\n{value}");
        }
    }

    private static void AssertNotContains(string value, string unexpected)
    {
        if (value.Contains(unexpected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Unexpected text was found: '{unexpected}'.\nActual:\n{value}");
        }
    }

    private static void AssertStartsWith(string value, string expected)
    {
        if (!value.StartsWith(expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Expected prefix '{expected}'.\nActual:\n{value}");
        }
    }

    private static void AssertEqual<T>(T expected, T actual, string name)
        where T : IEquatable<T>
    {
        if (!actual.Equals(expected))
        {
            throw new InvalidOperationException($"Unexpected {name}. Expected '{expected}', got '{actual}'.");
        }
    }

    private sealed class LoopbackOutboundFactory : IProxyOutboundConnectionFactory
    {
        private readonly int _originPort;
        private string? _lastHost;
        private int _lastPort;

        public LoopbackOutboundFactory(int originPort) => _originPort = originPort;

        public async Task<IProxyOutboundConnection> ConnectAsync(
            string host,
            int port,
            CancellationToken cancellationToken)
        {
            _lastHost = host;
            _lastPort = port;
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true
            };

            try
            {
                await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, _originPort), cancellationToken);
                return new TestOutboundConnection(socket);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        public void AssertLastTarget(string expectedHost, int expectedPort)
        {
            if (!string.Equals(_lastHost, expectedHost, StringComparison.OrdinalIgnoreCase) ||
                _lastPort != expectedPort)
            {
                throw new InvalidOperationException(
                    $"Unexpected outbound target. Expected {expectedHost}:{expectedPort}, got {_lastHost}:{_lastPort}.");
            }
        }
    }

    private sealed class TestOutboundConnection : IProxyOutboundConnection
    {
        public TestOutboundConnection(Socket socket) => Socket = socket;

        public Socket Socket { get; }
        public CancellationToken LifetimeToken => CancellationToken.None;

        public ValueTask DisposeAsync()
        {
            Socket.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
