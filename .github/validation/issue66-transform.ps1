Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Read-Lf([string]$Path) {
    return [IO.File]::ReadAllText($Path).Replace("`r`n", "`n")
}

function Write-Lf([string]$Path, [string]$Text) {
    [IO.File]::WriteAllText($Path, $Text.Replace("`r`n", "`n"), [Text.UTF8Encoding]::new($false))
}

function Replace-Exact([string]$Text, [string]$Old, [string]$New, [string]$Label) {
    $first = $Text.IndexOf($Old, [StringComparison]::Ordinal)
    if ($first -lt 0) { throw "Missing transform anchor: $Label" }
    if ($Text.IndexOf($Old, $first + $Old.Length, [StringComparison]::Ordinal) -ge 0) {
        throw "Non-unique transform anchor: $Label"
    }
    return $Text.Substring(0, $first) + $New + $Text.Substring($first + $Old.Length)
}

$sourcePath = 'src/ProxyToAnyConnect/Network/L2tpDnsResolver.cs'
$source = Read-Lf $sourcePath
$source = Replace-Exact $source @'
        var parsed = await QueryUdpAsync(query, transactionId, dnsServer, context, cancellationToken);

        if (parsed.Truncated)
        {
            var tcpResponse = await QueryTcpAsync(query, dnsServer, context, cancellationToken);
            parsed = ParseResponse(tcpResponse, transactionId);
        }
'@ @'
        var parsed = await QueryUdpAsync(
            query,
            transactionId,
            host,
            dnsServer,
            context,
            cancellationToken);

        if (parsed.Truncated)
        {
            var tcpResponse = await QueryTcpAsync(query, dnsServer, context, cancellationToken);
            parsed = ParseResponse(tcpResponse, transactionId, host);
        }
'@ 'production query binding'

