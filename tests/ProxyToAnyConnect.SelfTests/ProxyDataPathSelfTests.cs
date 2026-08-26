using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ProxyToAnyConnect.Configuration;
using ProxyToAnyConnect.Network;
using ProxyToAnyConnect.Proxy;

namespace ProxyToAnyConnect.SelfTests;

internal static class ProxyDataPathSelfTests
{
    private const int PayloadSize = 2 * 1024 * 1024;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    public static async Task<int> RunAsync()
    {
        try
        {
            await TransfersMultiMegabyteConnectPayloadAsync();
            Console.WriteLine("PASS: pooled CONNECT data path transfers multi-megabyte payloads bidirectionally");

            await BoundsConcurrentSessionAdmissionAsync();
            Console.WriteLine("PASS: proxy concurrency limit bounds user-space session admission");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: proxy data-path regression: {ex}");
            return 1;
        }
    }

    private static async Task TransfersMultiMegabyteConnectPayloadAsync()
    {
        using var timeout = new CancellationTokenSource(Timeout);
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
                MaxConcurrentConnections = 16,
                MaxHeaderBytes = 65536
            },
            factory);
        var proxyTask = proxy.RunAsync(proxyCancellation.Token);

        try
        {
            using var client = new TcpClient { NoDelay = true };
            await client.ConnectAsync(IPAddress.Loopback, proxyPort, timeout.Token);
            await using var clientStream = client.GetStream();

            var connectRequest = BuildConnectRequest("performance.test");
            await clientStream.WriteAsync(connectRequest, timeout.Token);

            using var originClient = await originListener.AcceptTcpClientAsync(timeout.Token);
            originClient.NoDelay = true;
            await using var originStream = originClient.GetStream();

            var responseHeader = await ReadHeaderAsync(clientStream, timeout.Token);
            EnsureConnectEstablished(responseHeader);

            var clientPayload = CreatePayload(PayloadSize, seed: 17);
            var originPayload = CreatePayload(PayloadSize, seed: 91);
            var originReceived = GC.AllocateUninitializedArray<byte>(clientPayload.Length);
            var clientReceived = GC.AllocateUninitializedArray<byte>(originPayload.Length);

            var stopwatch = Stopwatch.StartNew();

            var clientWrite = clientStream.WriteAsync(clientPayload, timeout.Token).AsTask();
            var originRead = ReadExactlyAsync(originStream, originReceived, timeout.Token);
            await Task.WhenAll(clientWrite, originRead);

            var originWrite = originStream.WriteAsync(originPayload, timeout.Token).AsTask();
            var clientRead = ReadExactlyAsync(clientStream, clientReceived, timeout.Token);
            await Task.WhenAll(originWrite, clientRead);

            stopwatch.Stop();

            if (!originReceived.AsSpan().SequenceEqual(clientPayload))
            {
                throw new InvalidOperationException("Client-to-origin multi-megabyte payload was corrupted.");
            }

            if (!clientReceived.AsSpan().SequenceEqual(originPayload))
            {
                throw new InvalidOperationException("Origin-to-client multi-megabyte payload was corrupted.");
            }

            var transferredBytes = (long)clientPayload.Length + originPayload.Length;
            var mibPerSecond = transferredBytes / 1024d / 1024d / Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001);
            Console.WriteLine(
                $"INFO: CONNECT loopback transferred {transferredBytes / 1024d / 1024d:F1} MiB " +
                $"in {stopwatch.Elapsed.TotalMilliseconds:F1} ms ({mibPerSecond:F1} MiB/s)." );
        }
        finally
        {
            proxyCancellation.Cancel();
            await proxyTask;
        }
    }

    private static async Task BoundsConcurrentSessionAdmissionAsync()
    {
        using var timeout = new CancellationTokenSource(Timeout);
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
                MaxConcurrentConnections = 1,
                MaxHeaderBytes = 65536
            },
            factory);
        var proxyTask = proxy.RunAsync(proxyCancellation.Token);

        TcpClient? firstClient = null;
        TcpClient? firstOrigin = null;
        try
        {
            firstClient = new TcpClient { NoDelay = true };
            await firstClient.ConnectAsync(IPAddress.Loopback, proxyPort, timeout.Token);
            var firstStream = firstClient.GetStream();
            await firstStream.WriteAsync(BuildConnectRequest("first.test"), timeout.Token);

            firstOrigin = await originListener.AcceptTcpClientAsync(timeout.Token);
            EnsureConnectEstablished(await ReadHeaderAsync(firstStream, timeout.Token));

            using var secondClient = new TcpClient { NoDelay = true };
            await secondClient.ConnectAsync(IPAddress.Loopback, proxyPort, timeout.Token);
            var secondStream = secondClient.GetStream();
            await secondStream.WriteAsync(BuildConnectRequest("second.test"), timeout.Token);

            await Task.Delay(200, timeout.Token);
            if (factory.ConnectCount != 1)
            {
                throw new InvalidOperationException(
                    $"Expected exactly one admitted upstream session while the slot was occupied; got {factory.ConnectCount}.");
            }

            firstClient.Dispose();
            firstClient = null;
            firstOrigin.Dispose();
            firstOrigin = null;

            using var secondOrigin = await originListener.AcceptTcpClientAsync(timeout.Token);
            EnsureConnectEstablished(await ReadHeaderAsync(secondStream, timeout.Token));

            if (factory.ConnectCount != 2)
            {
                throw new InvalidOperationException(
                    $"Second session was not admitted after the first slot was released; count={factory.ConnectCount}.");
            }
        }
        finally
        {
            firstClient?.Dispose();
            firstOrigin?.Dispose();
            proxyCancellation.Cancel();
            await proxyTask;
        }
    }

    private static byte[] BuildConnectRequest(string host) =>
        Encoding.ASCII.GetBytes(
            $"CONNECT {host}:443 HTTP/1.1\r\nHost: {host}:443\r\n\r\n");

    private static void EnsureConnectEstablished(byte[] responseHeader)
    {
        if (!Encoding.ASCII.GetString(responseHeader)
                .StartsWith("HTTP/1.1 200 Connection Established", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("CONNECT handshake failed before data-path test.");
        }
    }

    private static byte[] CreatePayload(int length, byte seed)
    {
        var payload = GC.AllocateUninitializedArray<byte>(length);
        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] = unchecked((byte)(seed + i * 31));
        }

        return payload;
    }

    private static async Task<byte[]> ReadHeaderAsync(Stream stream, CancellationToken cancellationToken)
    {
        var result = new List<byte>(128);
        var one = new byte[1];
        while (result.Count < 65536)
        {
            var read = await stream.ReadAsync(one, cancellationToken);
            if (read == 0)
            {
                throw new IOException("Stream ended before CONNECT response header completed.");
            }

            result.Add(one[0]);
            var count = result.Count;
            if (count >= 4 &&
                result[count - 4] == (byte)'\r' &&
                result[count - 3] == (byte)'\n' &&
                result[count - 2] == (byte)'\r' &&
                result[count - 1] == (byte)'\n')
            {
                return result.ToArray();
            }
        }

        throw new IOException("CONNECT response header exceeded test limit.");
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
                throw new IOException("Stream ended before the expected payload was received.");
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
                await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, _originPort), cancellationToken);
                return new LoopbackOutboundConnection(socket);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
    }

    private sealed class LoopbackOutboundConnection : IProxyOutboundConnection
    {
        public LoopbackOutboundConnection(Socket socket)
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
