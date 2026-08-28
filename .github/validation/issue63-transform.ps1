Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Replace-Exact {
    param(
        [Parameter(Mandatory = $true)] [string] $Path,
        [Parameter(Mandatory = $true)] [string] $Old,
        [Parameter(Mandatory = $true)] [string] $New,
        [int] $ExpectedCount = 1
    )

    $raw = [IO.File]::ReadAllText($Path)
    $useCrLf = $raw.Contains("`r`n")
    $text = $raw.Replace("`r`n", "`n")
    $oldNormalized = $Old.Replace("`r`n", "`n").TrimEnd("`r", "`n")
    $newNormalized = $New.Replace("`r`n", "`n").TrimEnd("`r", "`n")
    $actualCount = [regex]::Matches($text, [regex]::Escape($oldNormalized)).Count
    if ($actualCount -ne $ExpectedCount) {
        $anchor = (($oldNormalized -split "`n") | Where-Object { $_.Trim().Length -gt 0 } | Select-Object -First 1).Trim()
        throw "Expected $ExpectedCount exact replacement target(s) in '$Path' for '$anchor', found $actualCount."
    }

    $updated = $text.Replace($oldNormalized, $newNormalized)
    if ($useCrLf) {
        $updated = $updated.Replace("`n", "`r`n")
    }
    [IO.File]::WriteAllText($Path, $updated, [Text.UTF8Encoding]::new($false))
}

$verifier = 'src/ProxyToAnyConnect/Vpn/VpnConnectivityVerifier.cs'
$httpTests = 'tests/ProxyToAnyConnect.SelfTests/VerificationHttpParserTests.cs'
$chunkTests = 'tests/ProxyToAnyConnect.SelfTests/VerificationChunkedDecodeSelfTests.cs'

Replace-Exact $verifier @'
    internal static byte[] ParseHttpSuccessBody(ReadOnlySpan<byte> response)
    {
        var metadata = ParseHttpSuccessHeader(response);
        var body = response[metadata.BodyOffset..];
        return metadata.IsChunked ? DecodeChunkedBody(body) : body.ToArray();
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

        return new HttpBodyMetadata(headerEnd + 4, isChunked);
    }

    private static bool TryParseStatusCode(ReadOnlySpan<char> statusLine, out int statusCode)
    {
        statusCode = 0;
        Span<Range> parts = stackalloc Range[3];
        var partCount = statusLine.Split(parts, ' ', StringSplitOptions.RemoveEmptyEntries);
        return partCount >= 2 && int.TryParse(statusLine[parts[1]], out statusCode);
    }
'@ @'
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
            statusLine.Length < 12)
        {
            return false;
        }

        var code = statusLine.Slice(9, 3);
        if (code[0] is < '0' or > '9' ||
            code[1] is < '0' or > '9' ||
            code[2] is < '0' or > '9')
        {
            return false;
        }

        if (statusLine.Length > 12)
        {
            if (statusLine[12] != ' ' || !IsValidHttpFieldValue(statusLine[13..]))
            {
                return false;
            }
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
'@

Replace-Exact $verifier @'
            offset = lineEnd + 2;
            if (chunkSize == 0)
            {
                return decodedLength;
            }
'@ @'
            offset = lineEnd + 2;
            if (chunkSize == 0)
            {
                ValidateChunkedMessageEnd(body, offset);
                return decodedLength;
            }
'@

Replace-Exact $verifier @'
    private static bool TryParseChunkSize(ReadOnlySpan<byte> sizeLine, out int chunkSize)
'@ @'
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
'@

Replace-Exact $verifier @'
    private readonly record struct HttpBodyMetadata(int BodyOffset, bool IsChunked);
'@ @'
    private readonly record struct HttpBodyMetadata(
        int BodyOffset,
        bool IsChunked,
        int? ContentLength);
'@

Replace-Exact $httpTests @'
            ("Verification HTTP 2xx body is accepted", AcceptsSuccessBody),
            ("Verification HTTP redirect is rejected", RejectsRedirect),
            ("Verification HTTP chunked body is decoded", DecodesChunkedBody)
'@ @'
            ("Verification HTTP 2xx body is accepted", AcceptsSuccessBody),
            ("Verification HTTP redirect is rejected", RejectsRedirect),
            ("Verification HTTP malformed status line is rejected", RejectsMalformedStatusLine),
            ("Verification HTTP Content-Length framing is exact", RejectsContentLengthFramingViolations),
            ("Verification HTTP transfer framing ambiguity is rejected", RejectsTransferFramingAmbiguity),
            ("Verification HTTP chunked body is decoded", DecodesChunkedBody),
            ("Verification HTTP chunked trailers are validated", ValidatesChunkedTrailers)
'@

Replace-Exact $httpTests @'
    private static void DecodesChunkedBody()
'@ @'
    private static void RejectsMalformedStatusLine()
    {
        AssertRejected(
            "NOTHTTP 200 OK\r\nContent-Length: 11\r\n\r\n203.0.113.7",
            "Malformed non-HTTP status line was accepted.");
        AssertRejected(
            "HTTP/2 200 OK\r\nContent-Length: 11\r\n\r\n203.0.113.7",
            "Unsupported textual HTTP version was accepted.");
        AssertRejected(
            "HTTP/1.1 0200 OK\r\nContent-Length: 11\r\n\r\n203.0.113.7",
            "Non-three-digit status code was accepted.");
    }

    private static void RejectsContentLengthFramingViolations()
    {
        AssertRejected(
            "HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n203.0.113.7",
            "Bytes after Content-Length: 0 were accepted as verification evidence.");
        AssertRejected(
            "HTTP/1.1 200 OK\r\nContent-Length: 12\r\n\r\n203.0.113.7",
            "Truncated Content-Length body was accepted.");
        AssertRejected(
            "HTTP/1.1 200 OK\r\nContent-Length: 999999999999999999999\r\n\r\n",
            "Overflowing Content-Length was accepted.");
        AssertRejected(
            "HTTP/1.1 200 OK\r\nContent-Length: 11\r\nContent-Length: 11\r\n\r\n203.0.113.7",
            "Duplicate Content-Length fields were accepted by the fail-closed verifier.");
    }

    private static void RejectsTransferFramingAmbiguity()
    {
        AssertRejected(
            "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\nContent-Length: 11\r\n\r\n" +
            "B\r\n203.0.113.7\r\n0\r\n\r\n",
            "Transfer-Encoding plus Content-Length ambiguity was accepted.");
        AssertRejected(
            "HTTP/1.1 200 OK\r\nTransfer-Encoding: gzip, chunked\r\n\r\n" +
            "B\r\n203.0.113.7\r\n0\r\n\r\n",
            "Unsupported transfer coding was accepted because it contained the word chunked.");
    }

    private static void DecodesChunkedBody()
'@

Replace-Exact $httpTests @'
    }
}
'@ @'
    }

    private static void ValidatesChunkedTrailers()
    {
        var valid = Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\n" +
            "Transfer-Encoding: chunked\r\n\r\n" +
            "B\r\n203.0.113.7\r\n" +
            "0\r\nX-Verification: complete\r\n\r\n");
        var body = VpnConnectivityVerifier.ParseHttpSuccessBody(valid);
        if (Encoding.ASCII.GetString(body) != "203.0.113.7")
        {
            throw new InvalidOperationException("Valid chunked trailer framing changed decoded verification body.");
        }

        AssertRejected(
            "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n" +
            "B\r\n203.0.113.7\r\n0\r\n",
            "Incomplete zero-chunk terminator was accepted.");
        AssertRejected(
            "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n" +
            "B\r\n203.0.113.7\r\n0\r\n\r\nextra",
            "Bytes after a complete chunked message were accepted.");
    }

    private static void AssertRejected(string rawResponse, string message)
    {
        try
        {
            _ = VpnConnectivityVerifier.ParseHttpSuccessBody(Encoding.ASCII.GetBytes(rawResponse));
        }
        catch (IOException)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }
}
'@

