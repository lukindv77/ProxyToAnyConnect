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
        var uri = ParseAbsoluteHttpUri(request.Target);
        var host = uri.IdnHost;
        var port = uri.IsDefaultPort ? 80 : uri.Port;
        var pathAndQuery = string.IsNullOrEmpty(uri.PathAndQuery) ? "/" : uri.PathAndQuery;

        await using var upstream = await _socketFactory.ConnectAsync(host, port, cancellationToken);
        await using var upstreamStream = new NetworkStream(upstream.Socket, ownsSocket: false);

        var originHeader = request.BuildOriginHeader(pathAndQuery);
        await upstreamStream.WriteAsync(originHeader, cancellationToken);
        RecordSent(originHeader.Length);
        if (!remainder.IsEmpty)
        {
            await upstreamStream.WriteAsync(remainder, cancellationToken);
            RecordSent(remainder.Length);
        }

        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            upstream.LifetimeToken);

        var clientToUpstream = PumpAsync(
            clientStream,
            upstreamStream,
            RecordSent,
            requestCancellation.Token);
        var upstreamToClient = PumpAsync(
            upstreamStream,
            clientStream,
            RecordReceived,
            requestCancellation.Token);

        try
        {
            await Task.WhenAny(clientToUpstream, upstreamToClient);
        }
        finally
        {
            requestCancellation.Cancel();
            await IgnoreCancellationAsync(clientToUpstream);
            await IgnoreCancellationAsync(upstreamToClient);
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
                var headerEnd = FindHeaderEnd(data);
                if (headerEnd < 0)
                {
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

    private static int FindHeaderEnd(ReadOnlySpan<byte> data)
    {
        for (var i = 0; i <= data.Length - 4; i++)
        {
            if (data[i] == (byte)'\r' &&
                data[i + 1] == (byte)'\n' &&
                data[i + 2] == (byte)'\r' &&
                data[i + 3] == (byte)'\n')
            {
                return i;
            }
        }

        return -1;
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

        var separator = authority.LastIndexOf(':');
        if (separator < 0)
        {
            return (authority, defaultPort);
        }

        var host = authority[..separator];
        if (string.IsNullOrWhiteSpace(host) ||
            !int.TryParse(authority[(separator + 1)..], out var port) ||
            port is < 1 or > 65535)
        {
            throw new InvalidDataException($"Invalid CONNECT target '{authority}'.");
        }

        return (host, port);
    }

    private static Uri ParseAbsoluteHttpUri(string target)
    {
        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri) ||
            !uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(uri.Host))
        {
            throw new InvalidDataException("Plain HTTP proxy requests must use an absolute http:// URI.");
        }

        return uri;
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

        private ParsedProxyRequest(string method, string target, string version, List<HeaderLine> headers)
        {
            Method = method;
            Target = target;
            Version = version;
            Headers = headers;
        }

        public string Method { get; }
        public string Target { get; }
        public string Version { get; }
        private List<HeaderLine> Headers { get; }

        public static ParsedProxyRequest Parse(ReadOnlySpan<byte> headerBytes)
        {
            var text = Encoding.Latin1.GetString(headerBytes);
            var lines = text.Split("\r\n", StringSplitOptions.None);
            if (lines.Length < 2)
            {
                throw new InvalidDataException("Invalid HTTP proxy request.");
            }

            var requestLine = lines[0].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (requestLine.Length != 3)
            {
                throw new InvalidDataException("Invalid HTTP proxy request line.");
            }

            var headers = new List<HeaderLine>();
            for (var i = 1; i < lines.Length && lines[i].Length > 0; i++)
            {
                var separator = lines[i].IndexOf(':');
                if (separator <= 0)
                {
                    throw new InvalidDataException("Invalid HTTP header line.");
                }

                headers.Add(new HeaderLine(
                    lines[i][..separator].Trim(),
                    lines[i][(separator + 1)..].Trim()));
            }

            return new ParsedProxyRequest(requestLine[0], requestLine[1], requestLine[2], headers);
        }

        public byte[] BuildOriginHeader(string pathAndQuery)
        {
            var connectionTokens = Headers
                .Where(header => header.Name.Equals("Connection", StringComparison.OrdinalIgnoreCase))
                .SelectMany(header => header.Value.Split(','))
                .Select(token => token.Trim())
                .Where(token => token.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var builder = new StringBuilder();
            builder.Append(Method).Append(' ').Append(pathAndQuery).Append(' ').Append(Version).Append("\r\n");

            foreach (var header in Headers)
            {
                if (header.Name.Equals("Connection", StringComparison.OrdinalIgnoreCase) ||
                    FixedHopByHopHeaders.Contains(header.Name) ||
                    connectionTokens.Contains(header.Name))
                {
                    continue;
                }

                builder.Append(header.Name).Append(": ").Append(header.Value).Append("\r\n");
            }

            builder.Append("Connection: close\r\n\r\n");
            return Encoding.Latin1.GetBytes(builder.ToString());
        }

        private sealed record HeaderLine(string Name, string Value);
    }
}
