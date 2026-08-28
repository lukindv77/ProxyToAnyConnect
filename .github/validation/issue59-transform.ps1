Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Replace-Exact {
    param(
        [Parameter(Mandatory = $true)] [string] $Path,
        [Parameter(Mandatory = $true)] [string] $Old,
        [Parameter(Mandatory = $true)] [string] $New,
        [int] $ExpectedCount = 1
    )

    $raw = [IO.File]::ReadAllText($Path)
    $useCrLf = $raw.Contains("`r`n")
    $text = $raw.Replace("`r`n", "`n")
    $oldNormalized = $Old.Replace("`r`n", "`n").TrimEnd("`r", "`n")
    $newNormalized = $New.Replace("`r`n", "`n").TrimEnd("`r", "`n")
    $actualCount = [regex]::Matches($text, [regex]::Escape($oldNormalized)).Count
    if ($actualCount -ne $ExpectedCount) {
        $anchor = (($oldNormalized -split "`n") | Where-Object { $_.Trim().Length -gt 0 } | Select-Object -First 1).Trim()
        throw "Expected $ExpectedCount exact replacement target(s) in '$Path' for '$anchor', found $actualCount."
    }

    $updated = $text.Replace($oldNormalized, $newNormalized)
    if ($useCrLf) {
        $updated = $updated.Replace("`n", "`r`n")
    }

    [IO.File]::WriteAllText($Path, $updated, [Text.UTF8Encoding]::new($false))
}

$store = 'src/ProxyToAnyConnect/Diagnostics/DailyJsonlLogStore.cs'
$tests = 'tests/ProxyToAnyConnect.SelfTests/LoggingAndMetricsSelfTests.cs'

Replace-Exact $store @'
            var fullPath = _dailyPathCache.Resolve(
                _rootDirectory,
                localDate,
                out var pathChanged);
            if (pathChanged)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            }

            // Append exactly one JSONL record. The existing file is never read or
'@ @'
            var fullPath = _dailyPathCache.Resolve(
                _rootDirectory,
                localDate,
                out var pathChanged);
            var monthDirectory = Path.GetDirectoryName(fullPath)!;
            if (pathChanged)
            {
                Directory.CreateDirectory(monthDirectory);
            }

            // A yyyy-MM name is not filesystem ownership proof. Reject month/file
            // reparse points before opening the append handle so a junction/symlink
            // cannot redirect structured logging outside the configured tree.
            EnsureRegularAppendPath(monthDirectory, fullPath);

            // Append exactly one JSONL record. The existing file is never read or
'@

Replace-Exact $store @'
            using var stream = new FileStream(
                fullPath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4 * 1024,
                FileOptions.SequentialScan);
            using var writer = new StreamWriter(
'@ @'
            using var stream = new FileStream(
                fullPath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4 * 1024,
                FileOptions.SequentialScan);

            // Re-check after opening but before the first write. This catches a path
            // substitution that happened between the pre-open ownership check and
            // FileStream construction; the already-open handle is still untouched.
            EnsureRegularAppendPath(monthDirectory, fullPath);

            using var writer = new StreamWriter(
'@

Replace-Exact $store @'
            var monthName = Path.GetFileName(monthDirectory);
            if (!IsMonthDirectoryName(monthName))
            {
                continue;
            }

            foreach (var filePath in Directory.EnumerateFiles(monthDirectory, "*.jsonl", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryParseDailyFileName(Path.GetFileName(filePath), out var fileDate))
'@ @'
            var monthName = Path.GetFileName(monthDirectory);
            if (!IsMonthDirectoryName(monthName) || !IsRegularDirectory(monthDirectory))
            {
                // Never enumerate through a junction/symlink merely because its leaf
                // name looks like an application-owned yyyy-MM log directory.
                continue;
            }

            foreach (var filePath in Directory.EnumerateFiles(monthDirectory, "*.jsonl", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsRegularFile(filePath) ||
                    !TryParseDailyFileName(Path.GetFileName(filePath), out var fileDate))
'@

Replace-Exact $store @'
    private static bool IsMonthDirectoryName(string name) =>
        DateTime.TryParseExact(
            name,
            "yyyy-MM",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out _);

    private static void TryDelete(string path)
'@ @'
    private static bool IsMonthDirectoryName(string name) =>
        DateTime.TryParseExact(
            name,
            "yyyy-MM",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out _);

    private static void EnsureRegularAppendPath(string monthDirectory, string filePath)
    {
        if (!IsRegularDirectory(monthDirectory))
        {
            throw new IOException(
                $"Structured log month directory is not an ordinary owned directory: {monthDirectory}");
        }

        if (File.Exists(filePath) && !IsRegularFile(filePath))
        {
            throw new IOException(
                $"Structured log daily file is not an ordinary owned file: {filePath}");
        }
    }

    private static bool IsRegularDirectory(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return (attributes & FileAttributes.Directory) != 0 &&
                   (attributes & FileAttributes.ReparsePoint) == 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsRegularFile(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            var attributes = File.GetAttributes(path);
            return (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void TryDelete(string path)
'@

Replace-Exact $store @'
    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
'@ @'
    private static void TryDelete(string path)
    {
        try
        {
            // Re-check immediately before deletion so a previously enumerated file
            // that became a reparse point is preserved rather than treated as owned.
            if (!IsRegularFile(path))
            {
                return;
            }

            File.Delete(path);
        }
'@

Replace-Exact $store @'
    private static void TryDeleteDirectoryIfEmpty(string path)
    {
        try
        {
            if (!Directory.EnumerateFileSystemEntries(path).Any())
            {
                Directory.Delete(path);
            }
'@ @'
    private static void TryDeleteDirectoryIfEmpty(string path)
    {
        try
        {
            if (!IsRegularDirectory(path))
            {
                return;
            }

            if (!Directory.EnumerateFileSystemEntries(path).Any() &&
                IsRegularDirectory(path))
            {
                Directory.Delete(path);
            }
'@

Replace-Exact $tests @'
        failed += Run("Daily log appends without replacing file", DailyLogAppends);
        failed += Run("Log retention removes expired daily files", LogRetentionRemovesExpiredFiles);
        failed += Run(
'@ @'
        failed += Run("Daily log appends without replacing file", DailyLogAppends);
        failed += Run("Log retention removes expired daily files", LogRetentionRemovesExpiredFiles);
        failed += Run("Log retention skips reparse-point month directories", LogRetentionSkipsReparseMonthDirectory);
        failed += Run("Daily log append rejects reparse-point paths", DailyLogAppendRejectsReparsePaths);
        failed += Run(
'@

Replace-Exact $tests @'
    private static void LogConfigurationFailureIsFailSoftAndRecoverable()
'@ @'
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
'@
