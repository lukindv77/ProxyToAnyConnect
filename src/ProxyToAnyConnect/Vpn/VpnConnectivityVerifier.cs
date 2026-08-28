using System.Buffers;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using ProxyToAnyConnect.Configuration;
using ProxyToAnyConnect.Network;

namespace ProxyToAnyConnect.Vpn;

internal sealed class VpnConnectivityVerifier
{
    private const int InitialResponseBufferBytes = 4 * 1024;

    private readonly VerificationOptions _options;
    private readonly L2tpDnsResolver _dnsResolver;
    private readonly string _probeHost;

    public VpnConnectivityVerifier(VerificationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!VerificationOptions.TryGetCanonicalProbeHost(options.ProbeHost, out _probeHost))
        {
            throw new ArgumentException(
                "Verification probe host is not a valid IDNA-canonicalizable DNS host name.",
                nameof(options));
        }

        _options = options;
        _dnsResolver = new L2tpDnsResolver();
    }

    public async Task<VpnVerificationResult> VerifyAsync(
        VpnContext context,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            context.LifetimeToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

        try
        {
            var targetAddresses = await _dnsResolver.ResolveIPv4Async(
                _probeHost,
                context,
                timeout.Token);

            Exception? lastError = null;
            foreach (var targetAddress in targetAddresses)
            {
                try
                {
                    return await ProbeAddressAsync(context, targetAddress, timeout.Token);
                }
                catch (Exception ex) when (ex is SocketException or IOException or AuthenticationException)
                {
                    lastError = ex;
                }
            }

            throw new IOException(
                $"L2TP verification probe could not connect to {_probeHost}:{_options.ProbePort}.",
                lastError);
        }
        catch (OperationCanceledException ex)
            when (!cancellationToken.IsCancellationRequested &&
                  !context.LifetimeToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"L2TP verification timed out after {_options.TimeoutSeconds} seconds.",
                ex);
        }
    }

    private async Task<VpnVerificationResult> ProbeAddressAsync(
        VpnContext context,
        IPAddress targetAddress,
        CancellationToken cancellationToken)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true
        };

        WindowsSocketInterfaceBinder.BindToIPv4Interface(socket, context.InterfaceIndex);
        socket.Bind(new IPEndPoint(context.LocalIPv4, 0));
        await socket.ConnectAsync(
            new IPEndPoint(targetAddress, _options.ProbePort),
            cancellationToken);

        if (!context.IsAlive)
        {
            throw new IOException("L2TP disappeared while the verification socket was connecting.");
        }

        await using var networkStream = new NetworkStream(socket, ownsSocket: false);
        await using var sslStream = new SslStream(networkStream, leaveInnerStreamOpen: true);
        await sslStream.AuthenticateAsClientAsync(
            new SslClientAuthenticationOptions
            {
                TargetHost = _probeHost
            },
            cancellationToken);

        var request = BuildProbeRequest(_probeHost, _options.ProbePath);

        await sslStream.WriteAsync(request, cancellationToken);
        await sslStream.FlushAsync(cancellationToken);

        using var response = await ReadPooledResponseAsync(
            sslStream,
            _options.MaxResponseBytes,
            cancellationToken);
        var body = ParseHttpSuccessBodyView(response.Memory);
        var observedText = Encoding.ASCII.GetString(body.Span).Trim();

        var expectedIp = TryGetExpectedPublicIPv4(_options.PublicAddress);
        IPAddress? observedIp = null;

        if (IPAddress.TryParse(observedText, out var parsedObservedIp) &&
            parsedObservedIp.AddressFamily == AddressFamily.InterNetwork)
        {
            observedIp = parsedObservedIp;
        }

        if (expectedIp is not null)
        {
            if (observedIp is null)
            {
                throw new IOException(
                    $"Verification endpoint did not return an IPv4 address. Response body: '{observedText}'.");
            }

            if (!observedIp.Equals(expectedIp))
            {
                throw new IOException(
                    $"L2TP public IPv4 verification failed. Expected {expectedIp}, observed {observedIp}.");
            }
        }

        return new VpnVerificationResult(
            targetAddress,
            observedIp,
            expectedIp is not null,
            expectedIp);
    }

    internal static IPAddress? TryGetExpectedPublicIPv4(string publicAddress)
    {
        if (!IPAddress.TryParse(publicAddress, out var address) ||
            address.AddressFamily != AddressFamily.InterNetwork)
        {
            return null;
        }

        return address;
    }

    internal static byte[] BuildProbeRequest(string? host, string? path)
    {
        host ??= string.Empty;
        path ??= string.Empty;

        if (!VerificationOptions.TryGetCanonicalProbeHost(host, out var canonicalHost))
        {
            throw new ArgumentException(
                "Verification probe host must be a valid IDNA-canonicalizable DNS host name.",
                nameof(host));
        }

        if (!VerificationOptions.IsValidProbePath(path))
        {
            throw new ArgumentException(
                "Verification probe path must be an ASCII HTTP origin-form request-target without a fragment.",
                nameof(path));
        }

        ReadOnlySpan<byte> requestPrefix = "GET "u8;
        ReadOnlySpan<byte> hostPrefix = " HTTP/1.1\r\nHost: "u8;
        ReadOnlySpan<byte> fixedSuffix =
            "\r\nUser-Agent: ProxyToAnyConnect/1.0\r\nAccept: text/plain\r\nAccept-Encoding: identity\r\nConnection: close\r\n\r\n"u8;

        var pathByteCount = Encoding.ASCII.GetByteCount(path);
        var hostByteCount = Encoding.ASCII.GetByteCount(canonicalHost);
        var totalLength = checked(
            requestPrefix.Length +
            pathByteCount +
            hostPrefix.Length +
            hostByteCount +
            fixedSuffix.Length);

        var request = GC.AllocateUninitializedArray<byte>(totalLength);
        var destination = request.AsSpan();
        var offset = 0;

        requestPrefix.CopyTo(destination[offset..]);
        offset += requestPrefix.Length;
        offset += Encoding.ASCII.GetBytes(path.AsSpan(), destination[offset..]);
        hostPrefix.CopyTo(destination[offset..]);
        offset += hostPrefix.Length;
        offset += Encoding.ASCII.GetBytes(canonicalHost.AsSpan(), destination[offset..]);
        fixedSuffix.CopyTo(destination[offset..]);

        return request;
    }

    internal static async Task<byte[]> ReadResponseAsync(
        Stream stream,
        int maxResponseBytes,
        CancellationToken cancellationToken)
    {
        using var response = await ReadPooledResponseAsync(
            stream,
            maxResponseBytes,
            cancellationToken);
        if (response.Length == 0)
        {
            return [];
        }

        var result = GC.AllocateUninitializedArray<byte>(response.Length);
        response.Memory.Span.CopyTo(result);
        return result;
    }

    internal static async Task<PooledResponseOwner> ReadPooledResponseAsync(
        Stream stream,
        int maxResponseBytes,
        CancellationToken cancellationToken)
    {
        var pool = ArrayPool<byte>.Shared;
        var maximumBufferedBytes = maxResponseBytes switch
        {
            < 0 => 1,
            int.MaxValue => int.MaxValue,
            _ => maxResponseBytes + 1
        };
        var buffer = pool.Rent(Math.Max(1, Math.Min(InitialResponseBufferBytes, maximumBufferedBytes)));
        var length = 0;

        try
        {
            while (true)
            {
                var writable = Math.Min(
                    buffer.Length - length,
                    maximumBufferedBytes - length);
                if (writable == 0)
                {
                    if (length == maximumBufferedBytes)
                    {
                        var overflowProbe = pool.Rent(1);
                        try
                        {
                            var overflowRead = await stream.ReadAsync(
                                overflowProbe.AsMemory(0, 1),
                                cancellationToken);
                            if (overflowRead == 0)
                            {
                                break;
                            }

                            throw new IOException(
                                "L2TP verification response exceeded the configured size limit.");
                        }
                        finally
                        {
                            pool.Return(overflowProbe, clearArray: false);
                        }
                    }

                    var doubledCapacity = buffer.Length <= int.MaxValue / 2
                        ? buffer.Length * 2
                        : int.MaxValue;
                    var nextCapacity = Math.Min(
                        maximumBufferedBytes,
                        Math.Max(length + 1, doubledCapacity));
                    var replacement = pool.Rent(nextCapacity);
                    buffer.AsSpan(0, length).CopyTo(replacement);
                    pool.Return(buffer, clearArray: false);
                    buffer = replacement;
                    continue;
                }

                var read = await stream.ReadAsync(
                    buffer.AsMemory(length, writable),
                    cancellationToken);
                if (read == 0)
                {
                    break;
                }

                length += read;
                if (length > maxResponseBytes)
                {
                    throw new IOException(
                        "L2TP verification response exceeded the configured size limit.");
                }
            }

            return new PooledResponseOwner(buffer, length);
        }
        catch
        {
            pool.Return(buffer, clearArray: false);
            throw;
        }
    }

    internal static byte[] ParseHttpSuccessBody(ReadOnlySpan<byte> response)
    {
        var metadata = ParseHttpSuccessHeader(response);
        var body = response[metadata.BodyOffset..];
        if (metadata.IsChunked)
        {
            return DecodeChunkedBody(body);
        }

        if (metadata.ContentLength is int contentLength)
        {
            EnsureExactContentLength(body.Length, contentLength);
            return body[..contentLength].ToArray();
        }

        return body.ToArray();
    }

    internal static ReadOnlyMemory<byte> ParseHttpSuccessBodyView(byte[] response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return ParseHttpSuccessBodyView(response.AsMemory());
    }

    internal static ReadOnlyMemory<byte> ParseHttpSuccessBodyView(ReadOnlyMemory<byte> response)
    {
        var metadata = ParseHttpSuccessHeader(response.Span);
        var body = response[metadata.BodyOffset..];
        if (metadata.IsChunked)
        {
            return DecodeChunkedBody(body.Span);
        }

        if (metadata.ContentLength is int contentLength)
        {
            EnsureExactContentLength(body.Length, contentLength);
            return body[..contentLength];
        }

        // HTTP/1.x close-delimited responses remain supported because the verifier
        // explicitly sends Connection: close and ReadPooledResponseAsync owns EOF.
        return body;
    }

    private static HttpBodyMetadata ParseHttpSuccessHeader(ReadOnlySpan<byte> response)
    {
        var headerEnd = FindHeaderEnd(response);
        if (headerEnd < 0)
        {
            throw new IOException("Verification endpoint returned an incomplete HTTP response.");
        }

        var headerBytes = response[..headerEnd];
        var headerText = Encoding.Latin1.GetString(headerBytes);
        var firstLineEnd = headerText.IndexOf("\r\n", StringComparison.Ordinal);
        var statusLine = firstLineEnd < 0
            ? headerText.AsSpan()
            : headerText.AsSpan(0, firstLineEnd);
        if (!TryParseStatusCode(statusLine, out var statusCode) ||
            statusCode is < 200 or >= 300)
        {
            throw new IOException(
                $"Verification endpoint returned an invalid or unsuccessful HTTP status: '{statusLine.ToString()}'.");
        }

        var transferEncodingSeen = false;
        var isChunked = false;
        int? contentLength = null;
        var offset = firstLineEnd < 0 ? headerText.Length : firstLineEnd + 2;
        while (offset < headerText.Length)
        {
            var remaining = headerText.AsSpan(offset);
            var lineEnd = remaining.IndexOf("\r\n".AsSpan());
            var line = lineEnd < 0 ? remaining : remaining[..lineEnd];
            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                throw new IOException("Verification endpoint returned a malformed HTTP header field.");
            }

            var name = line[..colon];
            var value = TrimHttpOws(line[(colon + 1)..]);
            if (!IsValidHttpFieldName(name) || !IsValidHttpFieldValue(value))
            {
                throw new IOException("Verification endpoint returned an invalid HTTP header field.");
            }

            if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
            {
                if (transferEncodingSeen)
                {
                    throw new IOException("Verification endpoint returned duplicate Transfer-Encoding fields.");
                }

                transferEncodingSeen = true;
                if (!value.Equals("chunked", StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException(
                        "Verification endpoint returned an unsupported Transfer-Encoding; only exact 'chunked' is accepted.");
                }

                isChunked = true;
            }
            else if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                if (contentLength is not null || !TryParseContentLength(value, out var parsedLength))
                {
                    throw new IOException("Verification endpoint returned an invalid or duplicate Content-Length field.");
                }

                contentLength = parsedLength;
            }

            if (lineEnd < 0)
            {
                break;
            }

            offset += lineEnd + 2;
        }

        if (isChunked && contentLength is not null)
        {
            throw new IOException(
                "Verification endpoint returned ambiguous HTTP framing with both Transfer-Encoding and Content-Length.");
        }

        return new HttpBodyMetadata(headerEnd + 4, isChunked, contentLength);
    }

    private static bool TryParseStatusCode(ReadOnlySpan<char> statusLine, out int statusCode)
    {
        statusCode = 0;
        if (!(statusLine.StartsWith("HTTP/1.1 ", StringComparison.Ordinal) ||
              statusLine.StartsWith("HTTP/1.0 ", StringComparison.Ordinal)) ||
            statusLine.Length < 13)
        {
            return false;
        }

        var code = statusLine.Slice(9, 3);
        if (code[0] is < '0' or > '9' ||
            code[1] is < '0' or > '9' ||
            code[2] is < '0' or > '9' ||
            statusLine[12] != ' ' ||
            !IsValidHttpFieldValue(statusLine[13..]))
        {
            return false;
        }

        statusCode = ((code[0] - '0') * 100) + ((code[1] - '0') * 10) + (code[2] - '0');
        return true;
    }
    private static ReadOnlySpan<char> TrimHttpOws(ReadOnlySpan<char> value)
    {
        while (!value.IsEmpty && value[0] is ' ' or '\t')
        {
            value = value[1..];
        }

        while (!value.IsEmpty && value[^1] is ' ' or '\t')
        {
            value = value[..^1];
        }

        return value;
    }

    private static bool IsValidHttpFieldName(ReadOnlySpan<char> name)
    {
        if (name.IsEmpty)
        {
            return false;
        }

        foreach (var current in name)
        {
            if (!(char.IsAsciiLetterOrDigit(current) ||
                  current is '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '.' or '^' or '_' or '`' or '|' or '~'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsValidHttpFieldValue(ReadOnlySpan<char> value)
    {
        foreach (var current in value)
        {
            if (current != '\t' && (current < ' ' || current == '\x7f'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryParseContentLength(ReadOnlySpan<char> value, out int contentLength)
    {
        contentLength = 0;
        if (value.IsEmpty)
        {
            return false;
        }

        foreach (var current in value)
        {
            if (current is < '0' or > '9')
            {
                return false;
            }

            var digit = current - '0';
            if (contentLength > (int.MaxValue - digit) / 10)
            {
                return false;
            }

            contentLength = (contentLength * 10) + digit;
        }

        return true;
    }

    private static void EnsureExactContentLength(int actualLength, int expectedLength)
    {
        if (actualLength == expectedLength)
        {
            return;
        }

        throw new IOException(
            actualLength < expectedLength
                ? "Verification endpoint returned a truncated Content-Length body."
                : "Verification endpoint returned bytes after the declared Content-Length body.");
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

    internal static byte[] DecodeChunkedBody(ReadOnlySpan<byte> body)
    {
        var decodedLength = ScanChunkedBody(body, destination: default, copyPayload: false);
        if (decodedLength == 0)
        {
            return [];
        }

        var decoded = GC.AllocateUninitializedArray<byte>(decodedLength);
        var copiedLength = ScanChunkedBody(body, decoded, copyPayload: true);
        if (copiedLength != decodedLength)
        {
            throw new IOException("Chunked verification response changed while decoding.");
        }

        return decoded;
    }

    private static int ScanChunkedBody(
        ReadOnlySpan<byte> body,
        Span<byte> destination,
        bool copyPayload)
    {
        var offset = 0;
        var decodedLength = 0;

        while (true)
        {
            var lineEnd = FindCrlf(body, offset);
            if (lineEnd < 0)
            {
                throw new IOException("Malformed chunked verification response.");
            }

            if (!TryParseChunkSize(body[offset..lineEnd], out var chunkSize))
            {
                throw new IOException("Malformed HTTP chunk size in verification response.");
            }

            offset = lineEnd + 2;
            if (chunkSize == 0)
            {
                ValidateChunkedMessageEnd(body, offset);
                return decodedLength;
            }

            if (offset > body.Length - 2 || chunkSize > body.Length - offset - 2)
            {
                throw new IOException(
                    "Truncated chunked verification response.");
            }

            if (copyPayload)
            {
                body.Slice(offset, chunkSize).CopyTo(destination[decodedLength..]);
            }

            decodedLength = checked(decodedLength + chunkSize);
            offset += chunkSize;

            if (body[offset] != (byte)'\r' || body[offset + 1] != (byte)'\n')
            {
                throw new IOException("Malformed chunk terminator in verification response.");
            }

            offset += 2;
        }
    }

    private static void ValidateChunkedMessageEnd(ReadOnlySpan<byte> body, int offset)
    {
        while (true)
        {
            var lineEnd = FindCrlf(body, offset);
            if (lineEnd < 0)
            {
                throw new IOException(
                    "Chunked verification response ended before the trailer terminator.");
            }

            var trailerLine = body[offset..lineEnd];
            offset = lineEnd + 2;
            if (trailerLine.IsEmpty)
            {
                if (offset != body.Length)
                {
                    throw new IOException(
                        "Verification endpoint returned bytes after the complete chunked message.");
                }

                return;
            }

            if (!IsValidTrailerField(trailerLine))
            {
                throw new IOException("Verification endpoint returned a malformed HTTP trailer field.");
            }
        }
    }

    private static bool IsValidTrailerField(ReadOnlySpan<byte> line)
    {
        var colon = line.IndexOf((byte)':');
        if (colon <= 0)
        {
            return false;
        }

        for (var index = 0; index < colon; index++)
        {
            if (!IsHttpTokenByte(line[index]))
            {
                return false;
            }
        }

        for (var index = colon + 1; index < line.Length; index++)
        {
            var current = line[index];
            if (current != (byte)'\t' && (current < 0x20 || current == 0x7f))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsHttpTokenByte(byte value) =>
        value is >= (byte)'0' and <= (byte)'9' or
            >= (byte)'A' and <= (byte)'Z' or
            >= (byte)'a' and <= (byte)'z' ||
        value is (byte)'!' or (byte)'#' or (byte)'$' or (byte)'%' or (byte)'&' or
            (byte)'\'' or (byte)'*' or (byte)'+' or (byte)'-' or (byte)'.' or
            (byte)'^' or (byte)'_' or (byte)'`' or (byte)'|' or (byte)'~';

    private static bool TryParseChunkSize(ReadOnlySpan<byte> sizeLine, out int chunkSize)
    {
        chunkSize = 0;
        if (sizeLine.IsEmpty)
        {
            return false;
        }

        var offset = 0;
        uint value = 0;
        while (offset < sizeLine.Length && TryGetHexDigit(sizeLine[offset], out var digit))
        {
            if (value > (uint.MaxValue - (uint)digit) / 16)
            {
                return false;
            }

            value = (value * 16) + (uint)digit;
            offset++;
        }

        if (offset == 0 || value > int.MaxValue)
        {
            return false;
        }

        if (offset < sizeLine.Length && !TryParseChunkExtensions(sizeLine, ref offset))
        {
            return false;
        }

        if (offset != sizeLine.Length)
        {
            return false;
        }

        chunkSize = (int)value;
        return true;
    }

    private static bool TryParseChunkExtensions(ReadOnlySpan<byte> line, ref int offset)
    {
        while (offset < line.Length)
        {
            SkipChunkBws(line, ref offset);
            if (offset >= line.Length || line[offset] != (byte)';')
            {
                return false;
            }

            offset++;
            SkipChunkBws(line, ref offset);

            var nameStart = offset;
            while (offset < line.Length && IsHttpTokenByte(line[offset]))
            {
                offset++;
            }

            if (offset == nameStart)
            {
                return false;
            }

            var afterName = offset;
            var equalsOffset = offset;
            SkipChunkBws(line, ref equalsOffset);
            if (equalsOffset < line.Length && line[equalsOffset] == (byte)'=')
            {
                offset = equalsOffset + 1;
                SkipChunkBws(line, ref offset);
                if (!TrySkipChunkExtensionValue(line, ref offset))
                {
                    return false;
                }
            }
            else
            {
                // BWS after a valueless extension name belongs to the next
                // `BWS ";"` production, not to the name itself. Restoring the
                // offset makes trailing whitespace without another extension fail.
                offset = afterName;
            }
        }

        return true;
    }

    private static bool TrySkipChunkExtensionValue(ReadOnlySpan<byte> line, ref int offset)
    {
        if (offset >= line.Length)
        {
            return false;
        }

        if (line[offset] != (byte)'"')
        {
            var tokenStart = offset;
            while (offset < line.Length && IsHttpTokenByte(line[offset]))
            {
                offset++;
            }
            return offset != tokenStart;
        }

        offset++;
        while (offset < line.Length)
        {
            var current = line[offset++];
            if (current == (byte)'"')
            {
                return true;
            }

            if (current == (byte)'\\')
            {
                if (offset >= line.Length || !IsValidQuotedPairByte(line[offset]))
                {
                    return false;
                }
                offset++;
                continue;
            }

            if (!IsValidQdTextByte(current))
            {
                return false;
            }
        }

        return false;
    }

    private static void SkipChunkBws(ReadOnlySpan<byte> line, ref int offset)
    {
        while (offset < line.Length && line[offset] is (byte)' ' or (byte)'\t')
        {
            offset++;
        }
    }

    private static bool IsValidQdTextByte(byte value) =>
        value is (byte)'\t' or (byte)' ' or 0x21 or
            >= 0x23 and <= 0x5B or
            >= 0x5D and <= 0x7E or
            >= 0x80;

    private static bool IsValidQuotedPairByte(byte value) =>
        value is (byte)'\t' or (byte)' ' or
            >= 0x21 and <= 0x7E or
            >= 0x80;

    private static bool TryGetHexDigit(byte value, out int digit)
    {
        digit = value switch
        {
            >= (byte)'0' and <= (byte)'9' => value - (byte)'0',
            >= (byte)'A' and <= (byte)'F' => value - (byte)'A' + 10,
            >= (byte)'a' and <= (byte)'f' => value - (byte)'a' + 10,
            _ => -1
        };
        return digit >= 0;
    }
    private readonly record struct HttpBodyMetadata(
        int BodyOffset,
        bool IsChunked,
        int? ContentLength);

    private static int FindCrlf(ReadOnlySpan<byte> data, int start)
    {
        for (var i = start; i < data.Length - 1; i++)
        {
            if (data[i] == (byte)'\r' && data[i + 1] == (byte)'\n')
            {
                return i;
            }
        }

        return -1;
    }

    internal sealed class PooledResponseOwner : IDisposable
    {
        private byte[]? _buffer;

        internal PooledResponseOwner(byte[] buffer, int length)
        {
            _buffer = buffer;
            Length = length;
        }

        internal int Length { get; }

        internal ReadOnlyMemory<byte> Memory
        {
            get
            {
                var buffer = _buffer ?? throw new ObjectDisposedException(nameof(PooledResponseOwner));
                return buffer.AsMemory(0, Length);
            }
        }

        public void Dispose()
        {
            var buffer = Interlocked.Exchange(ref _buffer, null);
            if (buffer is not null)
            {
                ArrayPool<byte>.Shared.Return(buffer, clearArray: false);
            }
        }
    }
}

internal sealed record VpnVerificationResult(
    IPAddress ProbeTargetIPv4,
    IPAddress? ObservedPublicIPv4,
    bool PublicIPv4ComparisonPerformed,
    IPAddress? ExpectedPublicIPv4);
