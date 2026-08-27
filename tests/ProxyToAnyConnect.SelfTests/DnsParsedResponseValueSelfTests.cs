using System.Diagnostics;
using System.Net;
using ProxyToAnyConnect.Network;

namespace ProxyToAnyConnect.SelfTests;

internal static class DnsParsedResponseValueSelfTests
{
    private const int WarmupIterations = 4096;
    private const int AllocationIterations = 1000;
    private const int TimingRounds = 15;
    private const int IterationsPerRound = 65536;
    private const double MaxMedianSlowdownRatio = 1.25;
    private static int _sink;

    public static int Run()
    {
        try
        {
            ResultSemanticsRemainEquivalent();

            for (var i = 0; i < WarmupIterations; i++)
            {
                RunOptimized();
                RunReferencePredecessor();
            }

            var optimizedBytes = MeasureAllocatedBytes(RunOptimized);
            var predecessorBytes = MeasureAllocatedBytes(RunReferencePredecessor);
            if (optimizedBytes >= predecessorBytes)
            {
                throw new InvalidOperationException(
                    $"Value ParsedDnsResponse allocated {optimizedBytes} bytes versus " +
                    $"{predecessorBytes} bytes for the reference-record predecessor.");
            }

            var timing = MeasurePaired(RunOptimized, RunReferencePredecessor);
            if (timing.Ratio > MaxMedianSlowdownRatio)
            {
                throw new InvalidOperationException(
                    $"Value ParsedDnsResponse paired median ratio was {timing.Ratio:F2}x " +
                    $"(representative medians {timing.OptimizedMedianNs:F0} vs " +
                    $"{timing.PredecessorMedianNs:F0} ns/op, limit {MaxMedianSlowdownRatio:F2}x).");
            }

            Console.WriteLine(
                $"PASS: DNS parsed-response value carrier removes result-object allocation " +
                $"(alloc {optimizedBytes / (double)AllocationIterations:F0} vs " +
                $"{predecessorBytes / (double)AllocationIterations:F0} bytes/result; " +
                $"timing {timing.OptimizedMedianNs:F0} vs {timing.PredecessorMedianNs:F0} ns/op, " +
                $"paired {timing.Ratio:F2}x)");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: DNS parsed-response value regression: {ex}");
            return 1;
        }
    }

    private static void ResultSemanticsRemainEquivalent()
    {
        IReadOnlyList<IPAddress> addresses =
        [
            IPAddress.Parse("203.0.113.7"),
            IPAddress.Parse("203.0.113.8")
        ];
        const string cname = "origin.service.example.internal";
        const uint ttl = 37;

        var actual = new ParsedDnsResponse(addresses, cname, Truncated: false, MinimumTtlSeconds: ttl);
        var expected = new LegacyParsedDnsResponse(addresses, cname, Truncated: false, MinimumTtlSeconds: ttl);
        if (!ReferenceEquals(actual.Addresses, expected.Addresses) ||
            actual.CanonicalName != expected.CanonicalName ||
            actual.Truncated != expected.Truncated ||
            actual.MinimumTtlSeconds != expected.MinimumTtlSeconds)
        {
            throw new InvalidOperationException("Parsed DNS response carrier changed field semantics.");
        }

        var truncated = new ParsedDnsResponse(Array.Empty<IPAddress>(), null, Truncated: true, MinimumTtlSeconds: null);
        if (!truncated.Truncated || truncated.Addresses.Count != 0 || truncated.CanonicalName is not null || truncated.MinimumTtlSeconds is not null)
        {
            throw new InvalidOperationException("Truncated ParsedDnsResponse semantics changed.");
        }
    }

    private static void RunOptimized()
    {
        var result = new ParsedDnsResponse(
            Array.Empty<IPAddress>(),
            null,
            Truncated: false,
            MinimumTtlSeconds: null);
        _sink ^= result.Addresses.Count;
    }

    private static void RunReferencePredecessor()
    {
        var result = new LegacyParsedDnsResponse(
            Array.Empty<IPAddress>(),
            null,
            Truncated: false,
            MinimumTtlSeconds: null);
        _sink ^= result.Addresses.Count;
        GC.KeepAlive(result);
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
        var ratioRounds = new double[TimingRounds];
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

            ratioRounds[round] = optimizedRounds[round] / predecessorRounds[round];
        }

        return new TimingResult(
            Median(optimizedRounds),
            Median(predecessorRounds),
            Median(ratioRounds));
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

    private sealed record LegacyParsedDnsResponse(
        IReadOnlyList<IPAddress> Addresses,
        string? CanonicalName,
        bool Truncated,
        uint? MinimumTtlSeconds);

    private readonly record struct TimingResult(
        double OptimizedMedianNs,
        double PredecessorMedianNs,
        double Ratio);
}
