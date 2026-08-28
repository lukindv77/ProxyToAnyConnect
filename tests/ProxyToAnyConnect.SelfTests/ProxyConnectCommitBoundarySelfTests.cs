using System.Net;
using System.Net.Sockets;
using System.Text;
using ProxyToAnyConnect.Configuration;
using ProxyToAnyConnect.Network;
using ProxyToAnyConnect.Proxy;

namespace ProxyToAnyConnect.SelfTests;

internal static class ProxyConnectCommitBoundarySelfTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(8);

    public static async Task<int> RunAsync()
    {
        try
        {
            await CommittedRemainderFailureClosesWithoutSecondHttpResponseAsync();
            await PreEstablishmentVpnFailureStillReturns503Async();
            Console.WriteLine(
                "PASS: CONNECT response commit boundary suppresses post-200 HTTP errors and preserves pre-establishment failures");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: CONNECT response commit-boundary regression: {ex}");
            return 1;
        }
    }

    private static async Task CommittedRemainderFailureClosesWithoutSecondHttpResponseAsync()
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        using var originListener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        originListener.Start();
        var originPort = ((IPEndPoint)originListener.LocalEndpoint).Port;

        var proxyPort = ReserveLoopbackPort();
        var factory = new SendShutdownOutboundFactory(originPort);
        using var proxyCancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var proxy = new ProxyServer(CreateProxyOptions(proxyPort), factory);
        var proxyTask = proxy.RunAsync(proxyCancellation.Token);

        try
        {
            using var client = new System.Net.Sockets.TcpClient { NoDelay = true };
            await client.ConnectAsync(IPAddress.Loopback, proxyPort, timeout.Token);
            await using var clientStream = client.GetStream();

            var header = Encoding.ASCII.GetBytes(
                "CONNECT commit-boundary.test:443 HTTP/1.1\r\n" +
                "Host: commit-boundary.test:443\r\n\r\n");
            var remainder = GC.AllocateUninitializedArray<byte>(512);
            remainder.AsSpan().Fill(0xA5);
            var request = GC.AllocateUninitializedArray<byte>(header.Length + remainder.Length);
            header.CopyTo(request, 0);
            remainder.CopyTo(request, header.Length);

            // One loopback send keeps the header terminator and first tunnel bytes
            // queued together for the proxy's bounded request read. The outbound
            // factory locally shuts down its send half before publication, so any
            // captured remainder write fails deterministically after the 200 response.
            await clientStream.WriteAsync(request, timeout.Token);

            using var origin = await originListener.AcceptTcpClientAsync(timeout.Token);
            var responseHeader = await ReadHeaderAsync(clientStream, timeout.Token);
            var responseText = Encoding.ASCII.GetString(responseHeader);
            if (!responseText.StartsWith(
                    "HTTP/1.1 200 Connection Established\r\n\r\n",
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"CONNECT did not commit the expected success response: {responseText}");
            }

            var tail = await ReadUntilCloseAsync(clientStream, timeout.Token);
            if (tail.Length != 0)
            {
                var tailText = Encoding.ASCII.GetString(tail);
                if (tailText.Contains("HTTP/1.1 ", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Proxy injected a second HTTP response after CONNECT establishment: {tailText}");
                }

                throw new InvalidOperationException(
                    $"Expected transport-close after committed remainder failure, received {tail.Length} unexpected byte(s).");
            }

            if (factory.ConnectCount != 1)
            {
                throw new InvalidOperationException(
                    $"Expected one outbound CONNECT attempt, got {factory.ConnectCount}.");
            }
        }
        finally
        {
            proxyCancellation.Cancel();
            await proxyTask;
        }
    }

    private static async Task PreEstablishmentVpnFailureStillReturns503Async()
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        var proxyPort = ReserveLoopbackPort();
        using var proxyCancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var proxy = new ProxyServer(CreateProxyOptions(proxyPort), new VpnUnavailableOutboundFactory());
        var proxyTask = proxy.RunAsync(proxyCancellation.Token);

        try
        {
            using var client = new System.Net.Sockets.TcpClient { NoDelay = true };
            await client.ConnectAsync(IPAddress.Loopback, proxyPort, timeout.Token);
            await using var stream = client.GetStream();
            await stream.WriteAsync(
                "CONNECT unavailable.test:443 HTTP/1.1\r\nHost: unavailable.test:443\r\n\r\n"u8.ToArray(),
                timeout.Token);

            var responseHeader = Encoding.ASCII.GetString(await ReadHeaderAsync(stream, timeout.Token));
            if (!responseHeader.StartsWith("HTTP/1.1 503 L2TP VPN unavailable\r\n", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Pre-establishment VPN failure no longer maps to HTTP 503: {responseHeader}");
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
            Id = "connect-commit-selftest",
            Name = "CONNECT commit self-test",
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
        var bytes = new List<byte>(128);
        var one = new byte[1];
        while (bytes.Count < 8192)
        {
            var read = await stream.ReadAsync(one, cancellationToken);
            if (read == 0)
            {
                throw new IOException("Connection closed before proxy response header completed.");
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

        throw new InvalidDataException("Proxy response header exceeded the test limit.");
    }

    private static async Task<byte[]> ReadUntilCloseAsync(Stream stream, CancellationToken cancellationToken)
    {
        var result = new List<byte>(512);
        var buffer = new byte[512];
        while (result.Count < 8192)
        {
            try
            {
                var read = await stream.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    return result.ToArray();
                }

                result.AddRange(buffer.AsSpan(0, read).ToArray());
            }
            catch (IOException ex) when (ex.InnerException is SocketException)
            {
                return result.ToArray();
            }
        }

        throw new InvalidOperationException("Committed CONNECT failure produced an unexpectedly large response tail.");
    }

    private static int ReserveLoopbackPort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
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

    private sealed class SendShutdownOutboundFactory : IProxyOutboundConnectionFactory
    {
        private readonly int _originPort;
        private int _connectCount;

        public SendShutdownOutboundFactory(int originPort)
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
                socket.Shutdown(SocketShutdown.Send);
                return new TestOutboundConnection(socket);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
    }

    private sealed class VpnUnavailableOutboundFactory : IProxyOutboundConnectionFactory
    {
        public Task<IProxyOutboundConnection> ConnectAsync(
            string host,
            int port,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromException<IProxyOutboundConnection>(
                new VpnUnavailableException("Synthetic pre-establishment VPN failure."));
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
