using ProxyToAnyConnect.Diagnostics;
using ProxyToAnyConnect.Runtime;

namespace ProxyToAnyConnect.SelfTests;

internal static class LoggingAndMetricsSelfTests
{
    public static async Task<int> RunAsync()
    {
        var failed = 0;
        failed += Run("Traffic counters are thread-safe", TrafficCountersAreThreadSafe);
        failed += Run("Rolling ping keeps only last five minutes", RollingPingKeepsFiveMinutes);
        failed += Run("Daily log path uses YYYY-MM folders", DailyLogPathUsesMonthlyFolder);
        failed += Run("Daily log appends without replacing file", DailyLogAppends);
        failed += Run("Log retention removes expired daily files", LogRetentionRemovesExpiredFiles);
        await Task.CompletedTask;
        return failed;
    }

    private static void TrafficCountersAreThreadSafe()
    {
        var counter = new TrafficCounter();
        Parallel.For(0, 10_000, _ =>
        {
            counter.AddReceived(3);
            counter.AddSent(7);
        });

        var snapshot = counter.Snapshot();
        Assert(snapshot.ReceivedBytes == 30_000, $"Unexpected RX {snapshot.ReceivedBytes}.");
        Assert(snapshot.SentBytes == 70_000, $"Unexpected TX {snapshot.SentBytes}.");
    }

    private static void RollingPingKeepsFiveMinutes()
    {
        var now = new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);
        var window = new RollingPingWindow();
        window.AddSuccessfulSample(TimeSpan.FromMilliseconds(900), now.AddMinutes(-6));
        window.AddSuccessfulSample(TimeSpan.FromMilliseconds(100), now.AddMinutes(-4));
        window.AddSuccessfulSample(TimeSpan.FromMilliseconds(200), now.AddMinutes(-1));

        var snapshot = window.Snapshot(now);
        Assert(snapshot.SampleCount == 2, $"Unexpected ping sample count {snapshot.SampleCount}.");
        Assert(snapshot.AverageMilliseconds is double average && Math.Abs(average - 150) < 0.001,
            $"Unexpected ping average {snapshot.AverageMilliseconds}.");
    }

    private static void DailyLogPathUsesMonthlyFolder()
    {
        var path = DailyJsonlLogStore.BuildRelativeDailyPath(new DateOnly(2026, 3, 7));
        var expected = Path.Combine("2026-03", "2026-03-07.jsonl");
        Assert(path == expected, $"Unexpected daily log path '{path}'.");
    }

    private static void DailyLogAppends()
    {
        var root = CreateTempDirectory();
        try
        {
            string path;
            using (var store = new DailyJsonlLogStore(root, 30))
            {
                var now = DateTimeOffset.Now;
                store.AppendLine("one", now);
                store.AppendLine("two", now);
                path = store.CurrentFilePath ?? throw new InvalidOperationException("No current log file.");
                Assert(File.Exists(path), "Daily log file was not created.");
            }

            var lines = File.ReadAllLines(path);
            Assert(lines.SequenceEqual(["one", "two"]), "Daily log file was not append-only.");
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    private static void LogRetentionRemovesExpiredFiles()
    {
        var root = CreateTempDirectory();
        try
        {
            var month = Path.Combine(root, "2026-08");
            Directory.CreateDirectory(month);
            var expired = Path.Combine(month, "2026-08-10.jsonl");
            var retained = Path.Combine(month, "2026-08-24.jsonl");
            File.WriteAllText(expired, "old");
            File.WriteAllText(retained, "new");

            using var store = new DailyJsonlLogStore(root, retentionDays: 7);
            store.CleanupRetention(today: new DateOnly(2026, 8, 26));

            Assert(!File.Exists(expired), "Expired log file was not deleted.");
            Assert(File.Exists(retained), "Retained log file was deleted unexpectedly.");
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    private static int Run(string name, Action action)
    {
        try
        {
            action();
            Console.WriteLine($"PASS: {name}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: {name}: {ex}");
            return 1;
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
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
}
