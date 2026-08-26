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
    private readonly L2tpSocketFactory _socketFactory;

    public ProxyServer(ProxyOptions options, L2tpSocketFactory socketFactory)
    {
        _options = options;
        _socketFactory = socketFactory;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var listener = new TcpListener(IPAddress.Parse(_options.ListenAddress), _options.ListenPort);
        listener.Start();

        Console.WriteLine($"Proxy listening on {_options.ListenAddress}:{_options.ListenPort}");

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(cancellationToken);
                _ = HandleClientSafelyAsync(client, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        finally
        {
            listener.Stop();
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
                // Normal shutdown.
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

        var readResult = await ReadHeaderAsync(clientStream, _options.MaxHeaderBytes, cancellationToken);
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

        await PumpBidirectionalAsync(
            clientStream,
            upstreamStream,
            upstream.Context.LifetimeToken,
            cancellationToken);
    }

    private async Task HandleHttpAsync(
        NetworkStream clientStream,
        ParsedProxyRequest request,
        ReadOnlyMemory<byte> remainder,
        CancellationToken cancellationToken)
    {
        var destination = ResolveHttpDestination(request);
        await using var upstream = await _socketFactory.ConnectAsync(
            destination.Host,
            destination.Port,
            cancellationToken);
        await using var upstreamStream = new NetworkStream(upstream.Socket, ownsSocket: false);

        var outboundHeader = request.BuildOriginHeader(destination.OriginTarget);
        await upstreamStream.WriteAsync(outboundHeader, cancellationToken);
        if (!remainder.IsEmpty)
        {
            await upstreamStream.WriteAsync(remainder, cancellationToken);
        }

        await PumpBidirectionalAsync(
            clientStream,
            upstreamStream,
            upstream.Context.LifetimeToken,
            cancellationToken);
    }

    private static HttpDestination ResolveHttpDestination(ParsedProxyRequest request)
    {
        if (Uri.TryCreate(request.Target, UriKind.Absolute, out var uri))
        {
            if (!uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "Non-CONNECT proxy requests currently support only the http scheme.");
            }

            var absolutePort = uri.IsDefaultPort ? 80 : uri.Port;
            var absoluteOriginTarget = string.IsNullOrEmpty(uri.PathAndQuery) ? "/" : uri.PathAndQuery;
            return new HttpDestination(uri.Host, absolutePort, absoluteOriginTarget);
        }

        var hostHeader = request.GetHeader("Host")
            ?? throw new InvalidDataException("HTTP request does not contain a Host header.");
        var (host, authorityPort) = ParseAuthority(hostHeader, 80);
        var relativeOriginTarget = string.IsNullOrWhiteSpace(request.Target) ? "/" : request.Target;
        return new HttpDestination(host, authorityPort, relativeOriginTarget);
    }

    internal static (string Host, int Port) ParseAuthority(string authority, int defaultPort)
    {
        var value = authority.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException("Proxy target authority is empty.");
        }

        if (value.StartsWith('['))
        {
            throw new NotSupportedException("IPv6 proxy targets are not supported yet.");
        }

        var colon = value.LastIndexOf(':');
        if (colon <= 0)
        {
            return (value, defaultPort);
        }

        var host = value[..colon];
        if (string.IsNullOrWhiteSpace(host) ||
            !int.TryParse(value[(colon + 1)..], out var port) ||
            port is < 1 or > 65535)
        {
            throw new InvalidDataException($"Invalid target authority '{authority}'.");
        }

        return (host, port);
    }

    private static async Task PumpBidirectionalAsync(
        Stream client,
        Stream upstream,
        CancellationToken vpnLifetimeToken,
        CancellationToken applicationToken)
    {
        using var sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            vpnLifetimeToken,
            applicationToken);

        var clientToUpstream = client.CopyToAsync(upstream, sessionCancellation.Token);
        var upstreamToClient = upstream.CopyToAsync(client, sessionCancellation.Token);

        await Task.WhenAny(clientToUpstream, upstreamToClient);
        sessionCancellation.Cancel();

        try
        {
            await Task.WhenAll(clientToUpstream, upstreamToClient);
        }
        catch (OperationCanceledException) when (sessionCancellation.IsCancellationRequested)
        {
            // Expected when either side closes or the VPN disappears.
        }
        catch (IOException)
        {
            // A closed TCP tunnel is a normal end of a proxy session.
        }
    }

    private static async Task<HeaderReadResult> ReadHeaderAsync(
        NetworkStream stream,
        int maxHeaderBytes,
        CancellationToken cancellationToken)
    {
        using var aggregate = new MemoryStream(Math.Min(maxHeaderBytes, 16 * 1024));
        var buffer = new byte[4096];

        while (aggregate.Length <= maxHeaderBytes)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                throw new InvalidDataException("Client disconnected before sending a complete HTTP header.");
            }

            aggregate.Write(buffer, 0, read);
            if (aggregate.Length > maxHeaderBytes)
            {
                throw new InvalidDataException("Proxy request header exceeds the configured limit.");
            }

            var data = aggregate.GetBuffer().AsSpan(0, checked((int)aggregate.Length));
            var headerEnd = FindHeaderEnd(data);
            if (headerEnd < 0)
            {
                continue;
            }

            var headerLength = headerEnd + 4;
            var header = data[..headerLength].ToArray();
            var remainder = data[headerLength..].ToArray();
            return new HeaderReadResult(header, remainder);
        }

        throw new InvalidDataException("Proxy request header exceeds the configured limit.");
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

    private static async Task TryWriteErrorAsync(
        TcpClient client,
        int statusCode,
        string reason,
        string detail,
        CancellationToken cancellationToken)
    {
        try
        {
            var body = Encoding.UTF8.GetBytes(detail + "\n");
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
            // The client may already be gone; error reporting is best-effort only.
        }
    }

    private sealed record HeaderReadResult(byte[] Header, byte[] Remainder);
    private sealed record HttpDestination(string Host, int Port, string OriginTarget);

    internal sealed class ParsedProxyRequest
    {
        private ParsedProxyRequest(
            string method,
            string target,
            string version,
            IReadOnlyList<KeyValuePair<string, string>> headers)
        {
            Method = method;
            Target = target;
            Version = version;
            Headers = headers;
        }

        public string Method { get; }
        public string Target { get; }
        public string Version { get; }
        public IReadOnlyList<KeyValuePair<string, string>> Headers { get; }

        public static ParsedProxyRequest Parse(ReadOnlySpan<byte> headerBytes)
        {
            var text = Encoding.Latin1.GetString(headerBytes);
            var lines = text.Split("\r\n", StringSplitOptions.None);
            if (lines.Length == 0)
            {
                throw new InvalidDataException("Empty proxy request.");
            }

            var firstLine = lines[0].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (firstLine.Length != 3)
            {
                throw new InvalidDataException("Malformed HTTP request line.");
            }

            if (!firstLine[2].StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Unsupported HTTP version token '{firstLine[2]}'.");
            }

            var headers = new List<KeyValuePair<string, string>>();
            for (var i = 1; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.Length == 0)
                {
                    break;
                }

                var separator = line.IndexOf(':');
                if (separator <= 0)
                {
                    throw new InvalidDataException($"Malformed HTTP header '{line}'.");
                }

                headers.Add(new KeyValuePair<string, string>(
                    line[..separator].Trim(),
                    line[(separator + 1)..].Trim()));
            }

            return new ParsedProxyRequest(firstLine[0], firstLine[1], firstLine[2], headers);
        }

        public string? GetHeader(string name)
        {
            foreach (var header in Headers)
            {
                if (header.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    return header.Value;
                }
            }

            return null;
        }

        public byte[] BuildOriginHeader(string originTarget)
        {
            var builder = new StringBuilder();
            builder.Append(Method).Append(' ').Append(originTarget).Append(' ').Append(Version).Append("\r\n");

            var connectionHeaderTokens = Headers
                .Where(header => header.Key.Equals("Connection", StringComparison.OrdinalIgnoreCase))
                .SelectMany(header => header.Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var header in Headers)
            {
                if (header.Key.Equals("Proxy-Connection", StringComparison.OrdinalIgnoreCase) ||
                    header.Key.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase) ||
                    header.Key.Equals("Proxy-Authenticate", StringComparison.OrdinalIgnoreCase) ||
                    header.Key.Equals("Connection", StringComparison.OrdinalIgnoreCase) ||
                    connectionHeaderTokens.Contains(header.Key))
                {
                    continue;
                }

                builder.Append(header.Key).Append(": ").Append(header.Value).Append("\r\n");
            }

            builder.Append("Connection: close\r\n\r\n");
            return Encoding.Latin1.GetBytes(builder.ToString());
        }
    }
}
