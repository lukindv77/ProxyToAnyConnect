using System.Diagnostics;
using System.Net;
using System.Text;
using ProxyToAnyConnect.Network;

namespace ProxyToAnyConnect.SelfTests;

internal static class DnsResponseAddressListSelfTests
{
    private const ushort TransactionId = 0x2345;
    private const int WarmupIterations = 4096;
    private const int AllocationIterations = 1000;
    private const int TimingRounds = 15;
    private const int IterationsPerRound = 32768;
    private const double MaxMedianSlowdownRatio = 1.25;

    public static int Run()
    {
        try
        {
            ResponseSemanticsRemainEquivalent();
            var cnameResponse = BuildCnameResponse();

            for (var i = 0; i < WarmupIterations; i++)
            {
                GC.KeepAlive(L2tpDnsResolver.ParseResponse(cnameResponse, TransactionId, "example.com"));
                GC.KeepAlive(EagerAddressListPredecessor(cnameResponse));
            }

            var optimizedBytes = MeasureAllocatedBytes(() =>
                GC.KeepAlive(L2tpDnsResolver.ParseResponse(cnameResponse, TransactionId, "example.com")));
            var predecessorBytes = MeasureAllocatedBytes(() =>
                GC.KeepAlive(EagerAddressListPredecessor(cnameResponse)));
            if (optimizedBytes >= predecessorBytes)
            {
                throw new InvalidOperationException(
                    $"Lazy DNS address storage allocated {optimizedBytes} bytes versus " +
                    $"{predecessorBytes} bytes for the eager-list predecessor.");
            }

            Action optimized = () =>
                GC.KeepAlive(L2tpDnsResolver.ParseResponse(cnameResponse, TransactionId, "example.com"));
            Action predecessor = () => GC.KeepAlive(EagerAddressListPredecessor(cnameResponse));
            var timing = MeasurePaired(optimized, predecessor);
            if (timing.PairedRatioMedian > MaxMedianSlowdownRatio)
            {
                throw new InvalidOperationException(
                    $"Lazy DNS address storage median was {timing.OptimizedMedianNs:F0} ns/op versus " +
                    $"{timing.PredecessorMedianNs:F0} ns/op for eager-list predecessor " +
                    $"({timing.PairedRatioMedian:F2}x, limit {MaxMedianSlowdownRatio:F2}x).");
            }

            Console.WriteLine(
                $"PASS: DNS CNAME responses avoid unused address-list allocation " +
                $"(alloc {optimizedBytes / (double)AllocationIterations:F0} vs " +
                $"{predecessorBytes / (double)AllocationIterations:F0} bytes/response; " +
                $"timing {timing.OptimizedMedianNs:F0} vs {timing.PredecessorMedianNs:F0} ns/op, " +
                $"{timing.PairedRatioMedian:F2}x)");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: DNS lazy address-list regression: {ex}");
            return 1;
        }
    }

    private static void ResponseSemanticsRemainEquivalent()
    {
        var cname = L2tpDnsResolver.ParseResponse(BuildCnameResponse(), TransactionId, "example.com");
        if (cname.Addresses.Count != 0 ||
            cname.CanonicalName != "alias.example.com" ||
            cname.MinimumTtlSeconds != 60)
        {
            throw new InvalidOperationException("DNS CNAME response semantics changed.");
        }

        var a = L2tpDnsResolver.ParseResponse(BuildAResponse(), TransactionId, "example.com");
        if (a.Addresses.Count != 1 ||
            !a.Addresses[0].Equals(IPAddress.Parse("203.0.113.7")) ||
            a.CanonicalName is not null ||
            a.MinimumTtlSeconds != 45)
        {
            throw new InvalidOperationException("DNS A response semantics changed.");
        }

        AssertAmbiguousResponseRejected(BuildMixedResponse());
    }

    private static ParsedDnsResponse EagerAddressListPredecessor(byte[] response)
    {
        var eagerAddresses = new List<IPAddress>();
        var parsed = L2tpDnsResolver.ParseResponse(response, TransactionId, "example.com");
        GC.KeepAlive(eagerAddresses);
        return parsed;
    }

    private static byte[] BuildCnameResponse() => BuildResponse(
        [(Type: (ushort)5, Ttl: 60u, Data: EncodeName("alias.example.com"))]);

    private static byte[] BuildAResponse() => BuildResponse(
        [(Type: (ushort)1, Ttl: 45u, Data: new byte[] { 203, 0, 113, 7 })]);

    private static byte[] BuildMixedResponse() => BuildResponse(
        [
            (Type: (ushort)5, Ttl: 30u, Data: EncodeName("edge.example.com")),
            (Type: (ushort)1, Ttl: 50u, Data: new byte[] { 198, 51, 100, 9 })
        ]);

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
    {
        var packet = new List<byte>();
        AddUInt16(packet, TransactionId);
        AddUInt16(packet, 0x8180);
        AddUInt16(packet, 1);
        AddUInt16(packet, checked((ushort)answers.Length));
        AddUInt16(packet, 0);
        AddUInt16(packet, 0);
        packet.AddRange(EncodeName("example.com"));
        AddUInt16(packet, 1);
        AddUInt16(packet, 1);

        foreach (var answer in answers)
        {
            packet.Add(0xC0);
            packet.Add(0x0C);
            AddUInt16(packet, answer.Type);
            AddUInt16(packet, 1);
            AddUInt32(packet, answer.Ttl);
            AddUInt16(packet, checked((ushort)answer.Data.Length));
            packet.AddRange(answer.Data);
        }

        return packet.ToArray();
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

    private static long MeasureAllocatedBytes(Action action)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < AllocationIterations; i++)
        {
            action();
        }

        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static TimingResult MeasurePaired(Action optimized, Action predecessor)
    {
        var optimizedRounds = new double[TimingRounds];
        var predecessorRounds = new double[TimingRounds];
        for (var round = 0; round < TimingRounds; round++)
        {
            if ((round & 1) == 0)
            {
                optimizedRounds[round] = MeasureNanosecondsPerOperation(optimized);
                predecessorRounds[round] = MeasureNanosecondsPerOperation(predecessor);
            }
            else
            {
                predecessorRounds[round] = MeasureNanosecondsPerOperation(predecessor);
                optimizedRounds[round] = MeasureNanosecondsPerOperation(optimized);
            }
        }

        var pairedRatios = new double[TimingRounds];
        for (var round = 0; round < TimingRounds; round++)
        {
            pairedRatios[round] = optimizedRounds[round] / predecessorRounds[round];
        }
        var optimizedMedian = Median(optimizedRounds);
        var predecessorMedian = Median(predecessorRounds);
        return new TimingResult(optimizedMedian, predecessorMedian, Median(pairedRatios));
    }

    private static double MeasureNanosecondsPerOperation(Action action)
    {
        var started = Stopwatch.GetTimestamp();
        for (var i = 0; i < IterationsPerRound; i++)
        {
            action();
        }

        var elapsedTicks = Stopwatch.GetTimestamp() - started;
        return elapsedTicks * (1_000_000_000.0 / Stopwatch.Frequency) / IterationsPerRound;
    }

    private static double Median(double[] values)
    {
        var ordered = (double[])values.Clone();
        Array.Sort(ordered);
        return ordered[ordered.Length / 2];
    }

    private readonly record struct TimingResult(
        double OptimizedMedianNs,
        double PredecessorMedianNs,
        double PairedRatioMedian);
}