Replace-Exact $chunkTests @'
        string[] accepted =
        [
            "4\r\nWiki\r\n5\r\npedia\r\n0\r\n\r\n",
            " 4 \t;foo=bar\r\nWiki\r\nA\r\n0123456789\r\n0;ignored=yes\r\ntrailers ignored",
            "00000004\r\nWiki\r\n0\r\nignored",
            "\t4\t;ext=value\r\nWiki\r\n0\r\n",
            "0\r\nanything-after-zero-is-ignored"
        ];
'@ @'
        string[] accepted =
        [
            "4\r\nWiki\r\n5\r\npedia\r\n0\r\n\r\n",
            " 4 \t;foo=bar\r\nWiki\r\nA\r\n0123456789\r\n0;ignored=yes\r\nX-Trailer: complete\r\n\r\n",
            "00000004\r\nWiki\r\n0\r\n\r\n",
            "\t4\t;ext=value\r\nWiki\r\n0\r\n\r\n",
            "0\r\n\r\n"
        ];
'@

Replace-Exact $chunkTests @'
        string[] rejected =
        [
            "4Wiki",
            "G\r\n",
            "-1\r\n",
            "80000000\r\n",
            "4 0\r\n",
            "4\r\nWik",
            "4\r\nWikiXX0\r\n",
            "4\r\nWiki\rX0\r\n"
        ];

        foreach (var raw in rejected)
        {
            var body = Encoding.ASCII.GetBytes(raw);
            var optimizedRejected = ThrowsIOException(
                () => VpnConnectivityVerifier.DecodeChunkedBody(body));
            var predecessorRejected = ThrowsIOException(() => LegacyDecodeChunkedBody(body));
            if (optimizedRejected != predecessorRejected || !optimizedRejected)
            {
                throw new InvalidOperationException(
                    $"Chunk decoder changed rejection behavior for '{Escape(raw)}'.");
            }
        }
'@ @'
        string[] rejected =
        [
            "4Wiki",
            "G\r\n",
            "-1\r\n",
            "80000000\r\n",
            "4 0\r\n",
            "4\r\nWik",
            "4\r\nWikiXX0\r\n",
            "4\r\nWiki\rX0\r\n",
            "0\r\n",
            "0\r\nanything-after-zero-is-ignored",
            "4\r\nWiki\r\n0\r\nignored",
            "4\r\nWiki\r\n0\r\n\r\nextra",
            "0\r\nBadTrailer\r\n\r\n"
        ];

        foreach (var raw in rejected)
        {
            var body = Encoding.ASCII.GetBytes(raw);
            if (!ThrowsIOException(() => VpnConnectivityVerifier.DecodeChunkedBody(body)))
            {
                throw new InvalidOperationException(
                    $"Chunk decoder accepted malformed/incomplete framing for '{Escape(raw)}'.");
            }
        }
'@
