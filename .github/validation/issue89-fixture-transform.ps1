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

$addressListPath = 'tests/ProxyToAnyConnect.SelfTests/DnsResponseAddressListSelfTests.cs'
$addressList = Read-Lf $addressListPath
$addressList = Replace-LiteralOnce $addressList @'

        var mixed = L2tpDnsResolver.ParseResponse(BuildMixedResponse(), TransactionId, "example.com");
        if (mixed.Addresses.Count != 1 ||
            !mixed.Addresses[0].Equals(IPAddress.Parse("198.51.100.9")) ||
            mixed.CanonicalName != "edge.example.com" ||
            mixed.MinimumTtlSeconds != 30)
        {
            throw new InvalidOperationException("DNS mixed A/CNAME response semantics changed.");
        }
'@ @'

        AssertAmbiguousResponseRejected(BuildMixedResponse());
'@ 'lazy-address mixed-response predecessor expectation'
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
$aStorage = Replace-LiteralOnce $aStorage @'

        var mixed = L2tpDnsResolver.ParseResponse(
            BuildResponse(
            [
                (Type: (ushort)5, Ttl: 20u, Data: EncodeName("edge.example.com")),
                (Type: (ushort)1, Ttl: 50u, Data: new byte[] { 198, 51, 100, 9 })
            ]),
            TransactionId, "example.com");
        AssertAddresses(mixed, ["198.51.100.9"], expectedTtl: 20);
        if (mixed.CanonicalName != "edge.example.com")
        {
            throw new InvalidOperationException("Mixed CNAME/A canonical-name semantics changed.");
        }
'@ '' 'A-result mixed-response predecessor expectation'
Write-Lf $aStoragePath $aStorage
