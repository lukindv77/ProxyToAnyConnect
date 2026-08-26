using System.Net;
using System.Net.Sockets;
using System.Text;
using ProxyToAnyConnect.Configuration;
using ProxyToAnyConnect.Network;
using ProxyToAnyConnect.Proxy;

namespace ProxyToAnyConnect.SelfTests;

internal static class ProxyLifetimeSelfTests
{
    public static async Task<int> RunAsync()
    {
        try
        {
            await ConnectTunnelClosesWhenLifetimeEndsAsync();
            Console.WriteLine("PASS: Active CONNECT tunnel closes when outbound lifetime is cancelled");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: Active CONNECT tunnel closes when outbound lifetime is cancelled: {ex}");
            return 1;
        }
    }

    private static async Task ConnectTunnelClosesWhenLifetimeEndsAsync()
    {
        using var testTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var originListener = new TcpListener(IPAddress.Loopback, 0);
        originListener.Start();
        var originPort = ((IPEndPoint)originListener.LocalEndpoint).Port;

        using var lifetime = new CancellationTokenSource();
        var factory = new LifetimeOutboundFactory(originPort, lifetime.Token);
        var proxyPort = ReserveLoopbackPort();
        using var proxyCancellation = CancellationTokenSource.CreateLinkedTokenSource(testTimeout.Token);

        var proxy = new ProxyServer(
            new ProxyOptions
            {
                ListenAddress = "127.0.0.1",
                ListenPort = proxyPort,
                MaxHeaderBytes = 65536
            },
            factory);
        var proxyTask = proxy.RunAsync(proxyCancellation.Token);

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, proxyPort, testTimeout.Token);
            await using var clientStream = client.GetStream();

            var request = Encoding.ASCII.GetBytes(
                "CONNECT example.com:443 HTTP/1.1\r\nHost: example.com:443\r\n\r\n");
            await clientStream.WriteAsync(request, testTimeout.Token);

            using var origin = await originListener.AcceptTcpClientAsync(testTimeout.Token);
            await using var originStream = origin.GetStream();

            var responseHeader = Encoding.ASCII.GetString(
                await ReadHeaderAsync(clientStream, testTimeout.Token));
            if (!responseHeader.StartsWith("HTTP/1.1 200 Connection Established", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Unexpected CONNECT response: {responseHeader}");
            }

            // Prove the tunnel is alive before simulating loss of the VPN context.
            var beforeCancellation = Encoding.ASCII.GetBytes("alive");
            await clientStream.WriteAsync(beforeCancellation, testTimeout.Token);
            var received = new byte[beforeCancellation.Length];
            await ReadExactlyAsync(originStream, received, testTimeout.Token);
            if (!received.AsSpan().SequenceEqual(beforeCancellation))
            {
                throw new InvalidOperationException("CONNECT tunnel was not alive before lifetime cancellation.");
            }

            lifetime.Cancel();

            using var closeTimeout = CancellationTokenSource.CreateLinkedTokenSource(testTimeout.Token);
            closeTimeout.CancelAfter(TimeSpan.FromSeconds(3));

            var oneByte = new byte[1];
            try
            {
                var read = await clientStream.ReadAsync(oneByte, closeTimeout.Token);
                if (read != 0)
                {
                    throw new InvalidOperationException(
                        "Client connection remained readable after outbound lifetime cancellation.");
                }
            }
            catch (IOException)
            {
                // A reset/abort is also an acceptable fail-closed tunnel termination.
            }
            catch (SocketException)
            {
                // A reset/abort is also an acceptable fail-closed tunnel termination.
            }
        }
        finally
        {
            proxyCancellation.Cancel();
            await proxyTask;
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

    private static async Task<byte[]> ReadHeaderAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var result = new MemoryStream();
        var one = new byte[1];

        while (result.Length < 65536)
        {
            var read = await stream.ReadAsync(one, cancellationToken);
            if (read == 0)
            {
                throw new IOException("Connection closed before the HTTP header was complete.");
            }

            result.WriteByte(one[0]);
            if (result.Length >= 4)
            {
                var data = result.GetBuffer();
                var length = checked((int)result.Length);
                if (data[length - 4] == (byte)'\r' &&
                    data[length - 3] == (byte)'\n' &&
                    data[length - 2] == (byte)'\r' &&
                    data[length - 1] == (byte)'\n')
                {
                    return result.ToArray();
                }
            }
        }

        throw new IOException("HTTP header exceeded test limit.");
    }

    private static async Task ReadExactlyAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], cancellationToken);
            if (read == 0)
            {
                throw new IOException("Connection closed before expected bytes were received.");
            }

            offset += read;
        }
    }

    private sealed class LifetimeOutboundFactory : IProxyOutboundConnectionFactory
    {
        private readonly int _originPort;
        private readonly CancellationToken _lifetimeToken;

        public LifetimeOutboundFactory(int originPort, CancellationToken lifetimeToken)
        {
            _originPort = originPort;
            _lifetimeToken = lifetimeToken;
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
                return new LifetimeOutboundConnection(socket, _lifetimeToken);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
    }

    private sealed class LifetimeOutboundConnection : IProxyOutboundConnection
    {
        public LifetimeOutboundConnection(Socket socket, CancellationToken lifetimeToken)
        {
            Socket = socket;
            LifetimeToken = lifetimeToken;
        }

        public Socket Socket { get; }
        public CancellationToken LifetimeToken { get; }

        public ValueTask DisposeAsync()
        {
            Socket.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
