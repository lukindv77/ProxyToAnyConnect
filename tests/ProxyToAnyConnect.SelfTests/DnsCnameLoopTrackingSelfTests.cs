using System.Diagnostics;
using ProxyToAnyConnect.Network;

namespace ProxyToAnyConnect.SelfTests;

internal static class DnsCnameLoopTrackingSelfTests
{
    private const int WarmupIterations = 4096;
    private const int AllocationIterations = 1000;
    private const int TimingRounds = 9;
    private const int IterationsPerRound = 65536;
    private const double MaxMedianSlowdownRatio = 1.25;
    private const string RepresentativeHost = "api.service.example.internal";

    public static int Run()
    {
        try
        {
            LoopSemanticsRemainEquivalent();

            for (var i = 0; i < WarmupIterations; i++)
            {
                RunOptimizedDirectPath();
                RunEagerPredecessorDirectPath();
            }

            var optimizedBytes = MeasureAllocatedBytes(RunOptimizedDirectPath);
            var predecessorBytes = MeasureAllocatedBytes(RunEagerPredecessorDirectPath);
            if (optimizedBytes >= predecessorBytes)
            {
                throw new InvalidOperationException(
                    $"Lazy CNAME loop tracking allocated {optimizedBytes} bytes versus " +
                    $"{predecessorBytes} bytes for the eager HashSet predecessor.");
            }

            var timing = MeasurePaired(RunOptimizedDirectPath, RunEagerPredecessorDirectPath);
            if (timing.Ratio > MaxMedianSlowdownRatio)
            {
                throw new InvalidOperationException(
                    $"Lazy CNAME loop tracking direct path median was {timing.OptimizedMedianNs:F0} ns/op versus " +
                    $"{timing.PredecessorMedianNs:F0} ns/op for eager HashSet setup " +
                    $"({timing.Ratio:F2}x, limit {MaxMedianSlowdownRatio:F2}x).");
            }

            Console.WriteLine(
                $"PASS: DNS direct-A path avoids eager CNAME loop-tracker allocation " +
                $"(alloc {optimizedBytes / (double)AllocationIterations:F0} vs " +
                $"{predecessorBytes / (double)AllocationIterations:F0} bytes/query; " +
                $"timing {timing.OptimizedMedianNs:F0} vs {timing.PredecessorMedianNs:F0} ns/op, " +
                $"{timing.Ratio:F2}x)");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: DNS lazy CNAME loop tracking regression: {ex}");
            return 1;
        }
    }

    private static void LoopSemanticsRemainEquivalent()
    {
        if (!L2tpDnsResolver.TryEnterDnsName(null, RepresentativeHost))
        {
            throw new InvalidOperationException("A direct root lookup was rejected without a CNAME tracker.");
        }

        HashSet<string>? visited = null;
        visited = L2tpDnsResolver.EnsureVisitedNamesForCname(visited, RepresentativeHost);
        if (!visited.Contains(RepresentativeHost))
        {
            throw new InvalidOperationException("First CNAME recursion did not seed the root host.");
        }

        if (L2tpDnsResolver.TryEnterDnsName(visited, RepresentativeHost.ToUpperInvariant()))
        {
            throw new InvalidOperationException("First-hop self-loop detection lost case-insensitive semantics.");
        }

        const string alias = "edge.service.example.internal";
        if (!L2tpDnsResolver.TryEnterDnsName(visited, alias))
        {
            throw new InvalidOperationException("A new CNAME hop was rejected.");
        }

        var sameTracker = L2tpDnsResolver.EnsureVisitedNamesForCname(visited, alias);
        if (!ReferenceEquals(visited, sameTracker))
        {
            throw new InvalidOperationException("Existing CNAME loop-tracking state was recreated.");
        }

        const string secondAlias = "origin.service.example.internal";
        if (!L2tpDnsResolver.TryEnterDnsName(visited, secondAlias))
        {
            throw new InvalidOperationException("A second unique CNAME hop was rejected.");
        }

        if (L2tpDnsResolver.TryEnterDnsName(visited, alias.ToUpperInvariant()))
        {
            throw new InvalidOperationException("Multi-hop loop detection lost case-insensitive semantics.");
        }
    }

    private static void RunOptimizedDirectPath()
    {
        if (!L2tpDnsResolver.TryEnterDnsName(null, RepresentativeHost))
        {
            throw new InvalidOperationException("Unexpected direct-path loop rejection.");
        }
    }

    private static void RunEagerPredecessorDirectPath()
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!visited.Add(RepresentativeHost))
        {
            throw new InvalidOperationException("Unexpected predecessor loop rejection.");
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

    private readonly record struct TimingResult(
        double OptimizedMedianNs,
        double PredecessorMedianNs,
        double Ratio);
}
