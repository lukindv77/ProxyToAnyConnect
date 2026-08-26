using System.Net;
using System.Net.Sockets;
using System.Text;
using ProxyToAnyConnect.Configuration;
using ProxyToAnyConnect.Network;
using ProxyToAnyConnect.Proxy;

namespace ProxyToAnyConnect.SelfTests;

internal static class ProxyShutdownDrainSelfTests
{
    public static async Task<int> RunAsync()
    {
        try
        {
            await ShutdownWaitsForAcceptedSessionCleanupAsync();
            Console.WriteLine("PASS: proxy RunAsync drains accepted sessions before returning");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: proxy shutdown drain regression: {ex}");
            return 1;
        }
    }

    private static async Task ShutdownWaitsForAcceptedSessionCleanupAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var originListener = new TcpListener(IPAddress.Loopback, 0);
        originListener.Start();
        var originPort = ((IPEndPoint)originListener.LocalEndpoint).Port;

        var factory = new DisposalTrackingOutboundFactory(originPort);
        var proxyPort = ReserveLoopbackPort();
        using var proxyCancellation = new CancellationTokenSource();
        var proxy = new ProxyServer(
            new ProxyOptions
            {
                ListenAddress = "127.0.0.1",
                ListenPort = proxyPort,
                MaxConcurrentConnections = 1,
                MaxHeaderBytes = 8192
            },
            factory);
        var proxyTask = proxy.RunAsync(proxyCancellation.Token);

        using var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync(IPAddress.Loopback, proxyPort, timeout.Token);
        await using var clientStream = client.GetStream();

        var request = Encoding.ASCII.GetBytes(
            "CONNECT drain.test:443 HTTP/1.1\r\nHost: drain.test:443\r\n\r\n");
        await clientStream.WriteAsync(request, timeout.Token);

        using var origin = await originListener.AcceptTcpClientAsync(timeout.Token);
        await ReadHeaderAsync(clientStream, timeout.Token);

        if (factory.ConnectionDisposed.Task.IsCompleted)
        {
            throw new InvalidOperationException("Outbound session disposed before proxy shutdown was requested.");
        }

        proxyCancellation.Cancel();
        await proxyTask.WaitAsync(timeout.Token);

        if (!factory.ConnectionDisposed.Task.IsCompletedSuccessfully)
        {
            throw new InvalidOperationException(
                "Proxy RunAsync returned before the accepted outbound session completed disposal.");
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

    private static async Task ReadHeaderAsync(Stream stream, CancellationToken cancellationToken)
    {
        var window = new byte[4];
        var count = 0;
        var one = new byte[1];
        while (count < 8192)
        {
            var read = await stream.ReadAsync(one, cancellationToken);
            if (read == 0)
            {
                throw new IOException("CONNECT response ended before header completion.");
            }

            window[count % 4] = one[0];
            count++;
            if (count >= 4 &&
                window[(count - 4) % 4] == (byte)'\r' &&
                window[(count - 3) % 4] == (byte)'\n' &&
                window[(count - 2) % 4] == (byte)'\r' &&
                window[(count - 1) % 4] == (byte)'\n')
            {
                return;
            }
        }

        throw new IOException("CONNECT response header exceeded the test limit.");
    }

    private sealed class DisposalTrackingOutboundFactory : IProxyOutboundConnectionFactory
    {
        private readonly int _originPort;

        public DisposalTrackingOutboundFactory(int originPort)
        {
            _originPort = originPort;
        }

        public TaskCompletionSource ConnectionDisposed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<IProxyOutboundConnection> ConnectAsync(
            string host,
            int port,
            CancellationToken cancellationToken)
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true
            };

            try
            {
                await socket.ConnectAsync(
                    new IPEndPoint(IPAddress.Loopback, _originPort),
                    cancellationToken);
                return new DisposalTrackingConnection(socket, ConnectionDisposed);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
    }

    private sealed class DisposalTrackingConnection : IProxyOutboundConnection
    {
        private readonly TaskCompletionSource _disposed;
        private int _disposeStarted;

        public DisposalTrackingConnection(Socket socket, TaskCompletionSource disposed)
        {
            Socket = socket;
            _disposed = disposed;
        }

        public Socket Socket { get; }
        public CancellationToken LifetimeToken => CancellationToken.None;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposeStarted, 1) == 0)
            {
                Socket.Dispose();
                _disposed.TrySetResult();
            }

            return ValueTask.CompletedTask;
        }
    }
}
