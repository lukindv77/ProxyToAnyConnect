using System.Diagnostics;
using System.Text;
using ProxyToAnyConnect.Diagnostics;

namespace ProxyToAnyConnect.SelfTests;

internal static class DailyLogEncodingSelfTests
{
    private const int WarmupIterations = 4096;
    private const int AllocationIterations = 1000;
    private const int TimingRounds = 15;
    private const int IterationsPerRound = 65536;
    private const double MaxMedianSlowdownRatio = 1.25;

    public static int Run()
    {
        try
        {
            Utf8EncodingIsSharedAndBomFree();
            StaleStoreFailureDoesNotDisableReplacement();
            MeasureEncodingReuse();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: daily JSONL encoding/store-generation regression: {ex}");
            return 1;
        }
    }

    private static void Utf8EncodingIsSharedAndBomFree()
    {
        var first = DailyJsonlLogStore.JsonlEncoding;
        var second = DailyJsonlLogStore.JsonlEncoding;
        if (!ReferenceEquals(first, second) || first.GetPreamble().Length != 0)
        {
            throw new InvalidOperationException("JSONL UTF-8 encoding is not a shared BOM-free instance.");
        }

        const string line = "jsonl-данные-✓";
        var root = CreateTempDirectory();
        try
        {
            using (var store = new DailyJsonlLogStore(root, retentionDays: 7))
            {
                store.AppendLine(line, DateTimeOffset.Now);
            }

            var path = Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories).Single();
            var bytes = File.ReadAllBytes(path);
            if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
            {
                throw new InvalidOperationException("JSONL file unexpectedly starts with a UTF-8 BOM.");
            }

            if (Encoding.UTF8.GetString(bytes) != line + Environment.NewLine)
            {
                throw new InvalidOperationException("JSONL encoding reuse changed written text semantics.");
            }
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    private static void StaleStoreFailureDoesNotDisableReplacement()
    {
        var firstRoot = CreateTempDirectory();
        var secondRoot = CreateTempDirectory();
        try
        {
            using var first = new DailyJsonlLogStore(firstRoot, retentionDays: 7);
            using var second = new DailyJsonlLogStore(secondRoot, retentionDays: 7);
            if (!AppLog.ShouldDisableFileLoggingAfterFailure(first, first) ||
                AppLog.ShouldDisableFileLoggingAfterFailure(first, second))
            {
                throw new InvalidOperationException(
                    "Stale JSONL store failure classification can disable a replacement store.");
            }
        }
        finally
        {
            TryDeleteDirectory(firstRoot);
            TryDeleteDirectory(secondRoot);
        }
    }

    private static void MeasureEncodingReuse()
    {
        for (var i = 0; i < WarmupIterations; i++)
        {
            RunSharedEncoding();
            RunPerAppendEncodingPredecessor();
        }

        var optimizedBytes = MeasureAllocatedBytes(RunSharedEncoding);
        var predecessorBytes = MeasureAllocatedBytes(RunPerAppendEncodingPredecessor);
        if (optimizedBytes >= predecessorBytes)
        {
            throw new InvalidOperationException(
                $"Shared JSONL encoding allocated {optimizedBytes} bytes versus " +
                $"{predecessorBytes} bytes for per-append construction.");
        }

        var timing = MeasurePaired(RunSharedEncoding, RunPerAppendEncodingPredecessor);
        if (timing.Ratio > MaxMedianSlowdownRatio)
        {
            throw new InvalidOperationException(
                $"Shared JSONL encoding lookup median was {timing.OptimizedMedianNs:F0} ns/op versus " +
                $"{timing.PredecessorMedianNs:F0} ns/op ({timing.Ratio:F2}x, limit {MaxMedianSlowdownRatio:F2}x).");
        }

        Console.WriteLine(
            $"PASS: shared JSONL UTF-8 encoding removes per-append encoding allocation " +
            $"(alloc {optimizedBytes / (double)AllocationIterations:F0} vs " +
            $"{predecessorBytes / (double)AllocationIterations:F0} bytes/setup; " +
            $"timing {timing.OptimizedMedianNs:F0} vs {timing.PredecessorMedianNs:F0} ns/op, " +
            $"{timing.Ratio:F2}x)");
    }

    private static void RunSharedEncoding() =>
        GC.KeepAlive(DailyJsonlLogStore.JsonlEncoding);

    private static void RunPerAppendEncodingPredecessor() =>
        GC.KeepAlive(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

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

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "ProxyToAnyConnect-SelfTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }

    private readonly record struct TimingResult(
        double OptimizedMedianNs,
        double PredecessorMedianNs,
        double Ratio);
}
