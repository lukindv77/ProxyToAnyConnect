$ErrorActionPreference = 'Stop'

function Read-Lf([string]$Path) {
    $text = [System.IO.File]::ReadAllText($Path)
    return $text.Replace("`r`n", "`n").Replace("`r", "`n")
}

function Write-Lf([string]$Path, [string]$Text) {
    [System.IO.File]::WriteAllText($Path, $Text, [System.Text.UTF8Encoding]::new($false))
}

function Replace-RegexOnce(
    [string]$Text,
    [string]$Pattern,
    [string]$Replacement,
    [string]$Description) {
    $matches = [regex]::Matches($Text, $Pattern, [System.Text.RegularExpressions.RegexOptions]::Multiline)
    if ($matches.Count -ne 1) {
        throw "Expected exactly one $Description anchor, found $($matches.Count)."
    }
    return [regex]::Replace($Text, $Pattern, $Replacement, [System.Text.RegularExpressions.RegexOptions]::Multiline)
}

$resolverPath = 'src/ProxyToAnyConnect/Network/L2tpDnsResolver.cs'
$resolver = Read-Lf $resolverPath
$resolver = Replace-RegexOnce $resolver `
    '^        var answerCount = BinaryPrimitives\.ReadUInt16BigEndian\(response\[6\.\.\]\);$' `
    @'
        var answerCount = BinaryPrimitives.ReadUInt16BigEndian(response[6..]);
        var authorityCount = BinaryPrimitives.ReadUInt16BigEndian(response[8..]);
        var additionalCount = BinaryPrimitives.ReadUInt16BigEndian(response[10..]);
'@ `
    'DNS section count capture'
$resolver = Replace-RegexOnce $resolver `
    '^        IReadOnlyList<IPAddress> parsedAddresses;$' `
    @'
        SkipResourceRecords(response, ref offset, authorityCount);
        SkipResourceRecords(response, ref offset, additionalCount);
        if (offset != response.Length)
        {
            throw new IOException("DNS response contains bytes outside its declared sections.");
        }

        IReadOnlyList<IPAddress> parsedAddresses;
'@ `
    'DNS complete-message exhaustion check'
$resolver = Replace-RegexOnce $resolver `
    '^    private static void ReadExactlyAsync\(Stream stream, byte\[\] buffer, CancellationToken cancellationToken\)$' `
    @'
    private static void SkipResourceRecords(ReadOnlySpan<byte> response, ref int offset, int recordCount)
    {
        for (var i = 0; i < recordCount; i++)
        {
            SkipName(response, ref offset);
            EnsureRemaining(response, offset, 10);
            var dataLength = BinaryPrimitives.ReadUInt16BigEndian(response[(offset + 8)..]);
            offset += 10;
            EnsureRemaining(response, offset, dataLength);
            offset += dataLength;
        }
    }

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
'@ `
    'generic DNS section parser helper'
Write-Lf $resolverPath $resolver

$testsPath = 'tests/ProxyToAnyConnect.SelfTests/DnsResponseBindingSelfTests.cs'
$tests = Read-Lf $testsPath
$tests = Replace-RegexOnce $tests `
    '^            MalformedOwnedAddressRdataIsRejected\(\);\n            UnrelatedAnswerOwnersAreIgnored\(\);$' `
    @'
            MalformedOwnedAddressRdataIsRejected();
            CompleteDeclaredSectionsAreRequired();
            StructurallyValidNonAnswerSectionsRemainAccepted();
            UnrelatedAnswerOwnersAreIgnored();
'@ `
    'DNS complete-message test registration'
$tests = Replace-RegexOnce $tests `
    '^    private static void UnrelatedAnswerOwnersAreIgnored\(\)$' `
    @'
    private static void CompleteDeclaredSectionsAreRequired()
    {
        var missingAuthority = BuildResponse(ExpectedHost, 1, 1, null, 1, 1, [203, 0, 113, 7]);
        missingAuthority[9] = 1;
        AssertIOException(() => L2tpDnsResolver.ParseResponse(missingAuthority, TransactionId, ExpectedHost));

        var missingAdditional = BuildResponse(ExpectedHost, 1, 1, null, 1, 1, [203, 0, 113, 7]);
        missingAdditional[11] = 1;
        AssertIOException(() => L2tpDnsResolver.ParseResponse(missingAdditional, TransactionId, ExpectedHost));

        var truncatedAdditional = BuildResponse(ExpectedHost, 1, 1, null, 1, 1, [203, 0, 113, 7]);
        AppendSectionRecord(ref truncatedAdditional, 10, [0], 41, 1232, [0x01]);
        Array.Resize(ref truncatedAdditional, truncatedAdditional.Length - 1);
        AssertIOException(() => L2tpDnsResolver.ParseResponse(truncatedAdditional, TransactionId, ExpectedHost));

        var trailing = BuildResponse(ExpectedHost, 1, 1, null, 1, 1, [203, 0, 113, 7]);
        Array.Resize(ref trailing, trailing.Length + 1);
        trailing[^1] = 0x5A;
        AssertIOException(() => L2tpDnsResolver.ParseResponse(trailing, TransactionId, ExpectedHost));
    }

    private static void StructurallyValidNonAnswerSectionsRemainAccepted()
    {
        var packet = BuildResponse(ExpectedHost, 1, 1, null, 1, 1, [203, 0, 113, 7]);
        AppendSectionRecord(
            ref packet,
            8,
            [0xC0, 0x0C],
            2,
            1,
            EncodeName("ns.example.com"));
        AppendSectionRecord(ref packet, 10, [0], 41, 1232, []);

        var parsed = L2tpDnsResolver.ParseResponse(packet, TransactionId, ExpectedHost);
        if (parsed.Addresses.Count != 1 ||
            !parsed.Addresses[0].Equals(IPAddress.Parse("203.0.113.7")))
        {
            throw new InvalidOperationException("Valid authority/additional sections changed A routing semantics.");
        }
    }

    private static void UnrelatedAnswerOwnersAreIgnored()
'@ `
    'DNS complete-message regression methods'
$tests = Replace-RegexOnce $tests `
    '^    private static int FindAnswerRdLengthOffset\(byte\[\] packet\)$' `
    @'
    private static void AppendSectionRecord(
        ref byte[] packet,
        int countOffset,
        byte[] ownerEncoding,
        ushort type,
        ushort recordClass,
        byte[] data)
    {
        var count = (packet[countOffset] << 8) | packet[countOffset + 1];
        if (count >= ushort.MaxValue)
        {
            throw new InvalidOperationException("Test DNS section count overflowed.");
        }

        var bytes = packet.ToList();
        bytes.AddRange(ownerEncoding);
        AddUInt16(bytes, type);
        AddUInt16(bytes, recordClass);
        AddUInt32(bytes, 60);
        AddUInt16(bytes, checked((ushort)data.Length));
        bytes.AddRange(data);
        packet = bytes.ToArray();

        count++;
        packet[countOffset] = checked((byte)(count >> 8));
        packet[countOffset + 1] = checked((byte)count);
    }

    private static int FindAnswerRdLengthOffset(byte[] packet)
'@ `
    'DNS generic section test helper'
Write-Lf $testsPath $tests
