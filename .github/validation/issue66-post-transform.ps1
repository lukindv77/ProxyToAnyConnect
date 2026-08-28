Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Read-Lf([string]$Path) {
    [IO.File]::ReadAllText($Path).Replace("`r`n", "`n")
}

function Write-Lf([string]$Path, [string]$Text) {
    [IO.File]::WriteAllText($Path, $Text.Replace("`r`n", "`n"), [Text.UTF8Encoding]::new($false))
}

$skipPath = 'tests/ProxyToAnyConnect.SelfTests/DnsNameSkipSelfTests.cs'
$skip = Read-Lf $skipPath
$oldSkip = '        var parsed = L2tpDnsResolver.ParseResponse(packet.ToArray(), transactionId);'
$newSkip = '        var parsed = L2tpDnsResolver.ParseResponse(packet.ToArray(), transactionId, "www.example.com");'
if (($skip.Split($oldSkip).Count - 1) -ne 1) { throw 'Unexpected DnsNameSkip parser call surface.' }
$skip = $skip.Replace($oldSkip, $newSkip)
Write-Lf $skipPath $skip

$bindingPath = 'tests/ProxyToAnyConnect.SelfTests/DnsResponseBindingSelfTests.cs'
$binding = Read-Lf $bindingPath
$oldBinding = @'
        packet[rdLengthOffset] = 0;
        packet[rdLengthOffset + 1] = checked((byte)(originalLength + 1));
        packet.Add(0);
        AssertIOException(() => L2tpDnsResolver.ParseResponse(packet, TransactionId, ExpectedHost));
'@.Replace("`r`n", "`n")
$newBinding = @'
        packet[rdLengthOffset] = 0;
        packet[rdLengthOffset + 1] = checked((byte)(originalLength + 1));
        Array.Resize(ref packet, packet.Length + 1);
        AssertIOException(() => L2tpDnsResolver.ParseResponse(packet, TransactionId, ExpectedHost));
'@.Replace("`r`n", "`n")
if (($binding.Split($oldBinding).Count - 1) -ne 1) { throw 'Unexpected malformed-CNAME fixture surface.' }
$binding = $binding.Replace($oldBinding, $newBinding)
Write-Lf $bindingPath $binding
