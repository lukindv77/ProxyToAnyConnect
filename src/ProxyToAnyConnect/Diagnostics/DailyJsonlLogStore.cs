using System.Globalization;
using System.Text;

namespace ProxyToAnyConnect.Diagnostics;

internal sealed class DailyJsonlLogStore : IDisposable
{
    private readonly object _gate = new();
    private readonly string _rootDirectory;
    private readonly int _retentionDays;

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
    }

    public string RootDirectory => _rootDirectory;
    public int RetentionDays => _retentionDays;

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
            if (pathChanged)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            }

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
            using var writer = new StreamWriter(
                stream,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
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
            _ = CleanupRetentionBestEffortAsync(localDate);
        }
    }

    public Task CleanupRetentionAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => CleanupRetention(cancellationToken), cancellationToken);

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
            if (!IsMonthDirectoryName(monthName))
            {
                continue;
            }

            foreach (var filePath in Directory.EnumerateFiles(monthDirectory, "*.jsonl", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryParseDailyFileName(Path.GetFileName(filePath), out var fileDate))
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

    private async Task CleanupRetentionBestEffortAsync(DateOnly localToday)
    {
        try
        {
            await Task.Run(() => CleanupRetention(today: localToday));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"Log retention cleanup failed: {ex.Message}");
        }
    }

    private static bool IsMonthDirectoryName(string name) =>
        DateTime.TryParseExact(
            name,
            "yyyy-MM",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out _);

    private static void TryDelete(string path)
    {
        try
        {
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
            if (!Directory.EnumerateFileSystemEntries(path).Any())
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
        Interlocked.Exchange(ref _disposed, 1);
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
