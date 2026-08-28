$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

function Read-Lf([string]$Path) {
    $text = [System.IO.File]::ReadAllText($Path)
    return $text.Replace("`r`n", "`n").Replace("`r", "`n")
}

function Write-Lf([string]$Path, [string]$Text) {
    [System.IO.File]::WriteAllText(
        $Path,
        $Text,
        [System.Text.UTF8Encoding]::new($false))
}

function Replace-RegexOnce(
    [string]$Text,
    [string]$Pattern,
    [string]$Replacement,
    [string]$Description) {
    $matches = [regex]::Matches($Text, $Pattern)
    if ($matches.Count -ne 1) {
        throw "Expected exactly one $Description anchor, got $($matches.Count)."
    }
    return [regex]::Replace($Text, $Pattern, $Replacement, 1)
}

function Replace-LiteralOnce(
    [string]$Text,
    [string]$Old,
    [string]$New,
    [string]$Description) {
    $first = $Text.IndexOf($Old, [StringComparison]::Ordinal)
    if ($first -lt 0) {
        throw "Missing $Description anchor."
    }
    if ($Text.IndexOf($Old, $first + $Old.Length, [StringComparison]::Ordinal) -ge 0) {
        throw "Expected exactly one $Description anchor."
    }
    return $Text.Substring(0, $first) + $New + $Text.Substring($first + $Old.Length)
}

$verifierPath = 'src/ProxyToAnyConnect/Vpn/VpnConnectivityVerifier.cs'
$verifier = Read-Lf $verifierPath

$statusPattern = '(?ms)^    private static bool TryParseStatusCode\(ReadOnlySpan<char> statusLine, out int statusCode\)\n    \{.*?^    \}\n\n(?=    private static ReadOnlySpan<char> TrimHttpOws)'
$statusReplacement = @'
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

'@
$verifier = Replace-RegexOnce $verifier $statusPattern $statusReplacement 'status parser'

$chunkPattern = '(?ms)^    private static bool TryParseChunkSize\(ReadOnlySpan<byte> sizeLine, out int chunkSize\)\n    \{.*?^    private static bool IsAsciiWhitespace\(byte value\)\n    \{.*?^    \}\n\n(?=    private readonly record struct HttpBodyMetadata)'
$chunkReplacement = @'
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

'@
$verifier = Replace-RegexOnce $verifier $chunkPattern $chunkReplacement 'chunk-size parser'
Write-Lf $verifierPath $verifier

$chunkTestsPath = 'tests/ProxyToAnyConnect.SelfTests/VerificationChunkedDecodeSelfTests.cs'
$chunkTests = Read-Lf $chunkTestsPath
$chunkTests = Replace-LiteralOnce $chunkTests @'
            " 4 \t;foo=bar\r\nWiki\r\nA\r\n0123456789\r\n0;ignored=yes\r\nX-Trailer: complete\r\n\r\n",
'@ @'
            "4 \t; \tfoo \t= \tbar;quoted=\"a\\\"b\"\r\nWiki\r\nA\r\n0123456789\r\n0;ignored=yes\r\nX-Trailer: complete\r\n\r\n",
'@ 'valid chunk-extension fixture'
$chunkTests = Replace-LiteralOnce $chunkTests @'
            "\t4\t;ext=value\r\nWiki\r\n0\r\n\r\n",
'@ @'
            "4;ext=value\r\nWiki\r\n0\r\n\r\n",
'@ 'tab-trim predecessor fixture'
$rejectedAnchor = @'
            "4 0\r\n",
'@
$rejectedReplacement = @'
            "4 0\r\n",
            " 4\r\nWiki\r\n0\r\n\r\n",
            "\t4\r\nWiki\r\n0\r\n\r\n",
            "\v4\r\nWiki\r\n0\r\n\r\n",
            "\f4\r\nWiki\r\n0\r\n\r\n",
            "4 \r\nWiki\r\n0\r\n\r\n",
            "4;\r\nWiki\r\n0\r\n\r\n",
            "4;=value\r\nWiki\r\n0\r\n\r\n",
            "4;bad name=value\r\nWiki\r\n0\r\n\r\n",
            "4;name=\"unterminated\r\nWiki\r\n0\r\n\r\n",
            "4;name=value garbage\r\nWiki\r\n0\r\n\r\n",
            "4;name=\"bad\rvalue\"\r\nWiki\r\n0\r\n\r\n",
'@
$chunkTests = Replace-LiteralOnce $chunkTests $rejectedAnchor $rejectedReplacement 'malformed chunk matrix'
Write-Lf $chunkTestsPath $chunkTests

$httpTestsPath = 'tests/ProxyToAnyConnect.SelfTests/VerificationHttpParserTests.cs'
$httpTests = Read-Lf $httpTestsPath
$statusRejectAnchor = @'
        AssertRejected(
            "HTTP/1.1 0200 OK\r\nContent-Length: 11\r\n\r\n203.0.113.7",
            "Non-three-digit status code was accepted.");
'@
$statusRejectReplacement = @'
        AssertRejected(
            "HTTP/1.1 0200 OK\r\nContent-Length: 11\r\n\r\n203.0.113.7",
            "Non-three-digit status code was accepted.");
        AssertRejected(
            "HTTP/1.1 200\r\nContent-Length: 11\r\n\r\n203.0.113.7",
            "Status line without the required SP after status-code was accepted.");
'@
$httpTests = Replace-LiteralOnce $httpTests $statusRejectAnchor $statusRejectReplacement 'missing status separator regression'
$successAnchor = @'
        if (Encoding.ASCII.GetString(body) != "203.0.113.7")
        {
            throw new InvalidOperationException("Successful verification response body was parsed incorrectly.");
        }
'@
$successReplacement = @'
        if (Encoding.ASCII.GetString(body) != "203.0.113.7")
        {
            throw new InvalidOperationException("Successful verification response body was parsed incorrectly.");
        }

        var emptyReason = Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 \r\n" +
            "Content-Length: 11\r\n\r\n" +
            "203.0.113.7");
        var emptyReasonBody = VpnConnectivityVerifier.ParseHttpSuccessBody(emptyReason);
        if (Encoding.ASCII.GetString(emptyReasonBody) != "203.0.113.7")
        {
            throw new InvalidOperationException(
                "Status line with the required separator and an empty reason phrase was rejected.");
        }
'@
$httpTests = Replace-LiteralOnce $httpTests $successAnchor $successReplacement 'empty reason phrase positive regression'
Write-Lf $httpTestsPath $httpTests
