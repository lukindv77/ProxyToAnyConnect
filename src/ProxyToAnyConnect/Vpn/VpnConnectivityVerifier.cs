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

    public VpnConnectivityVerifier(VerificationOptions options)
    {
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
                _options.ProbeHost,
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
                $"L2TP verification probe could not connect to {_options.ProbeHost}:{_options.ProbePort}.",
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
                TargetHost = _options.ProbeHost
            },
            cancellationToken);

        var request = BuildProbeRequest(_options.ProbeHost, _options.ProbePath);

        await sslStream.WriteAsync(request, cancellationToken);
        await sslStream.FlushAsync(cancellationToken);

        var response = await ReadResponseAsync(
            sslStream,
            _options.MaxResponseBytes,
            cancellationToken);
        var body = ParseHttpSuccessBody(response);
        var observedText = Encoding.ASCII.GetString(body).Trim();

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

        ReadOnlySpan<byte> requestPrefix = "GET "u8;
        ReadOnlySpan<byte> hostPrefix = " HTTP/1.1\r\nHost: "u8;
        ReadOnlySpan<byte> fixedSuffix =
            "\r\nUser-Agent: ProxyToAnyConnect/1.0\r\nAccept: text/plain\r\nAccept-Encoding: identity\r\nConnection: close\r\n\r\n"u8;

        var pathByteCount = Encoding.ASCII.GetByteCount(path);
        var hostByteCount = Encoding.ASCII.GetByteCount(host);
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
        offset += Encoding.ASCII.GetBytes(host.AsSpan(), destination[offset..]);
        fixedSuffix.CopyTo(destination[offset..]);

        return request;
    }

    internal static async Task<byte[]> ReadResponseAsync(
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

            if (length == 0)
            {
                return [];
            }

            var result = GC.AllocateUninitializedArray<byte>(length);
            buffer.AsSpan(0, length).CopyTo(result);
            return result;
        }
        finally
        {
            pool.Return(buffer, clearArray: false);
        }
    }

    internal static byte[] ParseHttpSuccessBody(ReadOnlySpan<byte> response)
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
                $"Verification endpoint returned an unsuccessful HTTP status: '{statusLine.ToString()}'.");
        }

        var isChunked = false;
        var offset = firstLineEnd < 0 ? headerText.Length : firstLineEnd + 2;
        while (offset < headerText.Length)
        {
            var remaining = headerText.AsSpan(offset);
            var lineEnd = remaining.IndexOf("\r\n".AsSpan());
            var line = lineEnd < 0 ? remaining : remaining[..lineEnd];
            if (line.StartsWith("Transfer-Encoding:", StringComparison.OrdinalIgnoreCase) &&
                line.Contains("chunked", StringComparison.OrdinalIgnoreCase))
            {
                isChunked = true;
                break;
            }

            if (lineEnd < 0)
            {
                break;
            }

            offset += lineEnd + 2;
        }

        var body = response[(headerEnd + 4)..];
        return isChunked ? DecodeChunkedBody(body) : body.ToArray();
    }

    private static bool TryParseStatusCode(ReadOnlySpan<char> statusLine, out int statusCode)
    {
        statusCode = 0;
        Span<Range> parts = stackalloc Range[3];
        var partCount = statusLine.Split(parts, ' ', StringSplitOptions.RemoveEmptyEntries);
        return partCount >= 2 && int.TryParse(statusLine[parts[1]], out statusCode);
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
                return decodedLength;
            }

            if (offset > body.Length - 2 || chunkSize > body.Length - offset - 2)
            {
                throw new IOException("Truncated chunked verification response.");
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

    private static bool TryParseChunkSize(ReadOnlySpan<byte> sizeLine, out int chunkSize)
    {
        chunkSize = 0;
        var extensionSeparator = sizeLine.IndexOf((byte)';');
        if (extensionSeparator >= 0)
        {
            sizeLine = sizeLine[..extensionSeparator];
        }

        while (!sizeLine.IsEmpty && IsAsciiWhitespace(sizeLine[0]))
        {
            sizeLine = sizeLine[1..];
        }

        while (!sizeLine.IsEmpty && IsAsciiWhitespace(sizeLine[^1]))
        {
            sizeLine = sizeLine[..^1];
        }

        if (sizeLine.IsEmpty)
        {
            return false;
        }

        uint value = 0;
        foreach (var current in sizeLine)
        {
            var digit = current switch
            {
                >= (byte)'0' and <= (byte)'9' => current - (byte)'0',
                >= (byte)'A' and <= (byte)'F' => current - (byte)'A' + 10,
                >= (byte)'a' and <= (byte)'f' => current - (byte)'a' + 10,
                _ => -1
            };
            if (digit < 0 || value > (uint.MaxValue - (uint)digit) / 16)
            {
                return false;
            }

            value = (value * 16) + (uint)digit;
        }

        if (value > int.MaxValue)
        {
            return false;
        }

        chunkSize = (int)value;
        return true;
    }

    private static bool IsAsciiWhitespace(byte value)
    {
        return value == (byte)' ' || value is >= 0x09 and <= 0x0D;
    }

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
}

internal sealed record VpnVerificationResult(
    IPAddress ProbeTargetIPv4,
    IPAddress? ObservedPublicIPv4,
    bool PublicIPv4ComparisonPerformed,
    IPAddress? ExpectedPublicIPv4);
