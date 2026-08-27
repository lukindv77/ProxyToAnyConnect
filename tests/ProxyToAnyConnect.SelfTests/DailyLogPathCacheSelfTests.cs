using System.Diagnostics;
using ProxyToAnyConnect.Diagnostics;

namespace ProxyToAnyConnect.SelfTests;

internal static class DailyLogPathCacheSelfTests
{
    private const int WarmupIterations = 4096;
    private const int AllocationIterations = 1000;
    private const int TimingRounds = 15;
    private const int IterationsPerRound = 65536;
    private const double MaxMedianSlowdownRatio = 1.25;
    private static readonly DateOnly RepresentativeDate = new(2026, 8, 26);
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "ProxyToAnyConnect-log-path-selftest");
    private static DailyFilePathCache _optimizedCache;
    private static int _sink;

    public static int Run()
    {
        try
        {
            RolloverAndReuseSemanticsRemainEquivalent();
            _ = _optimizedCache.Resolve(Root, RepresentativeDate, out _);

            for (var i = 0; i < WarmupIterations; i++)
            {
                RunOptimizedSameDay();
                RunAlwaysRebuildPredecessor();
            }

            var optimizedBytes = MeasureAllocatedBytes(RunOptimizedSameDay);
            var predecessorBytes = MeasureAllocatedBytes(RunAlwaysRebuildPredecessor);
            if (optimizedBytes >= predecessorBytes)
            {
                throw new InvalidOperationException(
                    $"Daily path cache allocated {optimizedBytes} bytes versus " +
                    $"{predecessorBytes} bytes for always-rebuild path setup.");
            }

            var timing = MeasurePaired(RunOptimizedSameDay, RunAlwaysRebuildPredecessor);
            if (timing.Ratio > MaxMedianSlowdownRatio)
            {
                throw new InvalidOperationException(
                    $"Daily path cache median was {timing.OptimizedMedianNs:F0} ns/op versus " +
                    $"{timing.PredecessorMedianNs:F0} ns/op for always-rebuild setup " +
                    $"({timing.Ratio:F2}x, limit {MaxMedianSlowdownRatio:F2}x).");
            }

            Console.WriteLine(
                $"PASS: same-day JSONL path cache removes repeated path setup " +
                $"(alloc {optimizedBytes / (double)AllocationIterations:F0} vs " +
                $"{predecessorBytes / (double)AllocationIterations:F0} bytes/resolve; " +
                $"timing {timing.OptimizedMedianNs:F0} vs {timing.PredecessorMedianNs:F0} ns/op, " +
                $"{timing.Ratio:F2}x)");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: daily JSONL path cache regression: {ex}");
            return 1;
        }
    }

    private static void RolloverAndReuseSemanticsRemainEquivalent()
    {
        var cache = new DailyFilePathCache();
        var first = cache.Resolve(Root, RepresentativeDate, out var firstChanged);
        var second = cache.Resolve(Root, RepresentativeDate, out var secondChanged);
        if (!firstChanged || secondChanged || !ReferenceEquals(first, second))
        {
            throw new InvalidOperationException("Same-day daily log path was not reused exactly.");
        }

        var expectedFirst = Path.Combine(Root, DailyJsonlLogStore.BuildRelativeDailyPath(RepresentativeDate));
        if (!string.Equals(first, expectedFirst, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Cached daily log path changed hierarchy semantics.");
        }

        var nextDate = RepresentativeDate.AddDays(1);
        var next = cache.Resolve(Root, nextDate, out var nextChanged);
        var expectedNext = Path.Combine(Root, DailyJsonlLogStore.BuildRelativeDailyPath(nextDate));
        if (!nextChanged || !string.Equals(next, expectedNext, StringComparison.Ordinal) || ReferenceEquals(first, next))
        {
            throw new InvalidOperationException("Daily log path cache did not roll over on local date change.");
        }
    }

    private static void RunOptimizedSameDay()
    {
        var path = _optimizedCache.Resolve(Root, RepresentativeDate, out var changed);
        if (changed)
        {
            throw new InvalidOperationException("Warmed same-day path unexpectedly rebuilt.");
        }

        _sink ^= path.Length;
    }

    private static void RunAlwaysRebuildPredecessor()
    {
        var path = Path.Combine(Root, DailyJsonlLogStore.BuildRelativeDailyPath(RepresentativeDate));
        _sink ^= path.Length;
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
