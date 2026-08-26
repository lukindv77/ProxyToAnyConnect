using System.Net;
using System.Net.Sockets;
using System.Text;
using ProxyToAnyConnect.Configuration;
using ProxyToAnyConnect.Network;
using ProxyToAnyConnect.Proxy;

namespace ProxyToAnyConnect.SelfTests;

internal static class ProxyLifecycleStressSelfTests
{
    private const int Cycles = 250;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(45);

    public static async Task<int> RunAsync()
    {
        try
        {
            var weakServers = await RunCyclesAsync();

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var retained = weakServers.Count(reference => reference.IsAlive);

            // Async/JIT state machines are permitted to retain the most recently
            // awaited completed object until this method itself returns. What this
            // regression guards against is retention that grows with cycle count.
            if (retained > 1)
            {
                throw new InvalidOperationException(
                    $"{retained} of {weakServers.Length} stopped ProxyServer instances remained strongly reachable; expected at most one fixed async/JIT root.");
            }

            Console.WriteLine(
                $"PASS: {Cycles} proxy listener/session start-stop cycles have bounded retention ({retained} final async/JIT root)");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: proxy lifecycle stress regression: {ex}");
            return 1;
        }
    }

    private static async Task<WeakReference[]> RunCyclesAsync()
    {
        using var timeout = new CancellationTokenSource(Timeout);
        var weakServers = new WeakReference[Cycles];

        for (var cycle = 0; cycle < Cycles; cycle++)
        {
            weakServers[cycle] = await RunSingleCycleAsync(timeout.Token);
        }

        return weakServers;
    }

    private static async Task<WeakReference> RunSingleCycleAsync(CancellationToken cancellationToken)
    {
        using var originListener = new TcpListener(IPAddress.Loopback, 0);
        originListener.Start();
        var originPort = ((IPEndPoint)originListener.LocalEndpoint).Port;

        var proxyPort = ReserveLoopbackPort();
        using var proxyCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var proxy = new ProxyServer(
            new ProxyOptions
            {
                ListenAddress = "127.0.0.1",
                ListenPort = proxyPort,
                MaxConcurrentConnections = 4,
                MaxHeaderBytes = 8192
            },
            new LoopbackOutboundFactory(originPort));
        var weak = new WeakReference(proxy);
        var proxyTask = proxy.RunAsync(proxyCancellation.Token);

        try
        {
            await proxy.WaitUntilListeningAsync(cancellationToken);

            using var client = new TcpClient { NoDelay = true };
            await client.ConnectAsync(IPAddress.Loopback, proxyPort, cancellationToken);
            await using var clientStream = client.GetStream();

            var request = Encoding.ASCII.GetBytes(
                "CONNECT lifecycle.test:443 HTTP/1.1\r\nHost: lifecycle.test:443\r\n\r\n");
            await clientStream.WriteAsync(request, cancellationToken);

            using var origin = await originListener.AcceptTcpClientAsync(cancellationToken);
            await using var originStream = origin.GetStream();

            await ReadHeaderAsync(clientStream, cancellationToken);

            byte[] payload = [1, 2, 3, 4, 5, 6, 7, 8];
            await clientStream.WriteAsync(payload, cancellationToken);
            var received = new byte[payload.Length];
            await ReadExactlyAsync(originStream, received, cancellationToken);
            if (!payload.AsSpan().SequenceEqual(received))
            {
                throw new InvalidOperationException("Lifecycle stress payload was corrupted.");
            }
        }
        finally
        {
            proxyCancellation.Cancel();
            await proxyTask;
        }

        return weak;
    }

    private static async Task ReadHeaderAsync(Stream stream, CancellationToken cancellationToken)
    {
        var last = new byte[4];
        var count = 0;
        var one = new byte[1];
        while (count < 8192)
        {
            var read = await stream.ReadAsync(one, cancellationToken);
            if (read == 0)
            {
                throw new IOException("CONNECT response ended before header completion.");
            }

            last[count % 4] = one[0];
            count++;
            if (count >= 4 &&
                last[(count - 4) % 4] == (byte)'\r' &&
                last[(count - 3) % 4] == (byte)'\n' &&
                last[(count - 2) % 4] == (byte)'\r' &&
                last[(count - 1) % 4] == (byte)'\n')
            {
                return;
            }
        }

        throw new IOException("CONNECT response header exceeded stress-test limit.");
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
                throw new IOException("Stream ended before expected lifecycle payload was received.");
            }

            offset += read;
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

    private sealed class LoopbackOutboundFactory : IProxyOutboundConnectionFactory
    {
        private readonly int _originPort;

        public LoopbackOutboundFactory(int originPort)
        {
            _originPort = originPort;
        }

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
                return new LoopbackConnection(socket);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
    }

    private sealed class LoopbackConnection : IProxyOutboundConnection
    {
        public LoopbackConnection(Socket socket)
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
