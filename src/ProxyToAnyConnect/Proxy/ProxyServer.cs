using System.Net;
using System.Net.Sockets;
using System.Text;
using ProxyToAnyConnect.Configuration;
using ProxyToAnyConnect.Network;

namespace ProxyToAnyConnect.Proxy;

internal sealed class ProxyServer
{
    private static readonly byte[] ConnectionEstablished =
        Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n");

    private readonly ProxyOptions _options;
    private readonly IProxyOutboundConnectionFactory _socketFactory;
    private readonly TaskCompletionSource _listening =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ProxyServer(ProxyOptions options, IProxyOutboundConnectionFactory socketFactory)
    {
        _options = options;
        _socketFactory = socketFactory;
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
                var client = await listener.AcceptTcpClientAsync(cancellationToken);
                _ = HandleClientSafelyAsync(client, cancellationToken);
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
                Console.Error.WriteLine($"Unhandled proxy session error: {ex}");
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
        var readResult = await ReadHeaderAsync(clientStream, _options.MaxHeaderBytes, headerTimeout.Token);
        var request = ParsedProxyRequest.Parse(readResult.Header);

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
        }

        using var tunnelCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            upstream.LifetimeToken);

        var clientToUpstream = PumpAsync(clientStream, upstreamStream, tunnelCancellation.Token);
        var upstreamToClient = PumpAsync(upstreamStream, clientStream, tunnelCancellation.Token);

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
        if (!remainder.IsEmpty)
        {
            await upstreamStream.WriteAsync(remainder, cancellationToken);
        }

        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            upstream.LifetimeToken);

        var clientToUpstream = PumpAsync(clientStream, upstreamStream, requestCancellation.Token);
        var upstreamToClient = PumpAsync(upstreamStream, clientStream, requestCancellation.Token);

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

    private static async Task PumpAsync(Stream source, Stream destination, CancellationToken cancellationToken)
    {
        var buffer = new byte[32 * 1024];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                return;
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            await destination.FlushAsync(cancellationToken);
        }
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

    private static async Task<HeaderReadResult> ReadHeaderAsync(
        Stream stream,
        int maxHeaderBytes,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[Math.Min(4096, maxHeaderBytes)];
        using var received = new MemoryStream();

        while (received.Length < maxHeaderBytes)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                throw new InvalidDataException("Connection closed before the HTTP proxy request header was complete.");
            }

            received.Write(buffer, 0, read);
            var data = received.GetBuffer().AsSpan(0, checked((int)received.Length));
            var headerEnd = FindHeaderEnd(data);
            if (headerEnd >= 0)
            {
                var headerLength = headerEnd + 4;
                return new HeaderReadResult(
                    data[..headerLength].ToArray(),
                    data[headerLength..].ToArray());
            }
        }

        throw new InvalidDataException("HTTP proxy request header exceeded the configured size limit.");
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

        if (authority.StartsWith('[', StringComparison.Ordinal))
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

    private readonly record struct HeaderReadResult(byte[] Header, byte[] Remainder);

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
