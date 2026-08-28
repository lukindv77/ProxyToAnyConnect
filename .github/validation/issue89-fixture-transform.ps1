$ErrorActionPreference = 'Stop'

function Read-Lf([string]$Path) {
    $text = [System.IO.File]::ReadAllText($Path)
    return $text.Replace("`r`n", "`n").Replace("`r", "`n")
}

function Write-Lf([string]$Path, [string]$Text) {
    [System.IO.File]::WriteAllText($Path, $Text, [System.Text.UTF8Encoding]::new($false))
}

function Replace-LiteralOnce(
    [string]$Text,
    [string]$Old,
    [string]$New,
    [string]$Description) {
    $first = $Text.IndexOf($Old, [StringComparison]::Ordinal)
    if ($first -lt 0) { throw "Missing $Description anchor." }
    if ($Text.IndexOf($Old, $first + $Old.Length, [StringComparison]::Ordinal) -ge 0) {
        throw "Expected exactly one $Description anchor."
    }
    return $Text.Substring(0, $first) + $New + $Text.Substring($first + $Old.Length)
}

function Replace-BlockOnce(
    [string]$Text,
    [string]$Start,
    [string]$Tail,
    [string]$New,
    [string]$Description) {
    $startIndex = $Text.IndexOf($Start, [StringComparison]::Ordinal)
    if ($startIndex -lt 0) { throw "Missing $Description start anchor." }
    if ($Text.IndexOf($Start, $startIndex + $Start.Length, [StringComparison]::Ordinal) -ge 0) {
        throw "Expected exactly one $Description start anchor."
    }

    $tailIndex = $Text.IndexOf($Tail, $startIndex, [StringComparison]::Ordinal)
    if ($tailIndex -lt 0) { throw "Missing $Description tail anchor." }
    $closing = "`n        }"
    $closingIndex = $Text.IndexOf($closing, $tailIndex + $Tail.Length, [StringComparison]::Ordinal)
    if ($closingIndex -lt 0) { throw "Missing $Description closing brace." }
    $endIndex = $closingIndex + $closing.Length
    return $Text.Substring(0, $startIndex) + $New + $Text.Substring($endIndex)
}

$addressListPath = 'tests/ProxyToAnyConnect.SelfTests/DnsResponseAddressListSelfTests.cs'
$addressList = Read-Lf $addressListPath
$addressList = Replace-BlockOnce $addressList `
    '        var mixed = L2tpDnsResolver.ParseResponse(BuildMixedResponse(), TransactionId, "example.com");' `
    '            throw new InvalidOperationException("DNS mixed A/CNAME response semantics changed.");' `
    '        AssertAmbiguousResponseRejected(BuildMixedResponse());' `
    'lazy-address mixed-response predecessor expectation'
$addressList = Replace-LiteralOnce $addressList @'
    private static byte[] BuildResponse((ushort Type, uint Ttl, byte[] Data)[] answers)
'@ @'
    private static void AssertAmbiguousResponseRejected(byte[] response)
    {
        try
        {
            L2tpDnsResolver.ParseResponse(response, TransactionId, "example.com");
        }
        catch (IOException)
        {
            return;
        }

        throw new InvalidOperationException("Ambiguous CNAME/A response was accepted by the lazy-address suite.");
    }

    private static byte[] BuildResponse((ushort Type, uint Ttl, byte[] Data)[] answers)
'@ 'lazy-address rejection helper'
Write-Lf $addressListPath $addressList

$aStoragePath = 'tests/ProxyToAnyConnect.SelfTests/DnsAResultStorageSelfTests.cs'
$aStorage = Read-Lf $aStoragePath
$aStorage = Replace-BlockOnce $aStorage `
    '        var mixed = L2tpDnsResolver.ParseResponse(' `
    '            throw new InvalidOperationException("Mixed CNAME/A canonical-name semantics changed.");' `
    '' `
    'A-result mixed-response predecessor expectation'
Write-Lf $aStoragePath $aStorage
