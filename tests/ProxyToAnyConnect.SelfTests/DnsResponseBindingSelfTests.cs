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
            NonQueryOpcodeIsRejected();
            OrdinaryResponseFlagsRemainAccepted();
            MalformedOwnedAddressRdataIsRejected();
            UnrelatedAnswerOwnersAreIgnored();
            WrongAnswerClassIsIgnored();
            MalformedCnameRdataLengthIsRejected();
            CnameOnlyIsAccepted();
            MultipleOwnedAddressesRemainAccepted();
            AmbiguousCnameAndAddressAreRejected();
            MultipleOwnedCnamesAreRejected();

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
        Array.Resize(ref packet, packet.Length + 1);
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