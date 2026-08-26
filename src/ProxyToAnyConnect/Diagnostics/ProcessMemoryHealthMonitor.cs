using System.Diagnostics;

namespace ProxyToAnyConnect.Diagnostics;

internal readonly record struct ProcessMemorySnapshot(
    DateTimeOffset TimestampUtc,
    long ManagedHeapBytes,
    long TotalAllocatedBytes,
    long WorkingSetBytes,
    long PrivateBytes,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    int HandleCount,
    int ThreadCount);

internal sealed class ProcessMemoryHealthMonitor : IAsyncDisposable
{
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(5);

    private readonly TimeSpan _interval;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _runTask;
    private ProcessMemorySnapshot _current;
    private int _disposed;

    public ProcessMemoryHealthMonitor(TimeSpan? interval = null)
    {
        _interval = interval ?? DefaultInterval;
        if (_interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval));
        }

        _current = Capture();
        LogSnapshot(_current, "process.memory.startup");
        _runTask = RunAsync(_shutdown.Token);
    }

    // Only the latest scalar snapshot is retained. Historical analysis belongs
    // in the append-only JSONL log, not in process memory.
    public ProcessMemorySnapshot Current => Volatile.Read(ref _current);

    internal static ProcessMemorySnapshot Capture()
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();

        return new ProcessMemorySnapshot(
            DateTimeOffset.UtcNow,
            GC.GetTotalMemory(forceFullCollection: false),
            GC.GetTotalAllocatedBytes(precise: false),
            Environment.WorkingSet,
            process.PrivateMemorySize64,
            GC.CollectionCount(0),
            GC.CollectionCount(1),
            GC.CollectionCount(2),
            process.HandleCount,
            process.Threads.Count);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_interval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                var snapshot = Capture();
                Volatile.Write(ref _current, snapshot);
                LogSnapshot(snapshot, "process.memory.periodic");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // Diagnostics are never allowed to destabilize proxy/VPN operation.
            AppLog.Warning(
                "process.memory.monitor_failed",
                "Process memory health monitoring stopped after an unexpected error.",
                new { Error = ex.Message });
        }
    }

    private static void LogSnapshot(ProcessMemorySnapshot snapshot, string eventName)
    {
        AppLog.Info(
            eventName,
            "Current ProxyToAnyConnect process memory/resource health snapshot.",
            new
            {
                snapshot.ManagedHeapBytes,
                snapshot.TotalAllocatedBytes,
                snapshot.WorkingSetBytes,
                snapshot.PrivateBytes,
                snapshot.Gen0Collections,
                snapshot.Gen1Collections,
                snapshot.Gen2Collections,
                snapshot.HandleCount,
                snapshot.ThreadCount
            });
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _shutdown.Cancel();
        try
        {
            await _runTask;
        }
        finally
        {
            _shutdown.Dispose();
        }
    }
}
