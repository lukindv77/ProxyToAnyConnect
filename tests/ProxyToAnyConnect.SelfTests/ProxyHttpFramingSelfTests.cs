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
            ParserUsesHttpOwsOnlyForFieldValues();
            InitialRemainderCannotExceedDeclaredBody();
            await ExactContentLengthBoundsClientToOriginBytesAsync();
            await TransferEncodingIsRejectedBeforeOutboundConnectAsync();
            await NonOwsFramingBytesAreRejectedBeforeOutboundConnectAsync();

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

    private static void ParserUsesHttpOwsOnlyForFieldValues()
    {
        var ows = Parse(
            "POST http://example.test/upload HTTP/1.1\r\n" +
            "Host: example.test\r\n" +
            "Content-Length:\t 5 \t\r\n\r\n");
        if (ows.ContentLength != 5)
        {
            throw new InvalidOperationException("SP/HTAB HTTP OWS was not accepted around Content-Length.");
        }

        foreach (var invalidWhitespace in new byte[] { 0x85, 0xA0 })
        {
            var request = BuildRawRequest(
                "POST http://example.test/upload HTTP/1.1\r\n" +
                "Host: example.test\r\n" +
                "Content-Length: ",
                invalidWhitespace,
                "5",
                invalidWhitespace,
                "\r\n\r\n");
            AssertRawParseThrows<InvalidDataException>(request);
        }

        var opaqueRequest = BuildRawRequest(
            "GET http://example.test/ HTTP/1.1\r\n" +
            "Host: ignored.example\r\n" +
            "X-Opaque: ",
            (byte)0xA0,
            "edge",
            (byte)0x85,
            " \t\r\n\r\n");
        var opaque = ProxyServer.ParsedProxyRequest.Parse(opaqueRequest);
        var originHeader = opaque.BuildOriginHeader("/", "example.test");
        var expectedOpaque = BuildRawRequest("X-Opaque: ", (byte)0xA0, "edge", (byte)0x85, "\r\n");
        if (originHeader.AsSpan().IndexOf(expectedOpaque) < 0)
        {
            throw new InvalidOperationException(
                "Non-OWS obs-text at a forwarded header edge was silently trimmed.");
        }
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

            var clientResponse = await ReadFramedResponseAsync(clientStream, timeout.Token);
            if (!clientResponse.Header.StartsWith("HTTP/1.1 200 OK\r\n", StringComparison.Ordinal) ||
                !clientResponse.Body.AsSpan().SequenceEqual("OK"u8))
            {
                throw new InvalidOperationException(
                    $"Unexpected bounded proxy response:\n{clientResponse.Header}" +
                    Encoding.Latin1.GetString(clientResponse.Body));
            }

            // Deliberate post-Content-Length bytes remain unread by the one-request proxy.
            // Windows may report WSAECONNRESET when that socket closes; only accept that
            // close detail after the complete framed origin response has been delivered.
            await AssertClosedAfterCompleteResponseAsync(clientStream, timeout.Token);

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

                var response = await ReadFramedResponseAsync(stream, timeout.Token);
                if (!response.Header.StartsWith(
                        "HTTP/1.1 501 Not Implemented\r\n",
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Transfer-Encoding was not rejected as unsupported:\n{response.Header}");
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

                var response = await ReadFramedResponseAsync(stream, timeout.Token);
                if (!response.Header.StartsWith(
                        "HTTP/1.1 400 Bad Request\r\n",
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Ambiguous TE+CL framing was not rejected as bad request:\n{response.Header}");
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

    private static async Task NonOwsFramingBytesAreRejectedBeforeOutboundConnectAsync()
    {
        using var timeout = new CancellationTokenSource(Timeout);
        var factory = new CountingRejectingFactory();
        var proxyPort = ReserveLoopbackPort();
        using var proxyCancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var proxy = CreateTestProxy(proxyPort, factory);
        var proxyTask = proxy.RunAsync(proxyCancellation.Token);

        try
        {
            foreach (var invalidWhitespace in new byte[] { 0x85, 0xA0 })
            {
                using var client = new TcpClient { NoDelay = true };
                await client.ConnectAsync(IPAddress.Loopback, proxyPort, timeout.Token);
                await using var stream = client.GetStream();
                var request = BuildRawRequest(
                    "POST http://example.test/upload HTTP/1.1\r\n" +
                    "Host: example.test\r\n" +
                    "Content-Length: ",
                    invalidWhitespace,
                    "5",
                    invalidWhitespace,
                    "\r\n\r\nHELLO");
                await stream.WriteAsync(request, timeout.Token);

                var response = await ReadFramedResponseAsync(stream, timeout.Token);
                if (!response.Header.StartsWith("HTTP/1.1 400 Bad Request\r\n", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Non-OWS Content-Length byte 0x{invalidWhitespace:X2} was not rejected: {response.Header}");
                }
            }

            if (factory.ConnectCount != 0)
            {
                throw new InvalidOperationException(
                    $"Non-OWS malformed framing opened {factory.ConnectCount} outbound connection(s).");
            }
        }
        finally
        {
            proxyCancellation.Cancel();
            await proxyTask;
        }
    }

    private static byte[] BuildRawRequest(params object[] parts)
    {
        using var buffer = new MemoryStream();
        foreach (var part in parts)
        {
            switch (part)
            {
                case string text:
                    buffer.Write(Encoding.Latin1.GetBytes(text));
                    break;
                case byte value:
                    buffer.WriteByte(value);
                    break;
                default:
                    throw new ArgumentException($"Unsupported raw-request part type {part.GetType()}.", nameof(parts));
            }
        }

        return buffer.ToArray();
    }

    private static void AssertRawParseThrows<TException>(byte[] request)
        where TException : Exception
    {
        try
        {
            _ = ProxyServer.ParsedProxyRequest.Parse(request);
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Expected {typeof(TException).Name} for malformed raw framing was not thrown.");
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

    private static async Task<HttpResponse> ReadFramedResponseAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var headerBytes = await ReadHeaderAsync(stream, cancellationToken);
        var header = Encoding.Latin1.GetString(headerBytes);
        var contentLength = ParseResponseContentLength(header);
        var body = new byte[contentLength];
        await ReadExactlyAsync(stream, body, cancellationToken);
        return new HttpResponse(header, body);
    }

    private static int ParseResponseContentLength(string header)
    {
        foreach (var line in header.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            const string prefix = "Content-Length:";
            if (!line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = line.AsSpan(prefix.Length).Trim();
            if (int.TryParse(value, out var contentLength) && contentLength >= 0)
            {
                return contentLength;
            }

            throw new InvalidOperationException(
                $"Invalid Content-Length in proxy test response: {line}");
        }

        throw new InvalidOperationException(
            $"Proxy test response did not contain Content-Length:\n{header}");
    }

    private static async Task AssertClosedAfterCompleteResponseAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        using var closeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        closeTimeout.CancelAfter(TimeSpan.FromSeconds(2));
        var one = new byte[1];

        try
        {
            var read = await stream.ReadAsync(one, closeTimeout.Token);
            if (read != 0)
            {
                throw new InvalidOperationException(
                    "Proxy sent bytes beyond the complete Content-Length-framed response.");
            }

            Console.WriteLine(
                "INFO: exact-CL malicious-tail connection closed with clean EOF after complete response.");
        }
        catch (IOException ex) when (
            ex.InnerException is SocketException socketException &&
            socketException.SocketErrorCode == SocketError.ConnectionReset)
        {
            Console.WriteLine(
                "INFO: exact-CL malicious-tail connection reset after complete response " +
                "(Windows WSAECONNRESET 10054 with unread client tail).");
        }
        catch (OperationCanceledException) when (
            closeTimeout.IsCancellationRequested &&
            !cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                "Proxy did not close the exact-CL malicious-tail connection after the complete response.");
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

    private readonly record struct HttpResponse(string Header, byte[] Body);

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
