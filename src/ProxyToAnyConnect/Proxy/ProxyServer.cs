using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ProxyToAnyConnect.Configuration;
using ProxyToAnyConnect.Network;
using ProxyToAnyConnect.Runtime;

namespace ProxyToAnyConnect.Proxy;

internal sealed class ProxyServer
{
    private const int TransferBufferSize = 32 * 1024;
    private const int InitialHeaderBufferSize = 4 * 1024;

    private static readonly byte[] ConnectionEstablished =
        Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n");

    private readonly ProxyOptions _options;
    private readonly IProxyOutboundConnectionFactory _socketFactory;
    private readonly ProxyRuntimeMetrics? _proxyMetrics;
    private readonly L2tpRuntimeMetrics? _l2tpMetrics;
    private readonly SemaphoreSlim _sessionSlots;
    private readonly TaskCompletionSource _listening =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ProxyServer(
        ProxyOptions options,
        IProxyOutboundConnectionFactory socketFactory,
        ProxyRuntimeMetrics? proxyMetrics = null,
        L2tpRuntimeMetrics? l2tpMetrics = null)
    {
        _options = options;
        _socketFactory = socketFactory;
        _proxyMetrics = proxyMetrics;
        _l2tpMetrics = l2tpMetrics;
        _sessionSlots = new SemaphoreSlim(
            options.MaxConcurrentConnections,
            options.MaxConcurrentConnections);
    }

    public Task WaitUntilListeningAsync(CancellationToken cancellationToken) =>
        _listening.Task.WaitAsync(cancellationToken);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        TcpListener? listener = null;
        try
        {
            listener = new TcpListener(IPAddress.Parse(_options.ListenAddress), _options.ListenPort);
            listener.Start();
            _listening.TrySetResult();

            while (!cancellationToken.IsCancellationRequested)
            {
                await _sessionSlots.WaitAsync(cancellationToken);
                TcpClient? client = null;
                try
                {
                    // When every user-space session slot is occupied, the loop stops here
                    // before Accept. Additional clients remain subject to the Windows TCP
                    // listen backlog instead of creating an unbounded number of Tasks/buffers.
                    client = await listener.AcceptTcpClientAsync(cancellationToken);
                    _ = HandleClientAndReleaseSlotAsync(client, cancellationToken);
                    client = null;
                }
                catch
                {
                    client?.Dispose();
                    _sessionSlots.Release();
                    throw;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _listening.TrySetCanceled(cancellationToken);
        }
        catch (Exception ex)
        {
            _listening.TrySetException(ex);
            throw;
        }
        finally
        {
            listener?.Stop();

            // A session owns one semaphore permit from immediately before Accept
            // until all of its client/upstream resources have been disposed. Once
            // the accept loop has stopped, acquiring every permit is therefore a
            // zero-allocation bounded join over all previously accepted sessions.
            // RunAsync does not return (and ProxyInstanceRuntime cannot release its
            // L2TP lease) until every accepted session has completed cleanup.
            await DrainAcceptedSessionsAsync();
            _sessionSlots.Dispose();
        }
    }

    private async Task DrainAcceptedSessionsAsync()
    {
        var acquired = 0;
        try
        {
            while (acquired < _options.MaxConcurrentConnections)
            {
                await _sessionSlots.WaitAsync();
                acquired++;
            }
        }
        finally
        {
            if (acquired > 0)
            {
                _sessionSlots.Release(acquired);
            }
        }
    }

    private async Task HandleClientAndReleaseSlotAsync(
        TcpClient client,
        CancellationToken cancellationToken)
    {
        try
        {
            await HandleClientSafelyAsync(client, cancellationToken);
        }
        finally
        {
            _sessionSlots.Release();
        }
    }

    private async Task HandleClientSafelyAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                await HandleClientAsync(client, cancellationToken);
            }
            catch (VpnUnavailableException ex)
            {
                await TryWriteErrorAsync(client, 503, "L2TP VPN unavailable", ex.Message, cancellationToken);
            }
            catch (InvalidDataException ex)
            {
                await TryWriteErrorAsync(client, 400, "Bad Request", ex.Message, cancellationToken);
            }
            catch (NotSupportedException ex)
            {
                await TryWriteErrorAsync(client, 501, "Not Implemented", ex.Message, cancellationToken);
            }
            catch (Exception ex) when (ex is SocketException or IOException)
            {
                await TryWriteErrorAsync(client, 502, "Bad Gateway", ex.Message, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Normal proxy pause/shutdown.
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Unhandled proxy session error: {ex}");
                await TryWriteErrorAsync(client, 500, "Internal Server Error", "Proxy session failed.", cancellationToken);
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        client.NoDelay = true;
        await using var clientStream = client.GetStream();

        using var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        headerTimeout.CancelAfter(TimeSpan.FromSeconds(_options.ClientHeaderTimeoutSeconds));
        var readResult = await ReadRequestAsync(clientStream, _options.MaxHeaderBytes, headerTimeout.Token);
        var request = readResult.Request;

        if (request.Method.Equals("CONNECT", StringComparison.OrdinalIgnoreCase))
        {
            var (host, port) = ParseAuthority(request.Target, 443);
            await HandleConnectAsync(
                clientStream,
                host,
                port,
                readResult.Remainder,
                cancellationToken);
            return;
        }

        await HandleHttpAsync(clientStream, request, readResult.Remainder, cancellationToken);
    }

    private async Task HandleConnectAsync(
        NetworkStream clientStream,
        string host,
        int port,
        ReadOnlyMemory<byte> remainder,
        CancellationToken cancellationToken)
    {
        await using var upstream = await _socketFactory.ConnectAsync(host, port, cancellationToken);
        await using var upstreamStream = new NetworkStream(upstream.Socket, ownsSocket: false);

        await clientStream.WriteAsync(ConnectionEstablished, cancellationToken);
        if (!remainder.IsEmpty)
        {
            await upstreamStream.WriteAsync(remainder, cancellationToken);
            RecordSent(remainder.Length);
        }

        using var tunnelCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            upstream.LifetimeToken);

        var clientToUpstream = PumpAsync(
            clientStream,
            upstreamStream,
            RecordSent,
            tunnelCancellation.Token);
        var upstreamToClient = PumpAsync(
            upstreamStream,
            clientStream,
            RecordReceived,
            tunnelCancellation.Token);

        try
        {
            await Task.WhenAny(clientToUpstream, upstreamToClient);
        }
        finally
        {
            tunnelCancellation.Cancel();
            await IgnoreCancellationAsync(clientToUpstream);
            await IgnoreCancellationAsync(upstreamToClient);
        }
    }

