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
        throw "Expected $ExpectedCount replacement target(s) in '$Path', found $actualCount."
    }

    $updated = $text.Replace($oldNormalized, $newNormalized)
    if ($useCrLf) { $updated = $updated.Replace("`n", "`r`n") }
    [IO.File]::WriteAllText($Path, $updated, [Text.UTF8Encoding]::new($false))
}

$path = 'tests/ProxyToAnyConnect.SelfTests/VerificationParserSetupSelfTests.cs'

Replace-Exact $path @'
        string[] accepted =
        [
            "HTTP/1.1 200 OK\r\nContent-Length: 11\r\n\r\n203.0.113.7",
            "HTTP/1.1   204   No Content\r\nX-Test: yes\r\n\r\n",
            "HTTP/1.1 200 OK\r\ntRaNsFeR-EnCoDiNg: x-chunked-value\r\n\r\nB\r\n203.0.113.7\r\n0\r\n\r\n"
        ];
'@ @'
        string[] accepted =
        [
            "HTTP/1.1 200 OK\r\nContent-Length: 11\r\n\r\n203.0.113.7",
            "HTTP/1.1 204 No Content\r\nX-Test: yes\r\nContent-Length: 0\r\n\r\n",
            "HTTP/1.1 200 OK\r\ntRaNsFeR-EnCoDiNg: chunked\r\n\r\nB\r\n203.0.113.7\r\n0\r\n\r\n"
        ];
'@

Replace-Exact $path @'
        string[] rejected =
        [
            "HTTP/1.1 302 Found\r\nLocation: https://other.example/\r\n\r\n",
            "HTTP/1.1 nope OK\r\nContent-Length: 0\r\n\r\n",
            "HTTP/1.1 500 Error\r\nContent-Length: 0\r\n\r\n",
            "HTTP/1.1 200 OK\r\nContent-Length: 0\r\n"
        ];

        foreach (var raw in rejected)
        {
            var response = Encoding.ASCII.GetBytes(raw);
            var optimizedRejected = ThrowsIOException(
                () => VpnConnectivityVerifier.ParseHttpSuccessBody(response));
            var predecessorRejected = ThrowsIOException(() => SplitLinqPredecessor(response));
            if (optimizedRejected != predecessorRejected || !optimizedRejected)
            {
                throw new InvalidOperationException(
                    "Verification parser changed rejection behavior for malformed/unsuccessful response.");
            }
        }
'@ @'
        string[] rejected =
        [
            "HTTP/1.1 302 Found\r\nLocation: https://other.example/\r\n\r\n",
            "HTTP/1.1 nope OK\r\nContent-Length: 0\r\n\r\n",
            "HTTP/1.1 500 Error\r\nContent-Length: 0\r\n\r\n",
            "HTTP/1.1 200 OK\r\nContent-Length: 0\r\n",
            "HTTP/1.1   204   No Content\r\nContent-Length: 0\r\n\r\n",
            "HTTP/1.1 200 OK\r\nTransfer-Encoding: x-chunked-value\r\n\r\nB\r\n203.0.113.7\r\n0\r\n\r\n"
        ];

        foreach (var raw in rejected)
        {
            var response = Encoding.ASCII.GetBytes(raw);
            if (!ThrowsIOException(() => VpnConnectivityVerifier.ParseHttpSuccessBody(response)))
            {
                throw new InvalidOperationException(
                    "Verification parser accepted malformed, ambiguous or unsupported response framing.");
            }
        }
'@
