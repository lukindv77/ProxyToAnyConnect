using System.Net;
using System.Net.Sockets;
using System.Text;
using ProxyToAnyConnect.Configuration;
using ProxyToAnyConnect.Network;
using ProxyToAnyConnect.Proxy;

namespace ProxyToAnyConnect.SelfTests;

internal static class ProxyHttpResponseCommitBoundarySelfTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    public static async Task<int> RunAsync()
    {
        try
        {
            await NoBodyCommittedOriginResetDoesNotAppendProxyErrorAsync();
            await EarlyResponseCommittedOriginResetDoesNotAppendProxyErrorAsync();
            await OriginResetBeforeAnyResponseStillReturns502Async();
            Console.WriteLine(
                "PASS: plain HTTP response commitment suppresses post-origin proxy errors while preserving pre-response 502 mapping");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: plain HTTP response commit-boundary regression: {ex}");
            return 1;
        }
    }

    private static Task NoBodyCommittedOriginResetDoesNotAppendProxyErrorAsync() =>
        RunCommittedResetCaseAsync(
            "GET http://commit.test/data HTTP/1.1\r\nHost: commit.test\r\n\r\n",
            sendCompleteBody: true);

    private static Task EarlyResponseCommittedOriginResetDoesNotAppendProxyErrorAsync() =>
        RunCommittedResetCaseAsync(
            "POST http://commit.test/upload HTTP/1.1\r\n" +
            "Host: commit.test\r\n" +
            "Content-Length: 4096\r\n\r\nX",
            sendCompleteBody: false);

    private static async Task RunCommittedResetCaseAsync(string request, bool sendCompleteBody)
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        using var originListener = new TcpListener(IPAddress.Loopback, 0);
        originListener.Start();
        var originPort = ((IPEndPoint)originListener.LocalEndpoint).Port;

        var factory = new LoopbackOutboundFactory(originPort);
        var proxyPort = ReserveLoopbackPort();
        using var proxyCancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var proxy = new ProxyServer(CreateProxyOptions(proxyPort), factory);
        var proxyTask = proxy.RunAsync(proxyCancellation.Token);

        try
        {
            using var client = new TcpClient { NoDelay = true };
            await client.ConnectAsync(IPAddress.Loopback, proxyPort, timeout.Token);
            await using var clientStream = client.GetStream();
            await clientStream.WriteAsync(Encoding.ASCII.GetBytes(request), timeout.Token);

            using var origin = await originListener.AcceptTcpClientAsync(timeout.Token);
            origin.NoDelay = true;
            await using var originStream = origin.GetStream();
            _ = await ReadHeaderAsync(originStream, timeout.Token);

            if (!sendCompleteBody)
            {
                // Leave the declared upload incomplete. This keeps the client->origin
                // body task pending so the origin response/reset wins the early-response
                // branch after its first bytes have already reached the client.
            }

            var prefix = Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\n" +
                "Content-Length: 128\r\n" +
                "Connection: close\r\n\r\n" +
                "ORIGIN-PREFIX");
            await originStream.WriteAsync(prefix, timeout.Token);

            // Do not reset the origin until the client has received every committed
            // prefix byte through the proxy. This removes packetization/scheduling
            // ambiguity from the post-commit failure boundary.
            var observedPrefix = new byte[prefix.Length];
            await ReadExactlyAsync(clientStream, observedPrefix, timeout.Token);
            if (!observedPrefix.AsSpan().SequenceEqual(prefix))
            {
                throw new InvalidOperationException("Client did not receive the exact committed origin prefix.");
            }

            origin.Client.LingerState = new LingerOption(enable: true, seconds: 0);
            origin.Close();

            var tail = await ReadUntilClosedAsync(clientStream, timeout.Token);
            var tailText = Encoding.Latin1.GetString(tail);
            if (tailText.Contains("HTTP/1.1 502", StringComparison.Ordinal) ||
                tailText.Contains("HTTP/1.1 500", StringComparison.Ordinal) ||
                tailText.Contains("Bad Gateway", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Proxy appended its own HTTP failure after committed origin bytes: {tailText}");
            }

            if (factory.ConnectCount != 1)
            {
                throw new InvalidOperationException(
                    $"Expected one origin connection, got {factory.ConnectCount}.");
            }
        }
        finally
        {
            proxyCancellation.Cancel();
            await proxyTask;
        }
    }

    private static async Task OriginResetBeforeAnyResponseStillReturns502Async()
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        using var originListener = new TcpListener(IPAddress.Loopback, 0);
        originListener.Start();
        var originPort = ((IPEndPoint)originListener.LocalEndpoint).Port;

        var proxyPort = ReserveLoopbackPort();
        using var proxyCancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var proxy = new ProxyServer(CreateProxyOptions(proxyPort), new LoopbackOutboundFactory(originPort));
        var proxyTask = proxy.RunAsync(proxyCancellation.Token);

        try
        {
            using var client = new TcpClient { NoDelay = true };
            await client.ConnectAsync(IPAddress.Loopback, proxyPort, timeout.Token);
            await using var clientStream = client.GetStream();
            await clientStream.WriteAsync(
                "GET http://precommit.test/fail HTTP/1.1\r\nHost: precommit.test\r\n\r\n"u8.ToArray(),
                timeout.Token);

            using var origin = await originListener.AcceptTcpClientAsync(timeout.Token);
            origin.NoDelay = true;
            await using var originStream = origin.GetStream();
            _ = await ReadHeaderAsync(originStream, timeout.Token);

            origin.Client.LingerState = new LingerOption(enable: true, seconds: 0);
            origin.Close();

            var responseHeader = Encoding.ASCII.GetString(await ReadHeaderAsync(clientStream, timeout.Token));
            if (!responseHeader.StartsWith("HTTP/1.1 502 Bad Gateway\r\n", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Origin failure before response commitment no longer maps to HTTP 502: {responseHeader}");
            }
        }
        finally
        {
            proxyCancellation.Cancel();
            await proxyTask;
        }
    }

    private static ProxyOptions CreateProxyOptions(int port) =>
        new()
        {
            Id = "http-response-commit-selftest",
            Name = "HTTP response commit self-test",
            ListenAddress = "127.0.0.1",
            ListenPort = port,
            MaxConcurrentConnections = 4,
            MaxHeaderBytes = 8192,
            ClientHeaderTimeoutSeconds = 5,
            OutboundConnectTimeoutSeconds = 5,
            DnsTimeoutMilliseconds = 1000
        };

    private static async Task<byte[]> ReadHeaderAsync(Stream stream, CancellationToken cancellationToken)
    {
        var bytes = new List<byte>(256);
        var one = new byte[1];
        while (bytes.Count < 8192)
        {
            var read = await stream.ReadAsync(one, cancellationToken);
            if (read == 0)
            {
                throw new IOException("Connection closed before the HTTP header completed.");
            }

            bytes.Add(one[0]);
            var count = bytes.Count;
            if (count >= 4 &&
                bytes[count - 4] == (byte)'\r' &&
                bytes[count - 3] == (byte)'\n' &&
                bytes[count - 2] == (byte)'\r' &&
                bytes[count - 1] == (byte)'\n')
            {
                return bytes.ToArray();
            }
        }

        throw new InvalidDataException("HTTP header exceeded the self-test limit.");
    }

    private static async Task ReadExactlyAsync(
        Stream stream,
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < destination.Length)
        {
            var read = await stream.ReadAsync(destination[offset..], cancellationToken);
            if (read == 0)
            {
                throw new IOException(
                    $"Connection closed after {offset} of {destination.Length} expected committed byte(s).");
            }

            offset += read;
        }
    }

    private static async Task<byte[]> ReadUntilClosedAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var bytes = new List<byte>(256);
        var buffer = new byte[512];
        while (bytes.Count < 8192)
        {
            try
            {
                var read = await stream.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    return bytes.ToArray();
                }

                for (var index = 0; index < read; index++)
                {
                    bytes.Add(buffer[index]);
                }
            }
            catch (IOException ex) when (ex.InnerException is SocketException)
            {
                return bytes.ToArray();
            }
        }

        throw new InvalidOperationException("Unexpectedly large response tail after origin reset.");
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

    private sealed class LoopbackOutboundFactory : IProxyOutboundConnectionFactory
    {
        private readonly int _originPort;
        private int _connectCount;

        public LoopbackOutboundFactory(int originPort)
        {
            _originPort = originPort;
        }

        public int ConnectCount => Volatile.Read(ref _connectCount);

        public async Task<IProxyOutboundConnection> ConnectAsync(
            string host,
            int port,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _connectCount);
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true
            };
            try
            {
                await socket.ConnectAsync(
                    new IPEndPoint(IPAddress.Loopback, _originPort),
                    cancellationToken);
                return new TestOutboundConnection(socket);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
    }

    private sealed class TestOutboundConnection : IProxyOutboundConnection
    {
        public TestOutboundConnection(Socket socket)
        {
            Socket = socket;
        }

        public Socket Socket { get; }
        public CancellationToken LifetimeToken => CancellationToken.None;

        public ValueTask DisposeAsync()
        {
            Socket.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
