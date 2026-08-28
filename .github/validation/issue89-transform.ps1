$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

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
    if ($first -lt 0) { throw "Missing $Description anchor." }
    if ($Text.IndexOf($Old, $first + $Old.Length, [StringComparison]::Ordinal) -ge 0) {
        throw "Expected exactly one $Description anchor."
    }
    return $Text.Substring(0, $first) + $New + $Text.Substring($first + $Old.Length)
}

$resolverPath = 'src/ProxyToAnyConnect/Network/L2tpDnsResolver.cs'
$resolver = Read-Lf $resolverPath
$answerPattern = '(?ms)^            if \(ownedByQuery && recordClass == 1 && type == 1 && dataLength == 4\)\n            \{.*?^            \}\n\n(?=            offset \+= dataLength;)'
$answerReplacement = @'
            if (ownedByQuery && recordClass == 1 && type == 1 && dataLength == 4)
            {
                if (canonicalName is not null)
                {
                    throw new IOException(
                        "DNS response mixed CNAME and A data for the queried owner.");
                }

                var address = new IPAddress(response.Slice(offset, 4));
                if (firstAddress is null)
                {
                    firstAddress = address;
                }
                else
                {
                    (additionalAddresses ??= new List<IPAddress>()).Add(address);
                }

                minimumTtlSeconds = MinTtl(minimumTtlSeconds, ttlSeconds);
            }
            else if (ownedByQuery && recordClass == 1 && type == 5)
            {
                if (firstAddress is not null || canonicalName is not null)
                {
                    throw new IOException(
                        "DNS response returned an ambiguous CNAME RRset for the queried owner.");
                }

                var cnameOffset = offset;
                var parsedCanonicalName = ReadName(response, ref cnameOffset);
                if (cnameOffset != checked(offset + dataLength))
                {
                    throw new IOException("DNS CNAME RDATA length does not match its encoded name.");
                }

                canonicalName = parsedCanonicalName;
                minimumTtlSeconds = MinTtl(minimumTtlSeconds, ttlSeconds);
            }

'@
$resolver = Replace-RegexOnce $resolver $answerPattern $answerReplacement 'owned A/CNAME parser'
Write-Lf $resolverPath $resolver

$testsPath = 'tests/ProxyToAnyConnect.SelfTests/DnsResponseBindingSelfTests.cs'
$tests = Read-Lf $testsPath
$runAnchor = @'
            MalformedCnameRdataLengthIsRejected();
'@
$runReplacement = @'
            MalformedCnameRdataLengthIsRejected();
            CnameOnlyIsAccepted();
            MultipleOwnedAddressesRemainAccepted();
            AmbiguousCnameAndAddressAreRejected();
            MultipleOwnedCnamesAreRejected();
'@
$tests = Replace-LiteralOnce $tests $runAnchor $runReplacement 'DNS ambiguity run list'

$methodAnchor = @'
    private static void WrongQuestionNameIsRejected()
'@
$methods = @'
    private static void CnameOnlyIsAccepted()
    {
        var packet = BuildResponse(
            ExpectedHost,
            1,
            1,
            null,
            5,
            1,
            EncodeName("target.example.com"));
        var parsed = L2tpDnsResolver.ParseResponse(packet, TransactionId, ExpectedHost);
        if (parsed.Addresses.Count != 0 || parsed.CanonicalName != "target.example.com")
        {
            throw new InvalidOperationException("A single owned CNAME did not remain a valid canonical redirect.");
        }
    }

    private static void MultipleOwnedAddressesRemainAccepted()
    {
        var packet = BuildResponse(ExpectedHost, 1, 1, null, 1, 1, [203, 0, 113, 7]);
        AppendAnswer(ref packet, null, 1, 1, [203, 0, 113, 8]);
        var parsed = L2tpDnsResolver.ParseResponse(packet, TransactionId, ExpectedHost);
        if (parsed.Addresses.Count != 2 ||
            !parsed.Addresses[0].Equals(IPAddress.Parse("203.0.113.7")) ||
            !parsed.Addresses[1].Equals(IPAddress.Parse("203.0.113.8")))
        {
            throw new InvalidOperationException("Valid multi-A RRset semantics changed.");
        }
    }

    private static void AmbiguousCnameAndAddressAreRejected()
    {
        var cnameThenA = BuildResponse(
            ExpectedHost,
            1,
            1,
            null,
            5,
            1,
            EncodeName("target.example.com"));
        AppendAnswer(ref cnameThenA, null, 1, 1, [203, 0, 113, 7]);
        AssertIOException(() => L2tpDnsResolver.ParseResponse(cnameThenA, TransactionId, ExpectedHost));

        var aThenCname = BuildResponse(ExpectedHost, 1, 1, null, 1, 1, [203, 0, 113, 7]);
        AppendAnswer(
            ref aThenCname,
            null,
            5,
            1,
            EncodeName("target.example.com"));
        AssertIOException(() => L2tpDnsResolver.ParseResponse(aThenCname, TransactionId, ExpectedHost));
    }

    private static void MultipleOwnedCnamesAreRejected()
    {
        var conflicting = BuildResponse(
            ExpectedHost,
            1,
            1,
            null,
            5,
            1,
            EncodeName("first.example.com"));
        AppendAnswer(
            ref conflicting,
            null,
            5,
            1,
            EncodeName("second.example.com"));
        AssertIOException(() => L2tpDnsResolver.ParseResponse(conflicting, TransactionId, ExpectedHost));

        var duplicate = BuildResponse(
            ExpectedHost,
            1,
            1,
            null,
            5,
            1,
            EncodeName("same.example.com"));
        AppendAnswer(
            ref duplicate,
            null,
            5,
            1,
            EncodeName("same.example.com"));
        AssertIOException(() => L2tpDnsResolver.ParseResponse(duplicate, TransactionId, ExpectedHost));
    }

    private static void WrongQuestionNameIsRejected()
'@
$tests = Replace-LiteralOnce $tests $methodAnchor $methods 'DNS ambiguity test methods'

$helperAnchor = @'
    private static int FindAnswerRdLengthOffset(byte[] packet)
'@
$helperReplacement = @'
    private static void AppendAnswer(
        ref byte[] packet,
        string? answerOwner,
        ushort answerType,
        ushort answerClass,
        byte[] answerData)
    {
        var answerCount = (packet[6] << 8) | packet[7];
        if (answerCount >= ushort.MaxValue)
        {
            throw new InvalidOperationException("Test DNS response answer count overflowed.");
        }

        var bytes = packet.ToList();
        if (answerOwner is null)
        {
            bytes.Add(0xC0);
            bytes.Add(0x0C);
        }
        else
        {
            bytes.AddRange(EncodeName(answerOwner));
        }

        AddUInt16(bytes, answerType);
        AddUInt16(bytes, answerClass);
        AddUInt32(bytes, 60);
        AddUInt16(bytes, checked((ushort)answerData.Length));
        bytes.AddRange(answerData);
        packet = bytes.ToArray();

        answerCount++;
        packet[6] = checked((byte)(answerCount >> 8));
        packet[7] = checked((byte)answerCount);
    }

    private static int FindAnswerRdLengthOffset(byte[] packet)
'@
$tests = Replace-LiteralOnce $tests $helperAnchor $helperReplacement 'DNS answer appender helper'
Write-Lf $testsPath $tests
