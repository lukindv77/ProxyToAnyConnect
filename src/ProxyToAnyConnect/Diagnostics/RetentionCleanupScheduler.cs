namespace ProxyToAnyConnect.Diagnostics;

internal sealed class RetentionCleanupScheduler : IDisposable
{
    private readonly object _gate = new();
    private readonly Action<DateOnly, CancellationToken> _cleanup;
    private readonly CancellationTokenSource _lifetime = new();

    private Task? _workerTask;
    private DateOnly? _runningDate;
    private DateOnly? _pendingDate;
    private int _activeWorkers;
    private int _maxConcurrentWorkers;
    private int _workerStarts;
    private int _disposed;

    public RetentionCleanupScheduler(Action<DateOnly, CancellationToken> cleanup)
    {
        ArgumentNullException.ThrowIfNull(cleanup);
        _cleanup = cleanup;
    }

    public Task Schedule(DateOnly date)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

            if ((_runningDate is DateOnly runningDate && runningDate >= date) ||
                (_pendingDate is DateOnly pendingDate && pendingDate >= date))
            {
                return _workerTask ?? Task.CompletedTask;
            }

            _pendingDate = date;
            if (_workerTask is { IsCompleted: false })
            {
                return _workerTask;
            }

            _workerTask = Task.Run(RunWorker);
            return _workerTask;
        }
    }

    internal int ActiveWorkers => Volatile.Read(ref _activeWorkers);
    internal int MaxConcurrentWorkers => Volatile.Read(ref _maxConcurrentWorkers);
    internal int WorkerStarts => Volatile.Read(ref _workerStarts);

    private void RunWorker()
    {
        Interlocked.Increment(ref _workerStarts);
        var activeWorkers = Interlocked.Increment(ref _activeWorkers);
        UpdateMaximum(ref _maxConcurrentWorkers, activeWorkers);

        try
        {
            while (true)
            {
                DateOnly date;
                lock (_gate)
                {
                    if (Volatile.Read(ref _disposed) != 0 || _pendingDate is null)
                    {
                        _runningDate = null;
                        _workerTask = null;
                        return;
                    }

                    date = _pendingDate.Value;
                    _pendingDate = null;
                    _runningDate = date;
                }

                try
                {
                    _cleanup(date, _lifetime.Token);
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
                {
                    lock (_gate)
                    {
                        _runningDate = null;
                        _pendingDate = null;
                        _workerTask = null;
                    }

                    return;
                }
                catch (Exception ex)
                {
                    // Retention cleanup is best-effort diagnostics. A filesystem race or
                    // unexpected cleanup failure must never fault the scheduler task or
                    // interfere with proxy/VPN lifecycle state.
                    System.Diagnostics.Debug.WriteLine($"Log retention cleanup failed: {ex.Message}");
                }
                finally
                {
                    lock (_gate)
                    {
                        if (_runningDate == date)
                        {
                            _runningDate = null;
                        }
                    }
                }
            }
        }
        finally
        {
            Interlocked.Decrement(ref _activeWorkers);
        }
    }

    private static void UpdateMaximum(ref int location, int candidate)
    {
        while (true)
        {
            var current = Volatile.Read(ref location);
            if (candidate <= current)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref location, candidate, current) == current)
            {
                return;
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _lifetime.Cancel();
        lock (_gate)
        {
            _pendingDate = null;
        }
    }
}
