using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using ProxyToAnyConnect.Network;

namespace ProxyToAnyConnect.SelfTests;

internal static class DnsQuerySetupSelfTests
{
    private const int WarmupIterations = 256;
    private const int AllocationIterations = 1000;
    private const int TimingRounds = 9;
    // The operation is sub-microsecond on hosted runners, so a 4096-op sample
    // is only a few milliseconds and is vulnerable to scheduler/JIT noise. Keep
    // the same 1.25x policy but measure long enough for the median to be stable.
    private const int IterationsPerRound = 65536;
    private const double MaxMedianSlowdownRatio = 1.25;
    private const ushort TransactionId = 0x1234;
    private const string RepresentativeHost = "api.service.example.internal";

    public static int Run()
    {
        try
        {
            QueryWireFormatAndValidationMatchCurrentSemantics();

            for (var i = 0; i < WarmupIterations; i++)
            {
                GC.KeepAlive(L2tpDnsResolver.BuildQuery(RepresentativeHost, TransactionId));
                GC.KeepAlive(LegacyBuildQuery(RepresentativeHost, TransactionId));
            }

            var optimizedBytes = MeasureAllocatedBytes(
                () => GC.KeepAlive(L2tpDnsResolver.BuildQuery(RepresentativeHost, TransactionId)));
            var predecessorBytes = MeasureAllocatedBytes(
                () => GC.KeepAlive(LegacyBuildQuery(RepresentativeHost, TransactionId)));
            if (optimizedBytes >= predecessorBytes)
            {
                throw new InvalidOperationException(
                    $"Exact-size DNS query builder allocated {optimizedBytes} bytes versus " +
                    $"{predecessorBytes} bytes for the MemoryStream/Split predecessor.");
            }

            Action optimized = () =>
                GC.KeepAlive(L2tpDnsResolver.BuildQuery(RepresentativeHost, TransactionId));
            Action predecessor = () =>
                GC.KeepAlive(LegacyBuildQuery(RepresentativeHost, TransactionId));
            var timing = MeasurePaired(optimized, predecessor);
            if (timing.Ratio > MaxMedianSlowdownRatio)
            {
                throw new InvalidOperationException(
                    $"Exact-size DNS query builder median was {timing.OptimizedMedianNs:F0} ns/op versus " +
                    $"{timing.PredecessorMedianNs:F0} ns/op for MemoryStream/Split " +
                    $"({timing.Ratio:F2}x, limit {MaxMedianSlowdownRatio:F2}x).");
            }

            Console.WriteLine(
                $"PASS: exact-size DNS query construction reduces setup cost " +
                $"(alloc {optimizedBytes / (double)AllocationIterations:F0} vs " +
                $"{predecessorBytes / (double)AllocationIterations:F0} bytes/query; " +
                $"timing {timing.OptimizedMedianNs:F0} vs {timing.PredecessorMedianNs:F0} ns/op, " +
                $"{timing.Ratio:F2}x)");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: DNS query setup regression: {ex}");
            return 1;
        }
    }

    private static void QueryWireFormatAndValidationMatchCurrentSemantics()
    {
        foreach (var host in new[] { "www.example.com", "caf\u00e9.example", new string('a', 63) + ".example" })
        {
            var actual = L2tpDnsResolver.BuildQuery(host, TransactionId);
            var expected = LegacyBuildQuery(host, TransactionId);
            if (!actual.AsSpan().SequenceEqual(expected))
            {
                throw new InvalidOperationException(
                    $"Exact-size DNS query bytes differ from the intended predecessor wire format for '{host}'.");
            }

            if (!actual.AsSpan(6, 6).SequenceEqual(new byte[6]))
            {
                throw new InvalidOperationException(
                    "DNS query ANCOUNT/NSCOUNT/ARCOUNT fields were not explicitly zeroed.");
            }
        }

        AssertBothReject("a..example");
        AssertBothReject(".example");
        AssertBothReject("example.");
        AssertBothReject(new string('a', 64) + ".example");
    }

    private static void AssertBothReject(string host)
    {
        var optimizedRejected = ThrowsInvalidLabel(() =>
            L2tpDnsResolver.BuildQuery(host, TransactionId));
        var predecessorRejected = ThrowsInvalidLabel(() =>
            LegacyBuildQuery(host, TransactionId));
        if (!optimizedRejected || !predecessorRejected)
        {
            throw new InvalidOperationException(
                $"DNS label validation changed for '{host}'.");
        }
    }

    private static bool ThrowsInvalidLabel(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
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
        return new TimingResult(
            optimizedMedian,
            predecessorMedian,
            optimizedMedian / predecessorMedian);
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

    // Test-only copy of the pre-refactor DNS query construction shape. The DNS
    // count fields are explicitly cleared here so wire comparison tests the intended
    // standard query rather than undefined stackalloc contents.
    private static byte[] LegacyBuildQuery(string host, ushort transactionId)
    {
        using var stream = new MemoryStream();
        Span<byte> header = stackalloc byte[12];
        header.Clear();
        BinaryPrimitives.WriteUInt16BigEndian(header, transactionId);
        BinaryPrimitives.WriteUInt16BigEndian(header[2..], 0x0100);
        BinaryPrimitives.WriteUInt16BigEndian(header[4..], 1);
        stream.Write(header);

        foreach (var label in host.Split('.'))
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            if (bytes.Length is 0 or > 63)
            {
                throw new InvalidOperationException($"Invalid DNS label in '{host}'.");
            }

            stream.WriteByte((byte)bytes.Length);
            stream.Write(bytes);
        }

        stream.WriteByte(0);
        Span<byte> questionTail = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(questionTail, 1);
        BinaryPrimitives.WriteUInt16BigEndian(questionTail[2..], 1);
        stream.Write(questionTail);
        return stream.ToArray();
    }

    private readonly record struct TimingResult(
        double OptimizedMedianNs,
        double PredecessorMedianNs,
        double Ratio);
}
