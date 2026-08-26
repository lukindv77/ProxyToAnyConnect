using System.Globalization;
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

        var request = Encoding.ASCII.GetBytes(
            $"GET {_options.ProbePath} HTTP/1.1\r\n" +
            $"Host: {_options.ProbeHost}\r\n" +
            "User-Agent: ProxyToAnyConnect/1.0\r\n" +
            "Accept: text/plain\r\n" +
            "Accept-Encoding: identity\r\n" +
            "Connection: close\r\n\r\n");

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

    private static async Task<byte[]> ReadResponseAsync(
        Stream stream,
        int maxResponseBytes,
        CancellationToken cancellationToken)
    {
        using var response = new MemoryStream();
        var buffer = new byte[4096];

        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (response.Length + read > maxResponseBytes)
            {
                throw new IOException("L2TP verification response exceeded the configured size limit.");
            }

            response.Write(buffer, 0, read);
        }

        return response.ToArray();
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

    private static byte[] DecodeChunkedBody(ReadOnlySpan<byte> body)
    {
        using var decoded = new MemoryStream();
        var offset = 0;

        while (true)
        {
            var lineEnd = FindCrlf(body, offset);
            if (lineEnd < 0)
            {
                throw new IOException("Malformed chunked verification response.");
            }

            var sizeText = Encoding.ASCII.GetString(body[offset..lineEnd]);
            var extensionSeparator = sizeText.IndexOf(';');
            if (extensionSeparator >= 0)
            {
                sizeText = sizeText[..extensionSeparator];
            }

            if (!int.TryParse(
                    sizeText.Trim(),
                    NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture,
                    out var chunkSize) ||
                chunkSize < 0)
            {
                throw new IOException("Malformed HTTP chunk size in verification response.");
            }

            offset = lineEnd + 2;
            if (chunkSize == 0)
            {
                return decoded.ToArray();
            }

            if (offset > body.Length - chunkSize - 2)
            {
                throw new IOException("Truncated chunked verification response.");
            }

            decoded.Write(body.Slice(offset, chunkSize));
            offset += chunkSize;

            if (body[offset] != (byte)'\r' || body[offset + 1] != (byte)'\n')
            {
                throw new IOException("Malformed chunk terminator in verification response.");
            }

            offset += 2;
        }
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
