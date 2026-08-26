using System.Text.Json;
using ProxyToAnyConnect.Configuration;

namespace ProxyToAnyConnect.Diagnostics;

internal static class AppLog
{
    private static readonly object Gate = new();
    private static DailyJsonlLogStore? _store;
    private static bool _consoleJson;
    private static int _fileDisabled;

    public static string? CurrentLogFile => Volatile.Read(ref _store)?.CurrentFilePath;
    public static string? LogRootDirectory => Volatile.Read(ref _store)?.RootDirectory;

    // Compatibility bridge while the GUI/settings refactor is in progress.
    // A legacy filePath is interpreted as its parent directory; a directory path
    // is used directly. New GUI configuration will call the explicit overload.
    public static void Configure(LoggingOptions options, string configDirectory)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(configDirectory);

        var configuredPath = options.FilePath;
        string rootDirectory;
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            rootDirectory = AppContext.BaseDirectory;
        }
        else
        {
            var fullPath = Path.GetFullPath(configuredPath, configDirectory);
            rootDirectory = Path.HasExtension(fullPath)
                ? Path.GetDirectoryName(fullPath) ?? AppContext.BaseDirectory
                : fullPath;
        }

        Configure(rootDirectory, retentionDays: 30, options.ConsoleJson);
    }

    public static void Configure(string? rootDirectory, int retentionDays, bool consoleJson)
    {
        var resolvedRoot = string.IsNullOrWhiteSpace(rootDirectory)
            ? AppContext.BaseDirectory
            : Path.GetFullPath(rootDirectory);

        DailyJsonlLogStore? newStore = null;
        try
        {
            newStore = new DailyJsonlLogStore(resolvedRoot, retentionDays);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Interlocked.Exchange(ref _fileDisabled, 1);
            System.Diagnostics.Debug.WriteLine($"Structured file logging disabled: {ex.Message}");
        }

        lock (Gate)
        {
            var previous = _store;
            _store = newStore;
            _consoleJson = consoleJson;
            Interlocked.Exchange(ref _fileDisabled, newStore is null ? 1 : 0);
            previous?.Dispose();
        }

        if (newStore is not null)
        {
            _ = CleanupRetentionSafelyAsync(newStore);
        }
    }

    public static void Info(string eventName, string message, object? data = null) =>
        Write("Information", eventName, message, data, exception: null);

    public static void Warning(string eventName, string message, object? data = null) =>
        Write("Warning", eventName, message, data, exception: null);

    public static void Error(string eventName, string message, Exception? exception = null, object? data = null) =>
        Write("Error", eventName, message, data, exception);

    private static void Write(
        string level,
        string eventName,
        string message,
        object? data,
        Exception? exception)
    {
        var entry = new LogEntry(
            DateTimeOffset.UtcNow,
            level,
            eventName,
            message,
            data,
            exception is null
                ? null
                : new LogException(exception.GetType().FullName ?? exception.GetType().Name, exception.Message));

        string line;
        try
        {
            line = JsonSerializer.Serialize(entry);
        }
        catch
        {
            // Diagnostics must never break the proxy or alter fail-closed networking behavior.
            return;
        }

        if (_consoleJson)
        {
            System.Diagnostics.Debug.WriteLine(line);
        }

        var store = Volatile.Read(ref _store);
        if (store is null || Volatile.Read(ref _fileDisabled) != 0)
        {
            return;
        }

        try
        {
            store.AppendLine(line);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            Interlocked.Exchange(ref _fileDisabled, 1);
            System.Diagnostics.Debug.WriteLine($"Structured file logging disabled: {ex.Message}");
        }
    }

    private static async Task CleanupRetentionSafelyAsync(DailyJsonlLogStore store)
    {
        try
        {
            await store.CleanupRetentionAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            System.Diagnostics.Debug.WriteLine($"Log retention cleanup failed: {ex.Message}");
        }
    }

    private sealed record LogEntry(
        DateTimeOffset TimestampUtc,
        string Level,
        string Event,
        string Message,
        object? Data,
        LogException? Exception);

    private sealed record LogException(string Type, string Message);
}