$source = Replace-Exact $source @'
    private static async Task<ParsedDnsResponse> QueryUdpAsync(
        byte[] query,
        ushort transactionId,
        IPAddress dnsServer,
'@ @'
    private static async Task<ParsedDnsResponse> QueryUdpAsync(
        byte[] query,
        ushort transactionId,
        string expectedHost,
        IPAddress dnsServer,
'@ 'UDP expected host parameter'

$source = Replace-Exact $source @'
            return ParseResponse(buffer.AsSpan(0, received), transactionId);
'@ @'
            return ParseResponse(buffer.AsSpan(0, received), transactionId, expectedHost);
'@ 'UDP secure parse call'

$source = Replace-Exact $source @'
    internal static ParsedDnsResponse ParseResponse(ReadOnlySpan<byte> response, ushort transactionId)
    {
        if (response.Length < 12)
'@ @'
    internal static ParsedDnsResponse ParseResponse(
        ReadOnlySpan<byte> response,
        ushort transactionId,
        string expectedHost)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedHost);

        if (response.Length < 12)
'@ 'secure parser signature'

$source = Replace-Exact $source @'
        var questionCount = BinaryPrimitives.ReadUInt16BigEndian(response[4..]);
        var answerCount = BinaryPrimitives.ReadUInt16BigEndian(response[6..]);
        var offset = 12;

        for (var i = 0; i < questionCount; i++)
        {
            SkipName(response, ref offset);
            EnsureRemaining(response, offset, 4);
            offset += 4;
        }
'@ @'
        var questionCount = BinaryPrimitives.ReadUInt16BigEndian(response[4..]);
        if (questionCount != 1)
        {
            throw new IOException("DNS response must contain exactly one question.");
        }

        var answerCount = BinaryPrimitives.ReadUInt16BigEndian(response[6..]);
        var offset = 12;
        var questionNameOffset = offset;
        SkipName(response, ref offset);
        if (!DnsNameEqualsAscii(response, questionNameOffset, expectedHost))
        {
            throw new IOException("DNS response question name does not match the query.");
        }

        EnsureRemaining(response, offset, 4);
        var questionType = BinaryPrimitives.ReadUInt16BigEndian(response[offset..]);
        var questionClass = BinaryPrimitives.ReadUInt16BigEndian(response[(offset + 2)..]);
        if (questionType != 1 || questionClass != 1)
        {
            throw new IOException("DNS response question is not the requested A/IN question.");
        }
        offset += 4;
'@ 'question binding'

$source = Replace-Exact $source @'
        for (var i = 0; i < answerCount; i++)
        {
            SkipName(response, ref offset);
            EnsureRemaining(response, offset, 10);
            var type = BinaryPrimitives.ReadUInt16BigEndian(response[offset..]);
            var ttlSeconds = BinaryPrimitives.ReadUInt32BigEndian(response[(offset + 4)..]);
            var dataLength = BinaryPrimitives.ReadUInt16BigEndian(response[(offset + 8)..]);
            offset += 10;
            EnsureRemaining(response, offset, dataLength);

            if (type == 1 && dataLength == 4)
'@ @'
        for (var i = 0; i < answerCount; i++)
        {
            var ownerOffset = offset;
            SkipName(response, ref offset);
            EnsureRemaining(response, offset, 10);
            var type = BinaryPrimitives.ReadUInt16BigEndian(response[offset..]);
            var recordClass = BinaryPrimitives.ReadUInt16BigEndian(response[(offset + 2)..]);
            var ttlSeconds = BinaryPrimitives.ReadUInt32BigEndian(response[(offset + 4)..]);
            var dataLength = BinaryPrimitives.ReadUInt16BigEndian(response[(offset + 8)..]);
            offset += 10;
            EnsureRemaining(response, offset, dataLength);
            var ownedByQuery = DnsNameEqualsAscii(response, ownerOffset, expectedHost);

            if (ownedByQuery && recordClass == 1 && type == 1 && dataLength == 4)
'@ 'answer owner and class binding'

$source = Replace-Exact $source @'
            else if (type == 5)
            {
                var cnameOffset = offset;
                canonicalName = ReadName(response, ref cnameOffset);
                minimumTtlSeconds = MinTtl(minimumTtlSeconds, ttlSeconds);
            }
'@ @'
            else if (ownedByQuery && recordClass == 1 && type == 5)
            {
                var cnameOffset = offset;
                var parsedCanonicalName = ReadName(response, ref cnameOffset);
                if (cnameOffset != checked(offset + dataLength))
                {
                    throw new IOException("DNS CNAME RDATA length does not match its encoded name.");
                }

                canonicalName = parsedCanonicalName;
                minimumTtlSeconds = MinTtl(minimumTtlSeconds, ttlSeconds);
            }
'@ 'CNAME owner and RDATA framing'

$source = Replace-Exact $source @'
    private static uint MinTtl(uint? current, uint candidate) =>
        current is null ? candidate : Math.Min(current.Value, candidate);
'@ @'
    private static bool DnsNameEqualsAscii(
        ReadOnlySpan<byte> packet,
        int offset,
        ReadOnlySpan<char> expectedHost)
    {
        var current = offset;
        var expectedOffset = 0;
        var hasLabel = false;
        var jumps = 0;

        while (true)
        {
            EnsureRemaining(packet, current, 1);
            var length = packet[current++];
            if (length == 0)
            {
                return expectedOffset == expectedHost.Length;
            }

            if ((length & 0xC0) == 0xC0)
            {
                EnsureRemaining(packet, current, 1);
                var pointer = ((length & 0x3F) << 8) | packet[current++];
                if (pointer >= packet.Length || ++jumps > 32)
                {
                    throw new IOException("Invalid DNS name compression pointer.");
                }

                current = pointer;
                continue;
            }

            if ((length & 0xC0) != 0 || length > 63)
            {
                throw new IOException("Invalid DNS label length.");
            }

            EnsureRemaining(packet, current, length);
            if (hasLabel)
            {
                if (expectedOffset >= expectedHost.Length || expectedHost[expectedOffset] != '.')
                {
                    return false;
                }

                expectedOffset++;
            }

            if (expectedOffset > expectedHost.Length - length)
            {
                return false;
            }

            for (var i = 0; i < length; i++)
            {
                var expected = expectedHost[expectedOffset + i];
                if (expected > 0x7F || FoldAsciiCase(packet[current + i]) != FoldAsciiCase((byte)expected))
                {
                    return false;
                }
            }

            expectedOffset += length;
            current += length;
            hasLabel = true;
        }
    }

    private static byte FoldAsciiCase(byte value) =>
        value is >= 0x41 and <= 0x5A ? (byte)(value + 0x20) : value;

    private static uint MinTtl(uint? current, uint candidate) =>
        current is null ? candidate : Math.Min(current.Value, candidate);
'@ 'allocation-free DNS owner comparison'

Write-Lf $sourcePath $source

$programPath = 'tests/ProxyToAnyConnect.SelfTests/Program.cs'
$program = Read-Lf $programPath
$program = $program.Replace(
    'L2tpDnsResolver.ParseResponse(packet, DnsTransactionId)',
    'L2tpDnsResolver.ParseResponse(packet, DnsTransactionId, "example.com")')
if ($program -notmatch 'ParseResponse\(packet, DnsTransactionId, "example\.com"\)') {
    throw 'Program DNS parser calls were not updated.'
}
Write-Lf $programPath $program

$addressPath = 'tests/ProxyToAnyConnect.SelfTests/DnsResponseAddressListSelfTests.cs'
$address = Read-Lf $addressPath
$address = $address.Replace(
    'L2tpDnsResolver.ParseResponse(cnameResponse, TransactionId)',
    'L2tpDnsResolver.ParseResponse(cnameResponse, TransactionId, "example.com")')
$address = $address.Replace(
    'L2tpDnsResolver.ParseResponse(BuildCnameResponse(), TransactionId)',
    'L2tpDnsResolver.ParseResponse(BuildCnameResponse(), TransactionId, "example.com")')
$address = $address.Replace(
    'L2tpDnsResolver.ParseResponse(BuildAResponse(), TransactionId)',
    'L2tpDnsResolver.ParseResponse(BuildAResponse(), TransactionId, "example.com")')
$address = $address.Replace(
    'L2tpDnsResolver.ParseResponse(BuildMixedResponse(), TransactionId)',
    'L2tpDnsResolver.ParseResponse(BuildMixedResponse(), TransactionId, "example.com")')
$address = $address.Replace(
    'L2tpDnsResolver.ParseResponse(response, TransactionId)',
    'L2tpDnsResolver.ParseResponse(response, TransactionId, "example.com")')
if ($address.Contains('ParseResponse(cnameResponse, TransactionId)') -or
    $address.Contains('ParseResponse(BuildCnameResponse(), TransactionId)') -or
    $address.Contains('ParseResponse(BuildAResponse(), TransactionId)') -or
    $address.Contains('ParseResponse(BuildMixedResponse(), TransactionId)') -or
    $address.Contains('ParseResponse(response, TransactionId)')) {
    throw 'DnsResponseAddressListSelfTests retained an old parser call.'
}
Write-Lf $addressPath $address

$aStoragePath = 'tests/ProxyToAnyConnect.SelfTests/DnsAResultStorageSelfTests.cs'
$aStorage = Read-Lf $aStoragePath
$oldCount = ([regex]::Matches($aStorage, '(?m)^\s*TransactionId\);$')).Count
if ($oldCount -ne 3) { throw "Expected 3 multiline A-storage parser calls, found $oldCount." }
$aStorage = [regex]::Replace(
    $aStorage,
    '(?m)^(\s*)TransactionId\);$',
    '$1TransactionId, "example.com");')
Write-Lf $aStoragePath $aStorage

$bindingTestPath = 'tests/ProxyToAnyConnect.SelfTests/DnsResponseBindingSelfTests.cs'
$bindingTests = @'
using System.Net;
using System.Text;
using ProxyToAnyConnect.Network;

namespace ProxyToAnyConnect.SelfTests;

internal static class DnsResponseBindingSelfTests
{
    private const ushort TransactionId = 0x4567;
    private const string ExpectedHost = "example.com";

    public static int Run()
    {
        try
        {
            MatchingCompressedOwnerIsAccepted();
            MatchingOwnerIsAsciiCaseInsensitive();
            WrongQuestionNameIsRejected();
            WrongQuestionTypeOrClassIsRejected();
            NonSingleQuestionIsRejected();
            UnrelatedAnswerOwnersAreIgnored();
            WrongAnswerClassIsIgnored();
            MalformedCnameRdataLengthIsRejected();

            Console.WriteLine("PASS: DNS responses are bound to the exact A/IN question and answer owner without owner-name materialization");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: DNS response binding regression: {ex}");
            return 1;
        }
    }

    private static void MatchingCompressedOwnerIsAccepted()
    {
        var packet = BuildResponse(
            ExpectedHost,
            questionType: 1,
            questionClass: 1,
            answerOwner: null,
            answerType: 1,
            answerClass: 1,
            answerData: [203, 0, 113, 7]);
        var parsed = L2tpDnsResolver.ParseResponse(packet, TransactionId, ExpectedHost);
        if (parsed.Addresses.Count != 1 || !parsed.Addresses[0].Equals(IPAddress.Parse("203.0.113.7")))
        {
            throw new InvalidOperationException("Matching compressed DNS owner did not supply the expected A record.");
        }
    }

    private static void MatchingOwnerIsAsciiCaseInsensitive()
    {
        var packet = BuildResponse(
            "ExAmPlE.CoM",
            questionType: 1,
            questionClass: 1,
            answerOwner: null,
            answerType: 1,
            answerClass: 1,
            answerData: [198, 51, 100, 9]);
        var parsed = L2tpDnsResolver.ParseResponse(packet, TransactionId, ExpectedHost);
        if (parsed.Addresses.Count != 1 || !parsed.Addresses[0].Equals(IPAddress.Parse("198.51.100.9")))
        {
            throw new InvalidOperationException("DNS owner comparison was not ASCII case-insensitive.");
        }
    }

    private static void WrongQuestionNameIsRejected()
    {
        var packet = BuildResponse(
            "other.example.com", 1, 1, null, 1, 1, [203, 0, 113, 7]);
        AssertIOException(() => L2tpDnsResolver.ParseResponse(packet, TransactionId, ExpectedHost));
    }

    private static void WrongQuestionTypeOrClassIsRejected()
    {
        var wrongType = BuildResponse(ExpectedHost, 28, 1, null, 1, 1, [203, 0, 113, 7]);
        AssertIOException(() => L2tpDnsResolver.ParseResponse(wrongType, TransactionId, ExpectedHost));

        var wrongClass = BuildResponse(ExpectedHost, 1, 3, null, 1, 1, [203, 0, 113, 7]);
        AssertIOException(() => L2tpDnsResolver.ParseResponse(wrongClass, TransactionId, ExpectedHost));
    }

    private static void NonSingleQuestionIsRejected()
    {
        var packet = BuildResponse(ExpectedHost, 1, 1, null, 1, 1, [203, 0, 113, 7]);
        packet[4] = 0;
        packet[5] = 0;
        AssertIOException(() => L2tpDnsResolver.ParseResponse(packet, TransactionId, ExpectedHost));

        packet[5] = 2;
        AssertIOException(() => L2tpDnsResolver.ParseResponse(packet, TransactionId, ExpectedHost));
    }

    private static void UnrelatedAnswerOwnersAreIgnored()
    {
        var unrelatedA = BuildResponse(
            ExpectedHost, 1, 1, "other.example.com", 1, 1, [203, 0, 113, 7]);
        var parsedA = L2tpDnsResolver.ParseResponse(unrelatedA, TransactionId, ExpectedHost);
        if (parsedA.Addresses.Count != 0)
        {
            throw new InvalidOperationException("Unrelated A owner supplied routing evidence for the query.");
        }

        var unrelatedCname = BuildResponse(
            ExpectedHost,
            1,
            1,
            "other.example.com",
            5,
            1,
            EncodeName("target.example.com"));
        var parsedCname = L2tpDnsResolver.ParseResponse(unrelatedCname, TransactionId, ExpectedHost);
        if (parsedCname.CanonicalName is not null)
        {
            throw new InvalidOperationException("Unrelated CNAME owner redirected the queried authority.");
        }
    }

    private static void WrongAnswerClassIsIgnored()
    {
        var packet = BuildResponse(ExpectedHost, 1, 1, null, 1, 3, [203, 0, 113, 7]);
        var parsed = L2tpDnsResolver.ParseResponse(packet, TransactionId, ExpectedHost);
        if (parsed.Addresses.Count != 0)
        {
            throw new InvalidOperationException("Non-IN A answer supplied routing evidence.");
        }
    }

    private static void MalformedCnameRdataLengthIsRejected()
    {
        var packet = BuildResponse(
            ExpectedHost,
            1,
            1,
            null,
            5,
            1,
            EncodeName("target.example.com"));
        var rdLengthOffset = FindAnswerRdLengthOffset(packet);
        var originalLength = (packet[rdLengthOffset] << 8) | packet[rdLengthOffset + 1];
        packet[rdLengthOffset] = 0;
        packet[rdLengthOffset + 1] = checked((byte)(originalLength + 1));
        packet.Add(0);
        AssertIOException(() => L2tpDnsResolver.ParseResponse(packet, TransactionId, ExpectedHost));
    }

    private static byte[] BuildResponse(
        string questionName,
        ushort questionType,
        ushort questionClass,
        string? answerOwner,
        ushort answerType,
        ushort answerClass,
        byte[] answerData)
    {
        var packet = new List<byte>();
        AddUInt16(packet, TransactionId);
        AddUInt16(packet, 0x8180);
        AddUInt16(packet, 1);
        AddUInt16(packet, 1);
        AddUInt16(packet, 0);
        AddUInt16(packet, 0);
        packet.AddRange(EncodeName(questionName));
        AddUInt16(packet, questionType);
        AddUInt16(packet, questionClass);

        if (answerOwner is null)
        {
            packet.Add(0xC0);
            packet.Add(0x0C);
        }
        else
        {
            packet.AddRange(EncodeName(answerOwner));
        }

        AddUInt16(packet, answerType);
        AddUInt16(packet, answerClass);
        AddUInt32(packet, 60);
        AddUInt16(packet, checked((ushort)answerData.Length));
        packet.AddRange(answerData);
        return packet.ToArray();
    }

    private static int FindAnswerRdLengthOffset(byte[] packet)
    {
        var offset = 12;
        SkipEncodedName(packet, ref offset);
        offset += 4;
        SkipEncodedName(packet, ref offset);
        return offset + 8;
    }

    private static void SkipEncodedName(byte[] packet, ref int offset)
    {
        while (true)
        {
            var length = packet[offset++];
            if (length == 0)
            {
                return;
            }

            if ((length & 0xC0) == 0xC0)
            {
                offset++;
                return;
            }

            offset += length;
        }
    }

    private static byte[] EncodeName(string name)
    {
        var bytes = new List<byte>();
        foreach (var label in name.Split('.'))
        {
            var encoded = Encoding.ASCII.GetBytes(label);
            bytes.Add(checked((byte)encoded.Length));
            bytes.AddRange(encoded);
        }

        bytes.Add(0);
        return bytes.ToArray();
    }

    private static void AddUInt16(List<byte> bytes, ushort value)
    {
        bytes.Add((byte)(value >> 8));
        bytes.Add((byte)value);
    }

    private static void AddUInt32(List<byte> bytes, uint value)
    {
        bytes.Add((byte)(value >> 24));
        bytes.Add((byte)(value >> 16));
        bytes.Add((byte)(value >> 8));
        bytes.Add((byte)value);
    }

    private static void AssertIOException(Action action)
    {
        try
        {
            action();
        }
        catch (IOException)
        {
            return;
        }

        throw new InvalidOperationException("Expected malformed/mismatched DNS response to fail closed.");
    }
}
'@
Write-Lf $bindingTestPath $bindingTests

$runnerPath = 'tests/ProxyToAnyConnect.SelfTests/CombinedTestRunner.cs'
$runner = Read-Lf $runnerPath
$runner = Replace-Exact $runner @'
        Run(nameof(DnsCacheSelfTests), DnsCacheSelfTests.Run);
        Run(nameof(DnsQuerySetupSelfTests), DnsQuerySetupSelfTests.Run);
'@ @'
        Run(nameof(DnsCacheSelfTests), DnsCacheSelfTests.Run);
        Run(nameof(DnsResponseBindingSelfTests), DnsResponseBindingSelfTests.Run);
        Run(nameof(DnsQuerySetupSelfTests), DnsQuerySetupSelfTests.Run);
'@ 'aggregate DNS response binding suite'
Write-Lf $runnerPath $runner

Write-Host 'Issue #66 transform applied.'
