using System.Globalization;
using System.Text;

namespace ProxyToAnyConnect.Diagnostics;

internal sealed class DailyJsonlLogStore : IDisposable
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly object _gate = new();
    private readonly string _rootDirectory;
    private readonly int _retentionDays;
    private readonly RetentionCleanupScheduler _retentionCleanupScheduler;

    private string? _currentFilePath;
    private DailyFilePathCache _dailyPathCache;
    private DateOnly? _lastRetentionCleanupDate;
    private int _disposed;

    public DailyJsonlLogStore(string rootDirectory, int retentionDays)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        if (retentionDays is < 1 or > 3650)
        {
            throw new ArgumentOutOfRangeException(nameof(retentionDays));
        }

        _rootDirectory = Path.GetFullPath(rootDirectory);
        _retentionDays = retentionDays;
        Directory.CreateDirectory(_rootDirectory);
        _retentionCleanupScheduler = new RetentionCleanupScheduler(
            (today, cancellationToken) => CleanupRetention(cancellationToken, today));
    }

    public string RootDirectory => _rootDirectory;
    public int RetentionDays => _retentionDays;
    internal static Encoding JsonlEncoding => Utf8NoBom;

    public string? CurrentFilePath
    {
        get
        {
            lock (_gate)
            {
                return _currentFilePath;
            }
        }
    }

    public void AppendLine(string line, DateTimeOffset? timestamp = null)
    {
        ArgumentNullException.ThrowIfNull(line);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var localNow = (timestamp ?? DateTimeOffset.Now).ToLocalTime();
        var localDate = DateOnly.FromDateTime(localNow.DateTime);
        var scheduleRetentionCleanup = false;

        lock (_gate)
        {
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
            // rewritten as part of logging. Closing the handle after the append keeps
            // the daily file freely viewable/copyable by ordinary Windows tools.
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
                stream,
                Utf8NoBom,
                bufferSize: 4 * 1024,
                leaveOpen: false);
            writer.WriteLine(line);
            writer.Flush();

            _currentFilePath = fullPath;
            if (_lastRetentionCleanupDate != localDate)
            {
                _lastRetentionCleanupDate = localDate;
                scheduleRetentionCleanup = true;
            }
        }

        if (scheduleRetentionCleanup)
        {
            _ = _retentionCleanupScheduler.Schedule(localDate);
        }
    }

    public Task CleanupRetentionAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var localToday = DateOnly.FromDateTime(DateTime.Now);
        var cleanupTask = _retentionCleanupScheduler.Schedule(localToday);
        return cancellationToken.CanBeCanceled
            ? cleanupTask.WaitAsync(cancellationToken)
            : cleanupTask;
    }

    internal void CleanupRetention(CancellationToken cancellationToken = default, DateOnly? today = null)
    {
        var localToday = today ?? DateOnly.FromDateTime(DateTime.Now);
        var oldestToKeep = localToday.AddDays(-(_retentionDays - 1));

        if (!Directory.Exists(_rootDirectory))
        {
            return;
        }

        foreach (var monthDirectory in Directory.EnumerateDirectories(_rootDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
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
                {
                    continue;
                }

                if (fileDate < oldestToKeep)
                {
                    TryDelete(filePath);
                }
            }

            TryDeleteDirectoryIfEmpty(monthDirectory);
        }
    }

    internal static string BuildRelativeDailyPath(DateOnly date) =>
        Path.Combine(
            date.ToString("yyyy-MM", CultureInfo.InvariantCulture),
            date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".jsonl");

    internal static bool TryParseDailyFileName(string fileName, out DateOnly date)
    {
        date = default;
        if (!fileName.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return DateOnly.TryParseExact(
            Path.GetFileNameWithoutExtension(fileName),
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out date);
    }

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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

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
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _retentionCleanupScheduler.Dispose();
    }
}

internal struct DailyFilePathCache
{
    private DateOnly? _date;
    private string? _fullPath;

    public string Resolve(string rootDirectory, DateOnly date, out bool changed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);

        if (_date == date && _fullPath is not null)
        {
            changed = false;
            return _fullPath;
        }

        _fullPath = Path.Combine(
            rootDirectory,
            DailyJsonlLogStore.BuildRelativeDailyPath(date));
        _date = date;
        changed = true;
        return _fullPath;
    }
}
