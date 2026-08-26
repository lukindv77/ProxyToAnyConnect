using ProxyToAnyConnect.Configuration;
using ProxyToAnyConnect.Diagnostics;

namespace ProxyToAnyConnect.Runtime;

internal enum ProxyInstanceState
{
    Paused,
    Starting,
    Running,
    Stopping,
    Error
}

internal sealed class ProxyInstanceRuntime : IAsyncDisposable
{
    private readonly ProxyOptions _options;
    private readonly IProxyInstanceStartFactory _startFactory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private IAsyncDisposable? _lease;
    private CancellationTokenSource? _runCancellation;
    private Task? _runTask;
    private Task? _observerTask;
    private int _generation;
    private int _state = (int)ProxyInstanceState.Paused;
    private string? _lastError;
    private int _disposed;

    public ProxyInstanceRuntime(ProxyOptions options, VpnLeaseManager vpn)
        : this(options, new ProductionProxyInstanceStartFactory(vpn))
    {
    }

    internal ProxyInstanceRuntime(ProxyOptions options, IProxyInstanceStartFactory startFactory)
    {
        _options = options;
        _startFactory = startFactory;
    }

    public ProxyOptions Options => _options;
    public ProxyRuntimeMetrics Metrics { get; } = new();
    public ProxyInstanceState State => (ProxyInstanceState)Volatile.Read(ref _state);
    public string? LastError => Volatile.Read(ref _lastError);

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

            if (State is ProxyInstanceState.Running or ProxyInstanceState.Starting)
            {
                return;
            }

            SetState(ProxyInstanceState.Starting);
            Volatile.Write(ref _lastError, null);

