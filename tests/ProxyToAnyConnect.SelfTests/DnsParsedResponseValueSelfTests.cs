using System.Diagnostics;
using System.Net;
using ProxyToAnyConnect.Network;

namespace ProxyToAnyConnect.SelfTests;

internal static class DnsParsedResponseValueSelfTests
{
    private const int WarmupIterations = 4096;
    private const int AllocationIterations = 1000;
    private const int TimingRounds = 15;
    private const int InitialIterationsPerRound = 65536;
    private const int MaxIterationsPerRound = 32 * 1024 * 1024;
    private const double TargetTimingWindowMilliseconds = 30;
    private const double MinimumTimingWindowMilliseconds = 20;
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
            if (timing.MinimumRoundMilliseconds < MinimumTimingWindowMilliseconds)
            {
                throw new InvalidOperationException(
                    $"DNS parsed-response timing window was only {timing.MinimumRoundMilliseconds:F1} ms " +
                    $"at {timing.IterationsPerRound:N0} iterations; minimum is " +
                    $"{MinimumTimingWindowMilliseconds:F0} ms.");
            }

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
                $"paired {timing.Ratio:F2}x, {timing.IterationsPerRound:N0} iterations/side, " +
                $"minimum round {timing.MinimumRoundMilliseconds:F1} ms)");
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
        var iterationsPerRound = CalibrateIterations(optimized, predecessor);

        while (true)
        {
            var optimizedRounds = new double[TimingRounds];
            var predecessorRounds = new double[TimingRounds];
            var ratioRounds = new double[TimingRounds];
            var minimumRoundMilliseconds = double.MaxValue;

            for (var round = 0; round < TimingRounds; round++)
            {
                TimingSample optimizedSample;
                TimingSample predecessorSample;
                if ((round & 1) == 0)
                {
                    optimizedSample = MeasureTimingSample(optimized, iterationsPerRound);
                    predecessorSample = MeasureTimingSample(predecessor, iterationsPerRound);
                }
                else
                {
                    predecessorSample = MeasureTimingSample(predecessor, iterationsPerRound);
                    optimizedSample = MeasureTimingSample(optimized, iterationsPerRound);
                }

                optimizedRounds[round] = optimizedSample.NanosecondsPerOperation;
                predecessorRounds[round] = predecessorSample.NanosecondsPerOperation;
                ratioRounds[round] =
                    optimizedSample.NanosecondsPerOperation / predecessorSample.NanosecondsPerOperation;
                minimumRoundMilliseconds = Math.Min(
                    minimumRoundMilliseconds,
                    Math.Min(optimizedSample.ElapsedMilliseconds, predecessorSample.ElapsedMilliseconds));
            }

            if (minimumRoundMilliseconds >= MinimumTimingWindowMilliseconds)
            {
                return new TimingResult(
                    Median(optimizedRounds),
                    Median(predecessorRounds),
                    Median(ratioRounds),
                    iterationsPerRound,
                    minimumRoundMilliseconds);
            }

            if (iterationsPerRound >= MaxIterationsPerRound)
            {
                throw new InvalidOperationException(
                    $"DNS timing calibration reached {MaxIterationsPerRound:N0} iterations while a " +
                    $"measured round was only {minimumRoundMilliseconds:F1} ms.");
            }

            iterationsPerRound = Math.Min(MaxIterationsPerRound, checked(iterationsPerRound * 2));
        }
    }

    private static int CalibrateIterations(Action optimized, Action predecessor)
    {
        var iterations = InitialIterationsPerRound;
        while (true)
        {
            var optimizedSample = MeasureTimingSample(optimized, iterations);
            var predecessorSample = MeasureTimingSample(predecessor, iterations);
            var minimumMilliseconds = Math.Min(
                optimizedSample.ElapsedMilliseconds,
                predecessorSample.ElapsedMilliseconds);
            if (minimumMilliseconds >= TargetTimingWindowMilliseconds)
            {
                return iterations;
            }

            if (iterations >= MaxIterationsPerRound)
            {
                throw new InvalidOperationException(
                    $"DNS timing calibration could not reach a {TargetTimingWindowMilliseconds:F0} ms " +
                    $"target within {MaxIterationsPerRound:N0} iterations.");
            }

            var safeElapsed = Math.Max(minimumMilliseconds, 0.001);
            var requiredScale = Math.Max(
                2,
                (int)Math.Ceiling(TargetTimingWindowMilliseconds / safeElapsed));
            var requestedIterations = (long)iterations * requiredScale;
            iterations = (int)Math.Min(MaxIterationsPerRound, requestedIterations);
        }
    }

    private static TimingSample MeasureTimingSample(Action action, int iterations)
    {
        var started = Stopwatch.GetTimestamp();
        for (var i = 0; i < iterations; i++)
        {
            action();
        }

        var elapsedTicks = Stopwatch.GetTimestamp() - started;
        var elapsedMilliseconds = elapsedTicks * (1000.0 / Stopwatch.Frequency);
        var nanosecondsPerOperation =
            elapsedTicks * (1_000_000_000.0 / Stopwatch.Frequency) / iterations;
        return new TimingSample(nanosecondsPerOperation, elapsedMilliseconds);
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

    private readonly record struct TimingSample(
        double NanosecondsPerOperation,
        double ElapsedMilliseconds);

    private readonly record struct TimingResult(
        double OptimizedMedianNs,
        double PredecessorMedianNs,
        double Ratio,
        int IterationsPerRound,
        double MinimumRoundMilliseconds);
}
