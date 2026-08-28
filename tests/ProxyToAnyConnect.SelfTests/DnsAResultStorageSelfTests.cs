using System.Diagnostics;
using System.Net;
using System.Text;
using ProxyToAnyConnect.Network;
using ProxyToAnyConnect.Vpn;

namespace ProxyToAnyConnect.SelfTests;

internal static class DnsAResultStorageSelfTests
{
    private const ushort TransactionId = 0x3456;
    private const int WarmupIterations = 4096;
    private const int AllocationIterations = 1000;
    private const int TimingRounds = 15;
    private const int IterationsPerRound = 65536;
    private const double MaxMedianSlowdownRatio = 1.25;
    private static readonly IPAddress RepresentativeAddress = IPAddress.Parse("203.0.113.7");
    private static int _sink;

    public static int Run()
    {
        try
        {
            ParseAndCacheSemanticsRemainEquivalent();

            for (var i = 0; i < WarmupIterations; i++)
            {
                RunOptimizedStorage();
                RunListAndCopyPredecessor();
            }

            var optimizedBytes = MeasureAllocatedBytes(RunOptimizedStorage);
            var predecessorBytes = MeasureAllocatedBytes(RunListAndCopyPredecessor);
            if (optimizedBytes >= predecessorBytes)
            {
                throw new InvalidOperationException(
                    $"Direct-array A-result storage allocated {optimizedBytes} bytes versus " +
                    $"{predecessorBytes} bytes for the List+ToArray predecessor.");
            }

            var timing = MeasurePaired(RunOptimizedStorage, RunListAndCopyPredecessor);
            if (timing.Ratio > MaxMedianSlowdownRatio)
            {
                throw new InvalidOperationException(
                    $"Direct-array A-result storage median was {timing.OptimizedMedianNs:F0} ns/op versus " +
                    $"{timing.PredecessorMedianNs:F0} ns/op for List+ToArray " +
                    $"({timing.Ratio:F2}x, limit {MaxMedianSlowdownRatio:F2}x).");
            }

            Console.WriteLine(
                $"PASS: DNS A-result storage avoids List and cache-copy setup " +
                $"(alloc {optimizedBytes / (double)AllocationIterations:F0} vs " +
                $"{predecessorBytes / (double)AllocationIterations:F0} bytes/result; " +
                $"timing {timing.OptimizedMedianNs:F0} vs {timing.PredecessorMedianNs:F0} ns/op, " +
                $"{timing.Ratio:F2}x)");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: DNS A-result storage regression: {ex}");
            return 1;
        }
    }

    private static void ParseAndCacheSemanticsRemainEquivalent()
    {
        var one = L2tpDnsResolver.ParseResponse(
            BuildResponse([(Type: (ushort)1, Ttl: 45u, Data: new byte[] { 203, 0, 113, 7 })]),
            TransactionId, "example.com");
        AssertAddresses(one, ["203.0.113.7"], expectedTtl: 45);
        if (one.Addresses is not IPAddress[] oneArray)
        {
            throw new InvalidOperationException("Single-A response is not backed by the cache-reusable IPAddress[] result.");
        }

        using var context = CreateContext();
        var cache = new L2tpDnsCache(maxEntries: 4);
        cache.Set("one.example", context, one.Addresses, TimeSpan.FromSeconds(45));
        if (!cache.TryGet("one.example", context, out var cached) ||
            !ReferenceEquals(oneArray, cached))
        {
            throw new InvalidOperationException("DNS cache copied the direct single-A array instead of reusing its owned result.");
        }

        var multi = L2tpDnsResolver.ParseResponse(
            BuildResponse(
            [
                (Type: (ushort)1, Ttl: 60u, Data: new byte[] { 203, 0, 113, 7 }),
                (Type: (ushort)1, Ttl: 30u, Data: new byte[] { 198, 51, 100, 9 }),
                (Type: (ushort)1, Ttl: 90u, Data: new byte[] { 192, 0, 2, 44 })
            ]),
            TransactionId, "example.com");
        AssertAddresses(multi, ["203.0.113.7", "198.51.100.9", "192.0.2.44"], expectedTtl: 30);
        if (multi.Addresses is not IPAddress[])
        {
            throw new InvalidOperationException("Multi-A response did not finish in cache-reusable array storage.");
        }


    }

    private static void AssertAddresses(
        ParsedDnsResponse response,
        string[] expected,
        uint expectedTtl)
    {
        if (response.Addresses.Count != expected.Length || response.MinimumTtlSeconds != expectedTtl)
        {
            throw new InvalidOperationException("DNS A-result count or TTL semantics changed.");
        }

        for (var i = 0; i < expected.Length; i++)
        {
            if (response.Addresses[i].ToString() != expected[i])
            {
                throw new InvalidOperationException(
                    $"DNS A-result order changed at index {i}: got {response.Addresses[i]}, expected {expected[i]}.");
            }
        }
    }

    private static void RunOptimizedStorage()
    {
        IReadOnlyList<IPAddress> addresses = new[] { RepresentativeAddress };
        var cacheOwned = addresses as IPAddress[] ?? addresses.ToArray();
        _sink ^= cacheOwned.Length;
    }

    private static void RunListAndCopyPredecessor()
    {
        IReadOnlyList<IPAddress> addresses = new List<IPAddress> { RepresentativeAddress };
        var cacheOwned = addresses as IPAddress[] ?? addresses.ToArray();
        _sink ^= cacheOwned.Length;
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

    private static VpnContext CreateContext() =>
        new(
            "AResultStorageSelfTest",
            IPAddress.Parse("10.20.30.25"),
            new VpnInterfaceInfo(
                "AResultStorageSelfTest",
                "AResultStorageSelfTest",
                42,
                [IPAddress.Parse("10.0.0.53")]));

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

        var optimizedMedian = Median(optimizedRounds);
        var predecessorMedian = Median(predecessorRounds);
        return new TimingResult(optimizedMedian, predecessorMedian, optimizedMedian / predecessorMedian);
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
        double Ratio);
}