            IAsyncDisposable? lease = null;
            CancellationTokenSource? runCancellation = null;
            Task? runTask = null;
            try
            {
                var attempt = await _startFactory.CreateAsync(
                    _options,
                    Metrics,
                    cancellationToken);
                lease = attempt.Lease;
                runCancellation = new CancellationTokenSource();

                var generation = unchecked(++_generation);
                runTask = attempt.Server.RunAsync(runCancellation.Token);

                // Startup ownership is deliberately unpublished until the listener is
                // confirmed ready. A failed/cancelled readiness wait therefore has one
                // local owner that can deterministically cancel and drain this exact run
                // before its exact VPN lease is released.
                await attempt.Server.WaitUntilListeningAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();

                _lease = lease;
                _runCancellation = runCancellation;
                _runTask = runTask;

                SetState(ProxyInstanceState.Running);
                AppLog.Info(
                    "proxy.running",
                    "Proxy listener entered Running state.",
                    new
                    {
                        ProxyId = _options.Id,
                        ProxyName = _options.Name,
                        Bind = $"{_options.ListenAddress}:{_options.ListenPort}",
                        VpnId = _options.VpnConnectionId
                    });

                _observerTask = ObserveRunCompletionAsync(runTask, generation);
                lease = null;
                runCancellation = null;
                runTask = null;
            }
            catch (Exception ex)
            {
                runCancellation?.Cancel();
                if (runTask is not null)
                {
                    await DrainFailedStartRunAsync(runTask);
                }

                runCancellation?.Dispose();
                if (lease is not null)
                {
                    try
                    {
                        await lease.DisposeAsync();
                    }
                    catch (Exception cleanupError)
                    {
                        AppLog.Warning(
                            "proxy.start.cleanup_failed",
                            "Proxy startup ownership cleanup failed after the start attempt was already rejected.",
                            new
                            {
                                ProxyId = _options.Id,
                                ProxyName = _options.Name,
                                Error = cleanupError.Message
                            });
                    }
                }

                if (ex is OperationCanceledException && cancellationToken.IsCancellationRequested)
                {
                    Volatile.Write(ref _lastError, null);
                    SetState(ProxyInstanceState.Paused);
                    throw;
                }

                Volatile.Write(ref _lastError, ex.Message);
                SetState(ProxyInstanceState.Error);
                AppLog.Error(
                    "proxy.start.failed",
                    "Proxy failed to start.",
                    ex,
                    new { ProxyId = _options.Id, ProxyName = _options.Name });
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task DrainFailedStartRunAsync(Task runTask)
    {
        try
        {
            await runTask;
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or System.Net.Sockets.SocketException)
        {
            // The exact run task has been observed and drained. Its shutdown exception
            // must not replace the readiness/caller-cancellation exception that rejected
            // the startup transaction.
            System.Diagnostics.Debug.WriteLine(ex);
        }
        catch (Exception ex)
        {
            // Likewise observe an unexpected run failure while preserving the original
            // startup rejection as the caller-visible failure.
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    public async Task PauseAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        Task? observerToJoin = null;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

            if (State == ProxyInstanceState.Paused && _lease is null)
            {
                observerToJoin = Interlocked.Exchange(ref _observerTask, null);
            }
            else
            {
                SetState(ProxyInstanceState.Stopping);
                unchecked { _generation++; }

                var cancellation = _runCancellation;
                var runTask = _runTask;
                var lease = _lease;

                _runCancellation = null;
                _runTask = null;
                _lease = null;
                observerToJoin = Interlocked.Exchange(ref _observerTask, null);

                cancellation?.Cancel();
                if (runTask is not null)
                {
                    try
                    {
                        await runTask.WaitAsync(cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellation?.IsCancellationRequested == true)
                    {
                    }
                    catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException)
                    {
                        System.Diagnostics.Debug.WriteLine(ex);
                    }
                }

                cancellation?.Dispose();
                if (lease is not null)
                {
                    await lease.DisposeAsync();
                }

                SetState(ProxyInstanceState.Paused);
                AppLog.Info(
                    "proxy.paused",
                    "Proxy listener was paused and its L2TP lease released.",
                    new { ProxyId = _options.Id, ProxyName = _options.Name });
            }
        }
        finally
        {
            _gate.Release();
        }

        await JoinObserverAsync(observerToJoin);
    }

    public ProxyRuntimeSnapshot Snapshot()
    {
        var traffic = Metrics.Traffic.Snapshot();
        return new ProxyRuntimeSnapshot(
            _options.Id,
            _options.Name,
            _options.ListenAddress,
            _options.ListenPort,
            _options.VpnConnectionId,
            State,
            LastError,
            traffic.ReceivedBytes,
            traffic.SentBytes);
    }

    private async Task ObserveRunCompletionAsync(Task runTask, int generation)
    {
        Exception? failure = null;
        try
        {
            await runTask;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        await _gate.WaitAsync();
        try
        {
            if (generation != _generation || !ReferenceEquals(_runTask, runTask))
            {
                return;
            }

            var cancellation = _runCancellation;
            var lease = _lease;
            _runCancellation = null;
            _runTask = null;
            _lease = null;

            cancellation?.Dispose();
            try
            {
                if (lease is not null)
                {
                    await lease.DisposeAsync();
                }
            }
            catch (Exception cleanupError)
            {
                failure ??= cleanupError;
            }

            if (failure is not null)
            {
                Volatile.Write(ref _lastError, failure.Message);
                SetState(ProxyInstanceState.Error);
                AppLog.Error(
                    "proxy.runtime.failed",
                    "Proxy listener stopped unexpectedly.",
                    failure,
                    new { ProxyId = _options.Id, ProxyName = _options.Name });
            }
            else if (State != ProxyInstanceState.Stopping)
            {
                SetState(ProxyInstanceState.Paused);
            }
        }
        catch (Exception ex)
        {
            // This observer is deliberately no-throw because it can be triggered by
            // an unexpected listener completion while no foreground caller awaits it.
            Volatile.Write(ref _lastError, ex.Message);
            SetState(ProxyInstanceState.Error);
            AppLog.Error(
                "proxy.observer.failed",
                "Proxy runtime completion observer failed.",
                ex,
                new { ProxyId = _options.Id, ProxyName = _options.Name });
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task JoinObserverAsync(Task? observer)
    {
        if (observer is null)
        {
            return;
        }

        try
        {
            await observer;
        }
        catch (Exception ex)
        {
            // ObserveRunCompletionAsync is intended to be no-throw. Keep this guard
            // so a future regression can never surface as an unobserved Task exception.
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    private void SetState(ProxyInstanceState state) =>
        Interlocked.Exchange(ref _state, (int)state);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Task? observerToJoin;
        await _gate.WaitAsync();
        try
        {
            unchecked { _generation++; }
            _runCancellation?.Cancel();
            if (_runTask is not null)
            {
                try
                {
                    await _runTask;
                }
                catch
                {
                }
            }

            _runCancellation?.Dispose();
            _runCancellation = null;
            _runTask = null;
            observerToJoin = Interlocked.Exchange(ref _observerTask, null);

            if (_lease is not null)
            {
                await _lease.DisposeAsync();
                _lease = null;
            }

            SetState(ProxyInstanceState.Paused);
        }
        finally
        {
            _gate.Release();
        }

        await JoinObserverAsync(observerToJoin);

        // SemaphoreSlim only creates an OS wait handle if AvailableWaitHandle is
        // requested (it is not here). Do not race Dispose() against a caller that
        // passed the pre-wait disposed check immediately before disposal; once this
        // runtime becomes unreachable, the managed semaphore is collectible with it.
    }
}

internal readonly record struct ProxyRuntimeSnapshot(
    string Id,
    string Name,
    string ListenAddress,
    int ListenPort,
    string VpnConnectionId,
    ProxyInstanceState State,
    string? LastError,
    long ReceivedBytes,
    long SentBytes);
