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
        failed += Run("Log retention skips reparse-point month directories", LogRetentionSkipsReparseMonthDirectory);
        failed += Run("Daily log append rejects reparse-point paths", DailyLogAppendRejectsReparsePaths);
        failed += Run(
            "Log configuration is fail-soft, transactional and explicitly releasable",
            LogConfigurationFailureIsFailSoftAndRecoverable);
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

    private static void LogRetentionSkipsReparseMonthDirectory()
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("SKIP: log reparse ownership test requires Windows.");
            return;
        }

        var root = CreateTempDirectory();
        var external = CreateTempDirectory();
        var monthLink = Path.Combine(root, "2026-08");
        try
        {
            var externalExpired = Path.Combine(external, "2026-08-10.jsonl");
            File.WriteAllText(externalExpired, "external-old");
            if (!TryCreateDirectorySymbolicLink(monthLink, external))
            {
                Console.WriteLine("SKIP: Windows runner cannot create directory symbolic links for log ownership regression.");
                return;
            }

            using var store = new DailyJsonlLogStore(root, retentionDays: 7);
            store.CleanupRetention(today: new DateOnly(2026, 8, 26));

            Assert(File.Exists(externalExpired),
                "Log retention traversed a reparse-point month directory and deleted an external file.");
            Assert(File.ReadAllText(externalExpired) == "external-old",
                "Log retention modified an external file through a reparse-point month directory.");
        }
        finally
        {
            TryDeleteLink(monthLink, directory: true);
            TryDeleteDirectory(root);
            TryDeleteDirectory(external);
        }
    }

    private static void DailyLogAppendRejectsReparsePaths()
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("SKIP: log reparse ownership test requires Windows.");
            return;
        }

        var timestamp = new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero).ToLocalTime();
        var localDate = DateOnly.FromDateTime(timestamp.DateTime);
        var relative = DailyJsonlLogStore.BuildRelativeDailyPath(localDate);

        var fileRoot = CreateTempDirectory();
        var externalFileRoot = CreateTempDirectory();
        var fileMonth = Path.Combine(fileRoot, localDate.ToString("yyyy-MM"));
        Directory.CreateDirectory(fileMonth);
        var linkedDailyFile = Path.Combine(fileRoot, relative);
        var externalTarget = Path.Combine(externalFileRoot, "external-target.txt");
        File.WriteAllText(externalTarget, "sentinel");

        try
        {
            if (TryCreateFileSymbolicLink(linkedDailyFile, externalTarget))
            {
                AssertAppendOwnershipRejected(fileRoot, timestamp);
                Assert(File.ReadAllText(externalTarget) == "sentinel",
                    "Structured logging appended through a daily-file symbolic link.");
            }
            else
            {
                Console.WriteLine("SKIP: Windows runner cannot create file symbolic links for log ownership regression.");
            }
        }
        finally
        {
            TryDeleteLink(linkedDailyFile, directory: false);
            TryDeleteDirectory(fileRoot);
            TryDeleteDirectory(externalFileRoot);
        }

        var directoryRoot = CreateTempDirectory();
        var externalDirectory = CreateTempDirectory();
        var monthLink = Path.Combine(directoryRoot, localDate.ToString("yyyy-MM"));
        try
        {
            if (TryCreateDirectorySymbolicLink(monthLink, externalDirectory))
            {
                AssertAppendOwnershipRejected(directoryRoot, timestamp);
                var externalDaily = Path.Combine(externalDirectory, Path.GetFileName(relative));
                Assert(!File.Exists(externalDaily),
                    "Structured logging followed a reparse-point month directory outside the configured root.");
            }
            else
            {
                Console.WriteLine("SKIP: Windows runner cannot create directory symbolic links for append regression.");
            }
        }
        finally
        {
            TryDeleteLink(monthLink, directory: true);
            TryDeleteDirectory(directoryRoot);
            TryDeleteDirectory(externalDirectory);
        }
    }

    private static void AssertAppendOwnershipRejected(string root, DateTimeOffset timestamp)
    {
        using var store = new DailyJsonlLogStore(root, retentionDays: 30);
        try
        {
            store.AppendLine("must-not-escape", timestamp);
        }
        catch (IOException)
        {
            return;
        }

        throw new InvalidOperationException(
            "Structured log append accepted a reparse-point month/file path.");
    }

    private static bool TryCreateDirectorySymbolicLink(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool TryCreateFileSymbolicLink(string linkPath, string targetPath)
    {
        try
        {
            File.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException or NotSupportedException)
        {
            return false;
        }
    }

    private static void TryDeleteLink(string path, bool directory)
    {
        try
        {
            if (directory)
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: false);
                }
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static void LogConfigurationFailureIsFailSoftAndRecoverable()
    {
        var firstRoot = CreateTempDirectory();
        var secondRoot = CreateTempDirectory();
        try
        {
            AppLog.Shutdown();
            AppLog.Configure(firstRoot, retentionDays: 30, consoleJson: false);
            Assert(
                string.Equals(AppLog.LogRootDirectory, Path.GetFullPath(firstRoot), StringComparison.OrdinalIgnoreCase),
                "Initial valid logging configuration was not installed.");

            AppLog.Info("selftest.logging.first", "first");
            var firstFile = AppLog.CurrentLogFile
                ?? throw new InvalidOperationException("Initial logging store did not append a record.");

            AppLog.Configure("invalid\0log-root", retentionDays: 30, consoleJson: false);
            Assert(
                string.Equals(AppLog.LogRootDirectory, Path.GetFullPath(firstRoot), StringComparison.OrdinalIgnoreCase),
                "Malformed replacement log path disabled or replaced the healthy store.");

            AppLog.Configure(secondRoot, retentionDays: 0, consoleJson: false);
            Assert(
                string.Equals(AppLog.LogRootDirectory, Path.GetFullPath(firstRoot), StringComparison.OrdinalIgnoreCase),
                "Invalid retention replaced the healthy logging store.");

            AppLog.Info("selftest.logging.after_rejection", "still using first store");
            Assert(
                File.ReadAllLines(firstFile).Length >= 2,
                "Healthy logging store stopped accepting records after rejected configuration.");

            AppLog.Configure(secondRoot, retentionDays: 7, consoleJson: false);
            Assert(
                string.Equals(AppLog.LogRootDirectory, Path.GetFullPath(secondRoot), StringComparison.OrdinalIgnoreCase),
                "Valid logging configuration did not recover after rejected replacements.");

            AppLog.Info("selftest.logging.second", "second");
            var secondFile = AppLog.CurrentLogFile
                ?? throw new InvalidOperationException("Replacement logging store did not append a record.");
            Assert(
                secondFile.StartsWith(Path.GetFullPath(secondRoot), StringComparison.OrdinalIgnoreCase),
                "Recovered logging record was not written under the replacement root.");

            AppLog.Shutdown();
            Assert(AppLog.LogRootDirectory is null, "AppLog.Shutdown retained the active store owner.");
        }
        finally
        {
            AppLog.Shutdown();
            TryDeleteDirectory(firstRoot);
            TryDeleteDirectory(secondRoot);
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
