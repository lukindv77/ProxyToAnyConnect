Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Replace-Exact {
    param(
        [Parameter(Mandatory = $true)] [string] $Path,
        [Parameter(Mandatory = $true)] [string] $Old,
        [Parameter(Mandatory = $true)] [string] $New,
        [int] $ExpectedCount = 1
    )

    $text = [IO.File]::ReadAllText($Path).Replace("`r`n", "`n")
    $oldNormalized = $Old.Replace("`r`n", "`n").TrimEnd("`r", "`n")
    $newNormalized = $New.Replace("`r`n", "`n").TrimEnd("`r", "`n")
    $actualCount = [regex]::Matches($text, [regex]::Escape($oldNormalized)).Count
    if ($actualCount -ne $ExpectedCount) {
        throw "Expected $ExpectedCount exact replacement target(s) in '$Path', found $actualCount."
    }

    [IO.File]::WriteAllText(
        $Path,
        $text.Replace($oldNormalized, $newNormalized),
        [Text.UTF8Encoding]::new($false))
}

$requestTests = 'tests/ProxyToAnyConnect.SelfTests/VerificationProbeRequestSelfTests.cs'
$appOptions = 'src/ProxyToAnyConnect/Configuration/AppOptions.cs'

Replace-Exact $requestTests @'
using System.Diagnostics;
using System.Text;
'@ @'
using System.Diagnostics;
using System.Text;
using ProxyToAnyConnect.Configuration;
'@

Replace-Exact $appOptions @'
        if (isAscii)
        {
            if (Uri.CheckHostName(value) != UriHostNameType.Dns)
            {
                return false;
            }

            canonicalHost = value;
            return true;
        }
'@ @'
        if (isAscii)
        {
            if (!IsValidAsciiDnsHost(value))
            {
                return false;
            }

            canonicalHost = value;
            return true;
        }
'@

Replace-Exact $appOptions @'
        if (Uri.CheckHostName(canonicalHost) != UriHostNameType.Dns ||
            canonicalHost.Any(character => character > 0x7f))
        {
            canonicalHost = string.Empty;
            return false;
        }

        return true;
    }

    internal static bool IsValidProbePath(string? value)
'@ @'
        if (!IsValidAsciiDnsHost(canonicalHost))
        {
            canonicalHost = string.Empty;
            return false;
        }

        return true;
    }

    private static bool IsValidAsciiDnsHost(string value)
    {
        if (value.Length is <= 0 or > 253 || value[^1] == '.')
        {
            return false;
        }

        var labelStart = 0;
        for (var index = 0; index <= value.Length; index++)
        {
            if (index != value.Length && value[index] != '.')
            {
                continue;
            }

            var labelLength = index - labelStart;
            if (labelLength is <= 0 or > 63 ||
                value[labelStart] == '-' ||
                value[index - 1] == '-')
            {
                return false;
            }

            for (var labelIndex = labelStart; labelIndex < index; labelIndex++)
            {
                var current = value[labelIndex];
                if (!((current >= 'A' && current <= 'Z') ||
                      (current >= 'a' && current <= 'z') ||
                      (current >= '0' && current <= '9') ||
                      current == '-'))
                {
                    return false;
                }
            }

            labelStart = index + 1;
        }

        return true;
    }

    internal static bool IsValidProbePath(string? value)
'@