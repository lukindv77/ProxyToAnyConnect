using System.Net;
using System.Net.Sockets;
using System.Text;
using ProxyToAnyConnect.Configuration;
using ProxyToAnyConnect.Network;
using ProxyToAnyConnect.Proxy;

namespace ProxyToAnyConnect.SelfTests;

internal static class ProxyHttpFramingSelfTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public static async Task<int> RunAsync()
    {
        try
        {
            ParserRejectsAmbiguousOrUnsupportedFraming();
            InitialRemainderCannotExceedDeclaredBody();
            await ExactContentLengthBoundsClientToOriginBytesAsync();
            await TransferEncodingIsRejectedBeforeOutboundConnectAsync();

            Console.WriteLine(
                "PASS: plain HTTP framing is fail-closed and bounds client-to-origin bytes to one declared request body");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: proxy HTTP framing regression: {ex}");
            return 1;
        }
    }

    private static void ParserRejectsAmbiguousOrUnsupportedFraming()
    {
        var noBody = Parse(
            "GET http://example.test/ HTTP/1.1\r\n" +
            "Host: example.test\r\n\r\n");
        if (noBody.ContentLength != 0)
        {
            throw new InvalidOperationException("Request without body framing did not resolve to zero body length.");
        }

        var fiveBytes = Parse(
            "POST http://example.test/upload HTTP/1.1\r\n" +
            "Host: example.test\r\n" +
            "Content-Length: 5\r\n\r\n");
        if (fiveBytes.ContentLength != 5)
        {
            throw new InvalidOperationException("Valid Content-Length was not retained by the proxy parser.");
        }

        AssertParseThrows<InvalidDataException>(
            "POST http://example.test/ HTTP/1.1\r\nHost: example.test\r\nContent-Length: -1\r\n\r\n");
        AssertParseThrows<InvalidDataException>(
            "POST http://example.test/ HTTP/1.1\r\nHost: example.test\r\nContent-Length: +5\r\n\r\n");
        AssertParseThrows<InvalidDataException>(
            "POST http://example.test/ HTTP/1.1\r\nHost: example.test\r\nContent-Length: 5, 5\r\n\r\n");
        AssertParseThrows<InvalidDataException>(
            "POST http://example.test/ HTTP/1.1\r\nHost: example.test\r\nContent-Length: 5\r\nContent-Length: 5\r\n\r\n");
        AssertParseThrows<InvalidDataException>(
            "POST http://example.test/ HTTP/1.1\r\nHost: example.test\r\nContent-Length : 5\r\n\r\n");
        AssertParseThrows<NotSupportedException>(
            "POST http://example.test/ HTTP/1.1\r\nHost: example.test\r\nTransfer-Encoding: chunked\r\n\r\n");
        AssertParseThrows<InvalidDataException>(
            "POST http://example.test/ HTTP/1.1\r\nHost: example.test\r\nTransfer-Encoding: chunked\r\nContent-Length: 5\r\n\r\n");
    }

    private static void InitialRemainderCannotExceedDeclaredBody()
    {
        ProxyServer.EnsureInitialBodyRemainderFits(contentLength: 0, remainderLength: 0);
        ProxyServer.EnsureInitialBodyRemainderFits(contentLength: 5, remainderLength: 5);

        try
        {
            ProxyServer.EnsureInitialBodyRemainderFits(contentLength: 5, remainderLength: 6);
        }
        catch (InvalidDataException)
        {
            return;
        }

        throw new InvalidOperationException(
            "Header-read bytes beyond Content-Length were not rejected before outbound connect.");
    }

    private static async Task ExactContentLengthBoundsClientToOriginBytesAsync()
    {
        using var timeout = new CancellationTokenSource(Timeout);
        using var originListener = new TcpListener(IPAddress.Loopback, 0);
        originListener.Start();
        var originPort = ((IPEndPoint)originListener.LocalEndpoint).Port;

        var factory = new LoopbackOutboundFactory(originPort);
        var proxyPort = ReserveLoopbackPort();
        using var proxyCancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var proxy = CreateTestProxy(proxyPort, factory);
        var proxyTask = proxy.RunAsync(proxyCancellation.Token);

        try
        {
            using var client = new TcpClient { NoDelay = true };
            await client.ConnectAsync(IPAddress.Loopback, proxyPort, timeout.Token);
            await using var clientStream = client.GetStream();

            var requestHeader = Encoding.ASCII.GetBytes(
                "POST http://example.test/upload HTTP/1.1\r\n" +
                "Host: example.test\r\n" +
                "Content-Length: 5\r\n\r\n");
            await clientStream.WriteAsync(requestHeader, timeout.Token);

            using var originClient = await originListener.AcceptTcpClientAsync(timeout.Token);
            originClient.NoDelay = true;
            await using var originStream = originClient.GetStream();

            var originHeader = Encoding.Latin1.GetString(
                await ReadHeaderAsync(originStream, timeout.Token));
            if (!originHeader.StartsWith("POST /upload HTTP/1.1\r\n", StringComparison.Ordinal) ||
                !originHeader.Contains("Content-Length: 5\r\n", StringComparison.OrdinalIgnoreCase) ||
                !originHeader.Contains("Connection: close\r\n", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Unexpected bounded origin header:\n{originHeader}");
            }

            await clientStream.WriteAsync("HELLO"u8.ToArray(), timeout.Token);
            var body = new byte[5];
            await ReadExactlyAsync(originStream, body, timeout.Token);
            if (!body.AsSpan().SequenceEqual("HELLO"u8))
            {
                throw new InvalidOperationException("Origin did not receive the exact declared request body.");
            }

            // Bytes sent after the declared body belong to no request handled by this one-request
            // proxy connection. The previous unbounded pump forwarded them to the same origin socket.
            await clientStream.WriteAsync("SMUGGLED"u8.ToArray(), timeout.Token);
            using (var noExtra = new CancellationTokenSource(TimeSpan.FromMilliseconds(250)))
            {
                var one = new byte[1];
                try
                {
                    var extraRead = await originStream.ReadAsync(one, noExtra.Token);
                    if (extraRead != 0)
                    {
                        throw new InvalidOperationException(
                            "Client bytes after Content-Length were forwarded into the origin connection.");
                    }
                }
                catch (OperationCanceledException) when (noExtra.IsCancellationRequested)
                {
                    // Expected: the origin socket remains open but no post-body bytes arrive.
                }
            }

            var response = Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nOK");
            await originStream.WriteAsync(response, timeout.Token);
            originClient.Client.Shutdown(SocketShutdown.Send);

            var clientResponse = Encoding.Latin1.GetString(
                await ReadToEndAsync(clientStream, timeout.Token));
            if (!clientResponse.Contains("200 OK", StringComparison.Ordinal) ||
                !clientResponse.EndsWith("OK", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Unexpected bounded proxy response:\n{clientResponse}");
            }

            if (factory.ConnectCount != 1)
            {
                throw new InvalidOperationException($"Expected one origin connection; got {factory.ConnectCount}.");
            }
        }
        finally
        {
            proxyCancellation.Cancel();
            await proxyTask;
        }
    }

    private static async Task TransferEncodingIsRejectedBeforeOutboundConnectAsync()
    {
        using var timeout = new CancellationTokenSource(Timeout);
        var factory = new CountingRejectingFactory();
        var proxyPort = ReserveLoopbackPort();
        using var proxyCancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var proxy = CreateTestProxy(proxyPort, factory);
        var proxyTask = proxy.RunAsync(proxyCancellation.Token);

        try
        {
            using (var client = new TcpClient { NoDelay = true })
            {
                await client.ConnectAsync(IPAddress.Loopback, proxyPort, timeout.Token);
                await using var stream = client.GetStream();
                await stream.WriteAsync(
                    Encoding.ASCII.GetBytes(
                        "POST http://example.test/upload HTTP/1.1\r\n" +
                        "Host: example.test\r\n" +
                        "Transfer-Encoding: chunked\r\n\r\n" +
                        "5\r\nHELLO\r\n0\r\n\r\n"),
                    timeout.Token);

                var response = Encoding.Latin1.GetString(
                    await ReadToEndAsync(stream, timeout.Token));
                if (!response.StartsWith("HTTP/1.1 501 Not Implemented", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"Transfer-Encoding was not rejected as unsupported:\n{response}");
                }
            }

            using (var client = new TcpClient { NoDelay = true })
            {
                await client.ConnectAsync(IPAddress.Loopback, proxyPort, timeout.Token);
                await using var stream = client.GetStream();
                await stream.WriteAsync(
                    Encoding.ASCII.GetBytes(
                        "POST http://example.test/upload HTTP/1.1\r\n" +
                        "Host: example.test\r\n" +
                        "Transfer-Encoding: chunked\r\n" +
                        "Content-Length: 5\r\n\r\n"),
                    timeout.Token);

                var response = Encoding.Latin1.GetString(
                    await ReadToEndAsync(stream, timeout.Token));
                if (!response.StartsWith("HTTP/1.1 400 Bad Request", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"Ambiguous TE+CL framing was not rejected as bad request:\n{response}");
                }
            }

            if (factory.ConnectCount != 0)
            {
                throw new InvalidOperationException(
                    $"Invalid HTTP framing opened {factory.ConnectCount} outbound connection(s).");
            }
        }
        finally
        {
            proxyCancellation.Cancel();
            await proxyTask;
        }
    }

    private static ProxyServer.ParsedProxyRequest Parse(string text) =>
        ProxyServer.ParsedProxyRequest.Parse(Encoding.Latin1.GetBytes(text));

    private static void AssertParseThrows<TException>(string text)
        where TException : Exception
    {
        try
        {
            _ = Parse(text);
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Expected {typeof(TException).Name} for malformed framing was not thrown.");
    }

    private static ProxyServer CreateTestProxy(
        int proxyPort,
        IProxyOutboundConnectionFactory factory) =>
        new(
            new ProxyOptions
            {
                ListenAddress = "127.0.0.1",
                ListenPort = proxyPort,
                MaxConcurrentConnections = 8,
                MaxHeaderBytes = 65536,
                ClientHeaderTimeoutSeconds = 5
            },
            factory);

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

    private static async Task<byte[]> ReadHeaderAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var one = new byte[1];
        while (buffer.Length < 65536)
        {
            var read = await stream.ReadAsync(one, cancellationToken);
            if (read == 0)
            {
                throw new IOException("Stream ended before HTTP header completed.");
            }

            buffer.WriteByte(one[0]);
            if (buffer.Length >= 4)
            {
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
        }

        throw new IOException("HTTP header exceeded test limit.");
    }

    private static async Task<byte[]> ReadToEndAsync(
        Stream stream,
        CancellationToken cancellationToken)
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
                throw new IOException("Stream ended before expected body completed.");
            }

            offset += read;
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
            throw new InvalidOperationException("Invalid framing must be rejected before outbound connect.");
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
