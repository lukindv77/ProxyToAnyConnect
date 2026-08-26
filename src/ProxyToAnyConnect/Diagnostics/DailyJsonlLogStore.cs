using System.Globalization;
using System.Text;

namespace ProxyToAnyConnect.Diagnostics;

internal sealed class DailyJsonlLogStore : IDisposable
{
    private readonly object _gate = new();
    private readonly string _rootDirectory;
    private readonly int _retentionDays;

    private FileStream? _stream;
    private StreamWriter? _writer;
    private DateOnly _openDate;
    private string? _currentFilePath;
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

        lock (_gate)
        {
            EnsureWriterLocked(localDate);
            _writer!.WriteLine(line);
            _writer.Flush();
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

    private void EnsureWriterLocked(DateOnly date)
    {
        if (_writer is not null && _openDate == date)
        {
            return;
        }

        CloseWriterLocked();

        var relativePath = BuildRelativeDailyPath(date);
        var fullPath = Path.Combine(_rootDirectory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        _stream = new FileStream(
            fullPath,
            FileMode.Append,
            FileAccess.Write,
            FileShare.ReadWrite,
            bufferSize: 16 * 1024,
            FileOptions.SequentialScan);
        _writer = new StreamWriter(_stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), 16 * 1024)
        {
            AutoFlush = false
        };
        _openDate = date;
        _currentFilePath = fullPath;
    }

    private void CloseWriterLocked()
    {
        try
        {
            _writer?.Flush();
        }
        catch (IOException)
        {
        }

        _writer?.Dispose();
        _stream?.Dispose();
        _writer = null;
        _stream = null;
        _currentFilePath = null;
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
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        lock (_gate)
        {
            CloseWriterLocked();
        }
    }
}
