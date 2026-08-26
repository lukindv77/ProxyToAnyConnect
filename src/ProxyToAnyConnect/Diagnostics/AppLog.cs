using System.Text.Json;
using ProxyToAnyConnect.Configuration;

namespace ProxyToAnyConnect.Diagnostics;

internal static class AppLog
{
    private static readonly object Gate = new();
    private static string? _filePath;
    private static bool _consoleJson;
    private static int _fileDisabled;

    public static void Configure(LoggingOptions options, string configDirectory)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(configDirectory);

        _consoleJson = options.ConsoleJson;
        _filePath = string.IsNullOrWhiteSpace(options.FilePath)
            ? null
            : Path.GetFullPath(options.FilePath, configDirectory);

        if (_filePath is null)
        {
            return;
        }

        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Interlocked.Exchange(ref _fileDisabled, 1);
            Console.Error.WriteLine($"Structured file logging disabled: {ex.Message}");
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
            Console.WriteLine(line);
        }

        if (_filePath is null || Volatile.Read(ref _fileDisabled) != 0)
        {
            return;
        }

        lock (Gate)
        {
            try
            {
                File.AppendAllText(_filePath, line + Environment.NewLine);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Interlocked.Exchange(ref _fileDisabled, 1);
                Console.Error.WriteLine($"Structured file logging disabled: {ex.Message}");
            }
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