    private async Task HandleHttpAsync(
        NetworkStream clientStream,
        ParsedProxyRequest request,
        ReadOnlyMemory<byte> remainder,
        CancellationToken cancellationToken)
    {
        EnsureInitialBodyRemainderFits(request.ContentLength, remainder.Length);

        var uri = ParseAbsoluteHttpUri(request.Target);
        var host = uri.IdnHost;
        var port = uri.IsDefaultPort ? 80 : uri.Port;
        var pathAndQuery = string.IsNullOrEmpty(uri.PathAndQuery) ? "/" : uri.PathAndQuery;
        var authority = BuildHttpHostAuthority(uri);

        await using var upstream = await _socketFactory.ConnectAsync(host, port, cancellationToken);
        await using var upstreamStream = new NetworkStream(upstream.Socket, ownsSocket: false);

        var originHeader = request.BuildOriginHeader(pathAndQuery, authority);
        await upstreamStream.WriteAsync(originHeader, cancellationToken);
        RecordSent(originHeader.Length);

        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            upstream.LifetimeToken);

        if (request.ContentLength == 0)
        {
            await PumpAsync(
                upstreamStream,
                clientStream,
                RecordReceived,
                requestCancellation.Token);
            return;
        }

        using var bodyCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            requestCancellation.Token);
        var bodyUpload = ForwardRequestBodyAsync(
            clientStream,
            upstreamStream,
            remainder,
            request.ContentLength,
            bodyCancellation.Token);
        var responseDownload = PumpAsync(
            upstreamStream,
            clientStream,
            RecordReceived,
            requestCancellation.Token);

        var firstCompleted = await Task.WhenAny(bodyUpload, responseDownload);
        if (ReferenceEquals(firstCompleted, responseDownload))
        {
            // An origin may reject a request before consuming the declared body.
            // Stop reading client body bytes, but preserve the origin response/failure.
            bodyCancellation.Cancel();
            await IgnoreCancellationAsync(bodyUpload);
            await responseDownload;
            return;
        }

        try
        {
            await bodyUpload;
        }
        catch
        {
            requestCancellation.Cancel();
            await IgnoreCancellationAsync(responseDownload);
            throw;
        }

        // The proxy handles exactly one plain-HTTP request per client connection.
        // Once Content-Length bytes have been forwarded, no later client bytes are
        // read or sent upstream; only the origin response remains active.
        await responseDownload;
    }

    internal static void EnsureInitialBodyRemainderFits(long contentLength, int remainderLength)
    {
        if (contentLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(contentLength));
        }

        if (remainderLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(remainderLength));
        }

        if (remainderLength > contentLength)
        {
            throw new InvalidDataException(
                "Bytes after the HTTP header exceed the declared request body length.");
        }
    }

    private async Task ForwardRequestBodyAsync(
        Stream clientStream,
        Stream upstreamStream,
        ReadOnlyMemory<byte> initialBody,
        long contentLength,
        CancellationToken cancellationToken)
    {
        var remaining = contentLength;
        if (!initialBody.IsEmpty)
        {
            await upstreamStream.WriteAsync(initialBody, cancellationToken);
            RecordSent(initialBody.Length);
            remaining -= initialBody.Length;
        }

        if (remaining == 0)
        {
            return;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(TransferBufferSize);
        try
        {
            while (remaining > 0)
            {
                var requested = (int)Math.Min(remaining, TransferBufferSize);
                var read = await clientStream.ReadAsync(
                    buffer.AsMemory(0, requested),
                    cancellationToken);
                if (read == 0)
                {
                    throw new InvalidDataException(
                        "Client closed before the declared HTTP request body was complete.");
                }

                await upstreamStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                RecordSent(read);
                remaining -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: false);
        }
    }

    private static async Task PumpAsync(
        Stream source,
        Stream destination,
        Action<int> onTransferred,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(TransferBufferSize);
        try
        {
            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, TransferBufferSize), cancellationToken);
                if (read == 0)
                {
                    return;
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                onTransferred(read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: false);
        }
    }

    private void RecordReceived(int bytes)
    {
        _proxyMetrics?.Traffic.AddReceived(bytes);
        _l2tpMetrics?.Traffic.AddReceived(bytes);
    }

    private void RecordSent(int bytes)
    {
        _proxyMetrics?.Traffic.AddSent(bytes);
        _l2tpMetrics?.Traffic.AddSent(bytes);
    }

    private static async Task IgnoreCancellationAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
        catch (SocketException)
        {
        }
    }

    private static async Task<RequestReadResult> ReadRequestAsync(
        Stream stream,
        int maxHeaderBytes,
        CancellationToken cancellationToken)
    {
        var pool = ArrayPool<byte>.Shared;
        var capacity = Math.Min(InitialHeaderBufferSize, maxHeaderBytes);
        var buffer = pool.Rent(capacity);
        var length = 0;
        var searchStart = 0;

        try
        {
            while (length < maxHeaderBytes)
            {
                if (length == capacity)
                {
                    var nextCapacity = Math.Min(maxHeaderBytes, checked(capacity * 2));
                    if (nextCapacity <= capacity)
                    {
                        break;
                    }

                    var replacement = pool.Rent(nextCapacity);
                    buffer.AsSpan(0, length).CopyTo(replacement);
                    pool.Return(buffer, clearArray: false);
                    buffer = replacement;
                    capacity = nextCapacity;
                }

                var read = await stream.ReadAsync(
                    buffer.AsMemory(length, Math.Min(capacity - length, maxHeaderBytes - length)),
                    cancellationToken);
                if (read == 0)
                {
                    throw new InvalidDataException("Connection closed before the HTTP proxy request header was complete.");
                }

                length += read;
                var data = buffer.AsSpan(0, length);
                var headerEnd = FindHeaderEnd(data, searchStart);
                if (headerEnd < 0)
                {
                    // A newly completed CRLFCRLF delimiter can start at most three
                    // bytes before the previous end. Everything before that point
                    // has already been proven not to contain the header terminator.
                    searchStart = Math.Max(0, length - 3);
                    continue;
                }

                var headerLength = headerEnd + 4;
                var request = ParsedProxyRequest.Parse(data[..headerLength]);
                var remainderLength = length - headerLength;
                byte[] remainder;
                if (remainderLength == 0)
                {
                    remainder = [];
                }
                else
                {
                    remainder = GC.AllocateUninitializedArray<byte>(remainderLength);
                    data[headerLength..length].CopyTo(remainder);
                }

                return new RequestReadResult(request, remainder);
            }

            throw new InvalidDataException("HTTP proxy request header exceeded the configured size limit.");
        }
        finally
        {
            pool.Return(buffer, clearArray: false);
        }
    }

    internal static int FindHeaderEnd(ReadOnlySpan<byte> data, int searchStart = 0)
    {
        if ((uint)searchStart > (uint)data.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(searchStart));
        }

        var relativeIndex = data[searchStart..].IndexOf("\r\n\r\n"u8);
        return relativeIndex < 0 ? -1 : searchStart + relativeIndex;
    }

    internal static (string Host, int Port) ParseAuthority(string authority, int defaultPort)
    {
        if (string.IsNullOrWhiteSpace(authority))
        {
            throw new InvalidDataException("CONNECT target is empty.");
        }

        if (authority.StartsWith("[", StringComparison.Ordinal))
        {
            throw new NotSupportedException("IPv6 proxy targets are not supported yet.");
        }

        var separator = authority.IndexOf(':');
        if (separator >= 0 && authority.IndexOf(':', separator + 1) >= 0)
        {
            throw new InvalidDataException($"Invalid CONNECT target '{authority}'.");
        }

        var host = separator < 0 ? authority : authority[..separator];
        var port = defaultPort;
        if (separator >= 0 && !TryParseConnectPort(authority.AsSpan(separator + 1), out port))
        {
            throw new InvalidDataException($"Invalid CONNECT target '{authority}'.");
        }

        if (host.Length == 0)
        {
            throw new InvalidDataException($"Invalid CONNECT target '{authority}'.");
        }

        foreach (var character in host)
        {
            if (character <= 0x20 || character == 0x7F ||
                character == (char)0x5C ||
                character is '@' or '/' or '?' or '#' or ':')
            {
                throw new InvalidDataException($"Invalid CONNECT target '{authority}'.");
            }
        }

        if (IPAddress.TryParse(host, out var literal))
        {
            if (literal.AddressFamily != AddressFamily.InterNetwork)
            {
                throw new NotSupportedException("IPv6 proxy targets are not supported yet.");
            }

            return (literal.ToString(), port);
        }

        try
        {
            return (L2tpDnsResolver.NormalizeDnsHostStrict(host), port);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            throw new InvalidDataException($"Invalid CONNECT target '{authority}'.", ex);
        }
    }

    private static bool TryParseConnectPort(ReadOnlySpan<char> value, out int port)
    {
        port = 0;
        if (value.IsEmpty)
        {
            return false;
        }

        var parsed = 0;
        foreach (var character in value)
        {
            if (character < 0x30 || character > 0x39)
            {
                return false;
            }

            parsed = parsed * 10 + (character - 0x30);
            if (parsed > 65535)
            {
                return false;
            }
        }

        if (parsed == 0)
        {
            return false;
        }

        port = parsed;
        return true;
    }

    private static Uri ParseAbsoluteHttpUri(string target)
    {
        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri) ||
            !uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new InvalidDataException("Plain HTTP proxy requests must use an absolute http:// URI.");
        }

        return uri;
    }

    internal static string BuildHttpHostAuthority(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri ||
            !uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(uri.Host))
        {
            throw new InvalidDataException("A valid absolute HTTP URI is required to generate Host.");
        }

        if (uri.HostNameType == UriHostNameType.IPv6)
        {
            throw new NotSupportedException("IPv6 proxy targets are not supported yet.");
        }

        var host = uri.IdnHost;
        return uri.IsDefaultPort ? host : $"{host}:{uri.Port}";
    }

    private static async Task TryWriteErrorAsync(
        TcpClient client,
        int statusCode,
        string reason,
        string detail,
        CancellationToken cancellationToken)
    {
        if (!client.Connected)
        {
            return;
        }

        try
        {
            var body = Encoding.UTF8.GetBytes(detail);
            var header = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {statusCode} {reason}\r\n" +
                "Content-Type: text/plain; charset=utf-8\r\n" +
                $"Content-Length: {body.Length}\r\n" +
                "Connection: close\r\n\r\n");

            var stream = client.GetStream();
            await stream.WriteAsync(header, cancellationToken);
            await stream.WriteAsync(body, cancellationToken);
        }
        catch
        {
            // Best effort error response only.
        }
    }

    private readonly record struct RequestReadResult(ParsedProxyRequest Request, byte[] Remainder);

    internal sealed class ParsedProxyRequest
    {
        internal const int StackConnectionTokenCapacity = 8;

        private static readonly HashSet<string> FixedHopByHopHeaders = new(StringComparer.OrdinalIgnoreCase)
        {
            "Proxy-Authorization",
            "Proxy-Authenticate",
            "Proxy-Connection",
            "Keep-Alive",
            "TE",
            "Trailer",
            "Transfer-Encoding",
            "Upgrade"
        };

        // CONNECT does not forward origin headers. The shared empty list avoids
        // allocating a per-request List<HeaderLine> after header syntax validation.
        private static readonly List<HeaderLine> EmptyHeaders = [];

        private ParsedProxyRequest(
            string method,
            string target,
            string version,
            List<HeaderLine> headers,
            long contentLength)
        {
            Method = method;
            Target = target;
            Version = version;
            Headers = headers;
            ContentLength = contentLength;
        }

        public string Method { get; }
        public string Target { get; }
        public string Version { get; }
        public long ContentLength { get; }
        private List<HeaderLine> Headers { get; }

        public static ParsedProxyRequest Parse(ReadOnlySpan<byte> headerBytes)
        {
            var text = Encoding.Latin1.GetString(headerBytes);
            var requestLineEnd = text.IndexOf("\r\n", StringComparison.Ordinal);
            if (requestLineEnd < 0)
            {
                throw new InvalidDataException("Invalid HTTP proxy request.");
            }

            var requestLine = text.AsSpan(0, requestLineEnd);
            Span<Range> requestParts = stackalloc Range[3];
            var requestPartCount = requestLine.Split(
                requestParts,
                ' ',
                StringSplitOptions.RemoveEmptyEntries);
            if (requestPartCount != 3)
            {
                throw new InvalidDataException("Invalid HTTP proxy request line.");
            }

            var methodSpan = requestLine[requestParts[0]];
            var versionSpan = requestLine[requestParts[2]];
            if (!IsValidHeaderName(methodSpan))
            {
                throw new InvalidDataException("Invalid HTTP method token.");
            }

            if (!versionSpan.SequenceEqual("HTTP/1.0".AsSpan()) &&
                !versionSpan.SequenceEqual("HTTP/1.1".AsSpan()))
            {
                throw new InvalidDataException("Unsupported HTTP request version.");
            }

            var method = methodSpan.ToString();
            var target = requestLine[requestParts[1]].ToString();
            var version = versionSpan.ToString();
            var offset = requestLineEnd + 2;

            if (method.Equals("CONNECT", StringComparison.OrdinalIgnoreCase))
            {
                ValidateHeaderLines(text, offset);
                return new ParsedProxyRequest(method, target, version, EmptyHeaders, contentLength: 0);
            }

            var headers = new List<HeaderLine>();
            long? contentLength = null;
            var hasTransferEncoding = false;
            while (offset < text.Length)
            {
                var remaining = text.AsSpan(offset);
                var lineEnd = remaining.IndexOf("\r\n".AsSpan());
                if (lineEnd < 0)
                {
                    throw new InvalidDataException("Invalid HTTP header line.");
                }

                if (lineEnd == 0)
                {
                    break;
                }

                var line = remaining[..lineEnd];
                var separator = line.IndexOf(':');
                if (separator <= 0 || char.IsWhiteSpace(line[separator - 1]))
                {
                    throw new InvalidDataException("Invalid HTTP header line.");
                }

                var name = line[..separator];
                if (!IsValidHeaderName(name))
                {
                    throw new InvalidDataException("Invalid HTTP header name.");
                }

                var rawValue = line[(separator + 1)..];
                if (!IsValidHeaderValue(rawValue))
                {
                    throw new InvalidDataException("Invalid HTTP header field value.");
                }

                if (name.Equals("Connection", StringComparison.OrdinalIgnoreCase))
                {
                    ValidateConnectionOptions(rawValue);
                }

                var value = rawValue.Trim();
                var header = new HeaderLine(name.ToString(), value.ToString());
                headers.Add(header);

                if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    if (contentLength.HasValue)
                    {
                        throw new InvalidDataException(
                            "Multiple Content-Length fields are not accepted by this proxy.");
                    }

                    contentLength = ParseContentLength(value);
                }
                else if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
                {
                    hasTransferEncoding = true;
                }

                offset += lineEnd + 2;
            }

            if (hasTransferEncoding && contentLength.HasValue)
            {
                throw new InvalidDataException(
                    "Requests containing both Transfer-Encoding and Content-Length are rejected.");
            }

            if (hasTransferEncoding)
            {
                throw new NotSupportedException(
                    "Transfer-Encoding request bodies are not supported; use Content-Length.");
            }

            return new ParsedProxyRequest(
                method,
                target,
                version,
                headers,
                contentLength ?? 0);
        }

        private static long ParseContentLength(ReadOnlySpan<char> value)
        {
            if (value.IsEmpty)
            {
                throw new InvalidDataException("Content-Length is empty.");
            }

            long result = 0;
            foreach (var character in value)
            {
                if (character is < '0' or > '9')
                {
                    throw new InvalidDataException(
                        "Content-Length must be a non-negative decimal integer.");
                }

                var digit = character - '0';
                if (result > (long.MaxValue - digit) / 10)
                {
                    throw new InvalidDataException("Content-Length is too large.");
                }

                result = result * 10 + digit;
            }

            return result;
        }

        private static bool IsValidHeaderNameCharacter(char character) =>
            (uint)(character - '0') <= 9 ||
            (uint)((character | 0x20) - 'a') <= 25 ||
            character is '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '.' or '^' or '_' or '`' or '|' or '~';

        private static bool IsValidHeaderName(ReadOnlySpan<char> name)
        {
            if (name.IsEmpty)
            {
                return false;
            }

            foreach (var character in name)
            {
                if (!IsValidHeaderNameCharacter(character))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsValidHeaderValue(ReadOnlySpan<char> value)
        {
            foreach (var character in value)
            {
                if ((character < 0x20 && character != '\t') || character == 0x7F)
                {
                    return false;
                }
            }

            return true;
        }

        private static void ValidateConnectionOptions(ReadOnlySpan<char> value)
        {
            var segmentStart = 0;
            while (segmentStart <= value.Length)
            {
                var remaining = value[segmentStart..];
                var comma = remaining.IndexOf(',');
                var segment = comma < 0 ? remaining : remaining[..comma];

                var trimStart = 0;
                while (trimStart < segment.Length &&
                    (segment[trimStart] == ' ' || segment[trimStart] == (char)0x09))
                {
                    trimStart++;
                }

                var trimEnd = segment.Length;
                while (trimEnd > trimStart &&
                    (segment[trimEnd - 1] == ' ' || segment[trimEnd - 1] == (char)0x09))
                {
                    trimEnd--;
                }

                if (!IsValidHeaderName(segment[trimStart..trimEnd]))
                {
                    throw new InvalidDataException("Invalid HTTP Connection option.");
                }

                if (comma < 0)
                {
                    return;
                }

                segmentStart += comma + 1;
            }
        }

        private static void ValidateHeaderLines(string text, int offset)
        {
            var lineHasName = false;
            var seenColon = false;

            for (var i = offset; i < text.Length; i++)
            {
                var character = text[i];
                if (character == '\r')
                {
                    if (i + 1 >= text.Length || text[i + 1] != '\n')
                    {
                        throw new InvalidDataException("Invalid HTTP header line.");
                    }

                    if (!lineHasName && !seenColon)
                    {
                        return;
                    }

                    if (!seenColon)
                    {
                        throw new InvalidDataException("Invalid HTTP header line.");
                    }

                    lineHasName = false;
                    seenColon = false;
                    i++;
                    continue;
                }

                if (character == '\n')
                {
                    throw new InvalidDataException("Invalid HTTP header line.");
                }

                if (!seenColon)
                {
                    if (character == ':')
                    {
                        if (!lineHasName)
                        {
                            throw new InvalidDataException("Invalid HTTP header line.");
                        }

                        seenColon = true;
                        continue;
                    }

                    if (!IsValidHeaderNameCharacter(character))
                    {
                        throw new InvalidDataException("Invalid HTTP header name.");
                    }

                    lineHasName = true;
                    continue;
                }

                if ((character < 0x20 && character != '\t') || character == 0x7F)
                {
                    throw new InvalidDataException("Invalid HTTP header field value.");
                }
            }

            throw new InvalidDataException("Invalid HTTP header line.");
        }

        public byte[] BuildOriginHeader(string pathAndQuery)
        {
            Span<ConnectionTokenRef> stackConnectionTokens =
                stackalloc ConnectionTokenRef[StackConnectionTokenCapacity];
            var stackConnectionTokenCount = CollectConnectionTokens(
                stackConnectionTokens,
                out var overflowConnectionTokens);
            var connectionTokens = stackConnectionTokens[..stackConnectionTokenCount];

            var byteCount = checked(
                Encoding.Latin1.GetByteCount(Method) + 1 +
                Encoding.Latin1.GetByteCount(pathAndQuery) + 1 +
                Encoding.Latin1.GetByteCount(Version) + 2);

            foreach (var header in Headers)
            {
                if (ShouldSkipOriginHeader(header, connectionTokens, overflowConnectionTokens))
                {
                    continue;
                }

                byteCount = checked(
                    byteCount +
                    Encoding.Latin1.GetByteCount(header.Name) + 2 +
                    Encoding.Latin1.GetByteCount(header.Value) + 2);
            }

            byteCount = checked(byteCount + "Connection: close\r\n\r\n"u8.Length);
            var result = GC.AllocateUninitializedArray<byte>(byteCount);
            var destination = result.AsSpan();
            var written = 0;

            written += Encoding.Latin1.GetBytes(Method.AsSpan(), destination[written..]);
            destination[written++] = (byte)' ';
            written += Encoding.Latin1.GetBytes(pathAndQuery.AsSpan(), destination[written..]);
            destination[written++] = (byte)' ';
            written += Encoding.Latin1.GetBytes(Version.AsSpan(), destination[written..]);
            "\r\n"u8.CopyTo(destination[written..]);
            written += 2;

            foreach (var header in Headers)
            {
                if (ShouldSkipOriginHeader(header, connectionTokens, overflowConnectionTokens))
                {
                    continue;
                }

                written += Encoding.Latin1.GetBytes(header.Name.AsSpan(), destination[written..]);
                 ": "u8.CopyTo(destination[written..]);
                written += 2;
                written += Encoding.Latin1.GetBytes(header.Value.AsSpan(), destination[written..]);
                "\r\n"u8.CopyTo(destination[written..]);
                written += 2;
            }

            "Connection: close\r\n\r\n"u8.CopyTo(destination[written..]);
            return result;
        }

        private bool ShouldSkipOriginHeader(
            HeaderLine header,
            ReadOnlySpan<ConnectionTokenRef> stackConnectionTokens,
            HashSet<string>? overflowConnectionTokens)
        {
            if (header.Name.Equals("Connection", StringComparison.OrdinalIgnoreCase) ||
                FixedHopByHopHeaders.Contains(header.Name))
            {
                return true;
            }

            if (overflowConnectionTokens is not null)
            {
                return overflowConnectionTokens.Contains(header.Name);
            }

            var headerName = header.Name.AsSpan();
            foreach (var tokenRef in stackConnectionTokens)
            {
                var token = GetConnectionTokenSpan(tokenRef);
                if (token.Equals(headerName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        public byte[] BuildOriginHeader(string pathAndQuery, string authority)
        {
            if (string.IsNullOrWhiteSpace(authority))
            {
                throw new ArgumentException("Origin authority is required.", nameof(authority));
            }
            Span<ConnectionTokenRef> stackConnectionTokens =
                stackalloc ConnectionTokenRef[StackConnectionTokenCapacity];
            var stackConnectionTokenCount = CollectConnectionTokens(
                stackConnectionTokens,
                out var overflowConnectionTokens);
            var connectionTokens = stackConnectionTokens[..stackConnectionTokenCount];

            var byteCount = checked(
                Encoding.Latin1.GetByteCount(Method) + 1 +
                Encoding.Latin1.GetByteCount(pathAndQuery) + 1 +
                Encoding.Latin1.GetByteCount(Version) + 2 +
                "Host: "u8.Length + Encoding.Latin1.GetByteCount(authority) + 2);

            foreach (var header in Headers)
            {
                if (ShouldSkipOriginHeaderWithGeneratedHost(header, connectionTokens, overflowConnectionTokens))
                {
                    continue;
                }

                byteCount = checked(
                    byteCount +
                    Encoding.Latin1.GetByteCount(header.Name) + 2 +
                    Encoding.Latin1.GetByteCount(header.Value) + 2);
            }

            byteCount = checked(byteCount + "Connection: close\r\n\r\n"u8.Length);
            var result = GC.AllocateUninitializedArray<byte>(byteCount);
            var destination = result.AsSpan();
            var written = 0;

            written += Encoding.Latin1.GetBytes(Method.AsSpan(), destination[written..]);
            destination[written++] = (byte)' ';
            written += Encoding.Latin1.GetBytes(pathAndQuery.AsSpan(), destination[written..]);
            destination[written++] = (byte)' ';
            written += Encoding.Latin1.GetBytes(Version.AsSpan(), destination[written..]);
            "\r\nHost: "u8.CopyTo(destination[written..]);
            written += "\r\nHost: "u8.Length;
            written += Encoding.Latin1.GetBytes(authority.AsSpan(), destination[written..]);
            "\r\n"u8.CopyTo(destination[written..]);
            written += 2;

            foreach (var header in Headers)
            {
                if (ShouldSkipOriginHeaderWithGeneratedHost(header, connectionTokens, overflowConnectionTokens))
                {
                    continue;
                }

                written += Encoding.Latin1.GetBytes(header.Name.AsSpan(), destination[written..]);
                ": "u8.CopyTo(destination[written..]);
                written += 2;
                written += Encoding.Latin1.GetBytes(header.Value.AsSpan(), destination[written..]);
                "\r\n"u8.CopyTo(destination[written..]);
                written += 2;
            }

            "Connection: close\r\n\r\n"u8.CopyTo(destination[written..]);
            return result;
        }

        private bool ShouldSkipOriginHeaderWithGeneratedHost(
            HeaderLine header,
            ReadOnlySpan<ConnectionTokenRef> stackConnectionTokens,
            HashSet<string>? overflowConnectionTokens)
        {
            if (header.Name.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
                header.Name.Equals("Connection", StringComparison.OrdinalIgnoreCase) ||
                FixedHopByHopHeaders.Contains(header.Name))
            {
                return true;
            }

            if (overflowConnectionTokens is not null)
            {
                return overflowConnectionTokens.Contains(header.Name);
            }

            var headerName = header.Name.AsSpan();
            foreach (var tokenRef in stackConnectionTokens)
            {
                var token = GetConnectionTokenSpan(tokenRef);
                if (token.Equals(headerName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private int CollectConnectionTokens(
            Span<ConnectionTokenRef> stackTokens,
            out HashSet<string>? overflowTokens)
        {
            overflowTokens = null;
            var stackTokenCount = 0;

            for (var headerIndex = 0; headerIndex < Headers.Count; headerIndex++)
            {
                var header = Headers[headerIndex];
                if (!header.Name.Equals("Connection", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var value = header.Value.AsSpan();
                var segmentStart = 0;
                while (segmentStart <= value.Length)
                {
                    var remaining = value[segmentStart..];
                    var comma = remaining.IndexOf(',');
                    var segmentLength = comma < 0 ? remaining.Length : comma;
                    var segment = remaining[..segmentLength];
                    var trimStart = 0;
                    while (trimStart < segment.Length && char.IsWhiteSpace(segment[trimStart]))
                    {
                        trimStart++;
                    }

                    var trimEnd = segment.Length;
                    while (trimEnd > trimStart && char.IsWhiteSpace(segment[trimEnd - 1]))
                    {
                        trimEnd--;
                    }

                    if (trimEnd > trimStart)
                    {
                        var tokenRef = new ConnectionTokenRef(
                            headerIndex,
                            segmentStart + trimStart,
                            trimEnd - trimStart);

                        if (overflowTokens is null && stackTokenCount < stackTokens.Length)
                        {
                            stackTokens[stackTokenCount++] = tokenRef;
                        }
                        else
                        {
                            if (overflowTokens is null)
                            {
                                overflowTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                                for (var i = 0; i < stackTokenCount; i++)
                                {
                                    overflowTokens.Add(GetConnectionTokenSpan((stackTokens[i])).ToString());
                                }
                            }

                            overflowTokens.Add(GetConnectionTokenSpan(tokenRef).ToString());
                        }
                    }

                    if (comma < 0)
                    {
                        break;
                    }

                    segmentStart += comma + 1;
                }
            }

            return stackTokenCount;
        }

        private ReadOnlySpan<char> GetConnectionTokenSpan(ConnectionTokenRef tokenRef) =>
            Headers[tokenRef.HeaderIndex].Value.AsSpan(tokenRef.Start, tokenRef.Length);

        private readonly record struct ConnectionTokenRef(int HeaderIndex, int Start, int Length);

        private readonly record struct HeaderLine(string Name, string Value);
    }
}
