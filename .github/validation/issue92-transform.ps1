$ErrorActionPreference = 'Stop'

function Read-Lf([string]$Path) {
    $text = [System.IO.File]::ReadAllText($Path)
    return $text.Replace("`r`n", "`n").Replace("`r", "`n")
}

function Write-Lf([string]$Path, [string]$Text) {
    [System.IO.File]::WriteAllText($Path, $Text, [System.Text.UTF8Encoding]::new($false))
}

function Replace-LiteralOnce([string]$Text, [string]$Old, [string]$New, [string]$Description) {
    $first = $Text.IndexOf($Old, [StringComparison]::Ordinal)
    if ($first -lt 0) { throw "Missing $Description anchor." }
    if ($Text.IndexOf($Old, $first + $Old.Length, [StringComparison]::Ordinal) -ge 0) {
        throw "Expected exactly one $Description anchor."
    }
    return $Text.Substring(0, $first) + $New + $Text.Substring($first + $Old.Length)
}

$resolverPath = 'src/ProxyToAnyConnect/Network/L2tpDnsResolver.cs'
$resolver = Read-Lf $resolverPath
$resolver = Replace-LiteralOnce $resolver @'
        var truncated = (flags & 0x0200) != 0;
        var responseCode = flags & 0x000F;
'@ @'
        var opcode = flags & 0x7800;
        if (opcode != 0)
        {
            throw new IOException("DNS response opcode does not match the standard QUERY request.");
        }

        var truncated = (flags & 0x0200) != 0;
        var responseCode = flags & 0x000F;
'@ 'DNS response opcode validation'
$resolver = Replace-LiteralOnce $resolver @'
            if (ownedByQuery && recordClass == 1 && type == 1 && dataLength == 4)
            {
                if (canonicalName is not null)
'@ @'
            if (ownedByQuery && recordClass == 1 && type == 1)
            {
                if (dataLength != 4)
                {
                    throw new IOException("DNS A RDATA for the queried owner must be exactly four bytes.");
                }

                if (canonicalName is not null)
'@ 'owned A RDATA validation'
Write-Lf $resolverPath $resolver

$testsPath = 'tests/ProxyToAnyConnect.SelfTests/DnsResponseBindingSelfTests.cs'
$tests = Read-Lf $testsPath
$tests = Replace-LiteralOnce $tests @'
            NonSingleQuestionIsRejected();
            UnrelatedAnswerOwnersAreIgnored();
'@ @'
            NonSingleQuestionIsRejected();
            NonQueryOpcodeIsRejected();
            OrdinaryResponseFlagsRemainAccepted();
            MalformedOwnedAddressRdataIsRejected();
            UnrelatedAnswerOwnersAreIgnored();
'@ 'DNS response grammar test registration'
$tests = Replace-LiteralOnce $tests @'
    private static void UnrelatedAnswerOwnersAreIgnored()
'@ @'
    private static void NonQueryOpcodeIsRejected()
    {
        var packet = BuildResponse(ExpectedHost, 1, 1, null, 1, 1, [203, 0, 113, 7]);
        packet[2] |= 0x08; // OPCODE=1 rather than the QUERY opcode used by BuildQuery.
        AssertIOException(() => L2tpDnsResolver.ParseResponse(packet, TransactionId, ExpectedHost));
    }

    private static void OrdinaryResponseFlagsRemainAccepted()
    {
        var packet = BuildResponse(ExpectedHost, 1, 1, null, 1, 1, [203, 0, 113, 7]);
        packet[2] |= 0x04; // AA
        packet[3] |= 0x30; // AD + CD; RA from the base packet remains set.
        var parsed = L2tpDnsResolver.ParseResponse(packet, TransactionId, ExpectedHost);
        if (parsed.Addresses.Count != 1 ||
            !parsed.Addresses[0].Equals(IPAddress.Parse("203.0.113.7")))
        {
            throw new InvalidOperationException("Ordinary DNS response flags changed valid A semantics.");
        }
    }

    private static void MalformedOwnedAddressRdataIsRejected()
    {
        var malformedThenValid = BuildResponse(
            ExpectedHost, 1, 1, null, 1, 1, [203, 0, 113]);
        AppendAnswer(ref malformedThenValid, null, 1, 1, [203, 0, 113, 7]);
        AssertIOException(() =>
            L2tpDnsResolver.ParseResponse(malformedThenValid, TransactionId, ExpectedHost));

        var validThenMalformed = BuildResponse(
            ExpectedHost, 1, 1, null, 1, 1, [203, 0, 113, 7]);
        AppendAnswer(ref validThenMalformed, null, 1, 1, [203, 0, 113]);
        AssertIOException(() =>
            L2tpDnsResolver.ParseResponse(validThenMalformed, TransactionId, ExpectedHost));
    }

    private static void UnrelatedAnswerOwnersAreIgnored()
'@ 'DNS response grammar regression methods'
Write-Lf $testsPath $tests
