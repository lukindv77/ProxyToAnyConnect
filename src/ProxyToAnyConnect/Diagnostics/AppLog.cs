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

    public static void Configure(LoggingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Configure(options.Directory, options.RetentionDays, options.ConsoleJson);
    }

    public static void Configure(string? rootDirectory, int retentionDays, bool consoleJson)
    {
        DailyJsonlLogStore? newStore;
        try
        {
            var resolvedRoot = string.IsNullOrWhiteSpace(rootDirectory)
                ? AppContext.BaseDirectory
                : Path.GetFullPath(rootDirectory, AppContext.BaseDirectory);
            newStore = new DailyJsonlLogStore(resolvedRoot, retentionDays);
        }
        catch (Exception ex) when (
            ex is IOException or
                UnauthorizedAccessException or
                ArgumentException or
                NotSupportedException or
                System.Security.SecurityException)
        {
            // Configuration is deliberately loaded for editing before full runtime
            // validation. A malformed or inaccessible log root must therefore not
            // terminate startup before the repair UI is available. If logging was
            // already healthy, keep the previous store transactionally rather than
            // disabling diagnostics because a replacement could not be created.
            lock (Gate)
            {
                _consoleJson = consoleJson;
                if (_store is null)
                {
                    Interlocked.Exchange(ref _fileDisabled, 1);
                }
            }

            System.Diagnostics.Debug.WriteLine(
                $"Structured file logging configuration was rejected: {ex.Message}");
            return;
        }

        DailyJsonlLogStore? previous;
        lock (Gate)
        {
            previous = _store;
            _store = newStore;
            _consoleJson = consoleJson;
            Interlocked.Exchange(ref _fileDisabled, 0);
        }

        previous?.Dispose();
        _ = CleanupRetentionSafelyAsync(newStore);
    }

    public static void Shutdown()
    {
        DailyJsonlLogStore? previous;
        lock (Gate)
        {
            previous = _store;
            _store = null;
            _consoleJson = false;
            Interlocked.Exchange(ref _fileDisabled, 1);
        }

        previous?.Dispose();
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
        try
        {
            VpnLatestStatusRegistry.UpdateFromLog(eventName, message, data, exception);
        }
        catch
        {
            // Latest-status projection is optional diagnostics and must never
            // interfere with the structured log or networking state machine.
        }

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
            // Diagnostics must never break proxy routing or VPN fail-closed behavior.
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
            // A writer may have captured the previous store immediately before a
            // concurrent Configure() swaps and disposes it. That stale failure must
            // not disable the newly configured store globally.
            if (ShouldDisableFileLoggingAfterFailure(store, Volatile.Read(ref _store)))
            {
                Interlocked.Exchange(ref _fileDisabled, 1);
            }

            System.Diagnostics.Debug.WriteLine($"Structured file logging disabled: {ex.Message}");
        }
    }

    internal static bool ShouldDisableFileLoggingAfterFailure(
        DailyJsonlLogStore attemptedStore,
        DailyJsonlLogStore? currentStore) =>
        ReferenceEquals(attemptedStore, currentStore);

    private static async Task CleanupRetentionSafelyAsync(DailyJsonlLogStore store)
    {
        try
        {
            await store.CleanupRetentionAsync();
        }
        catch (Exception ex) when (
            ex is IOException or
                UnauthorizedAccessException or
                OperationCanceledException or
                ObjectDisposedException)
        {
            // A concurrent reconfiguration/shutdown may dispose this exact store
            // before its best-effort startup cleanup begins.
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
