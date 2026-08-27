using System.Runtime.ExceptionServices;
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
                var cleanupDefect = await CleanupRejectedStartOwnershipAsync(
                    ex,
                    runCancellation,
                    runTask,
                    lease);

                if (cleanupDefect)
                {
                    AppLog.Warning(
                        "proxy.start.cleanup_failed",
                        "Proxy startup was already rejected and one or more secondary ownership cleanup steps also failed.",
                        new
                        {
                            ProxyId = _options.Id,
                            ProxyName = _options.Name,
                            PrimaryError = ex.Message
                        });
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

    private static async Task<bool> CleanupRejectedStartOwnershipAsync(
        Exception primaryFailure,
        CancellationTokenSource? runCancellation,
        Task? runTask,
        IAsyncDisposable? lease)
    {
        var cleanupDefect = false;

        void Attach(Exception failure, string phase)
        {
            cleanupDefect = true;
            primaryFailure.Data[$"ProxyStartCleanup:{phase}"] =
                $"{failure.GetType().FullName}: {failure.Message}";
        }

        if (runCancellation is not null)
        {
            try
            {
                runCancellation.Cancel();
            }
            catch (Exception ex)
            {
                // Cancellation callbacks are secondary cleanup work. Even when one
                // throws, CancellationTokenSource has transitioned to cancelled and
                // the exact run task must still be drained before its lease release.
                Attach(ex, "run-cancel");
            }
        }

        if (runTask is not null)
        {
            try
            {
                await runTask;
            }
            catch (OperationCanceledException) when (runCancellation?.IsCancellationRequested == true)
            {
            }
            catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException)
            {
                System.Diagnostics.Debug.WriteLine(ex);
            }
            catch (Exception ex)
            {
                Attach(ex, "run-drain");
            }
        }

        if (runCancellation is not null)
        {
            try
            {
                runCancellation.Dispose();
            }
            catch (Exception ex)
            {
                Attach(ex, "run-token");
            }
        }

        if (lease is not null)
        {
            try
            {
                await lease.DisposeAsync();
            }
            catch (Exception ex)
            {
                Attach(ex, "vpn-lease");
            }
        }

        return cleanupDefect;
    }

    public async Task PauseAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        Task? observerToJoin = null;
        Exception? cleanupFailure = null;
        var performedShutdown = false;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

            if (State == ProxyInstanceState.Paused &&
                _lease is null &&
                _runTask is null &&
                _runCancellation is null)
            {
                observerToJoin = Interlocked.Exchange(ref _observerTask, null);
            }
            else
            {
                performedShutdown = true;
                SetState(ProxyInstanceState.Stopping);

                var stopResult = await StopPublishedOwnershipLockedAsync();
                observerToJoin = stopResult.Observer;
                cleanupFailure = stopResult.CleanupFailure;

                if (cleanupFailure is null)
                {
                    Volatile.Write(ref _lastError, null);
                    SetState(ProxyInstanceState.Paused);
                    AppLog.Info(
                        "proxy.paused",
                        "Proxy listener was paused and its L2TP lease released after exact run drain.",
                        new { ProxyId = _options.Id, ProxyName = _options.Name });
                }
                else
                {
                    Volatile.Write(ref _lastError, cleanupFailure.Message);
                    SetState(ProxyInstanceState.Error);
                    AppLog.Error(
                        "proxy.pause.cleanup_failed",
                        "Proxy listener stopped, but one or more shutdown ownership releases failed.",
                        cleanupFailure,
                        new { ProxyId = _options.Id, ProxyName = _options.Name });
                }
            }
        }
        finally
        {
            _gate.Release();
        }

        await JoinObserverAsync(observerToJoin);

        if (performedShutdown && cancellationToken.IsCancellationRequested)
        {
            if (cleanupFailure is not null)
            {
                throw new OperationCanceledException(
                    "Proxy pause was cancelled after transactional shutdown completed with a cleanup defect.",
                    cleanupFailure,
                    cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
        }

        RethrowStopFailure(cleanupFailure);
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

    private async Task<StopOwnershipResult> StopPublishedOwnershipLockedAsync()
    {
        unchecked { _generation++; }

        var cancellation = _runCancellation;
        var runTask = _runTask;
        var lease = _lease;
        _runCancellation = null;
        _runTask = null;
        _lease = null;
        var observer = Interlocked.Exchange(ref _observerTask, null);
        Exception? cleanupFailure = null;

        if (cancellation is not null)
        {
            try
            {
                cancellation.Cancel();
            }
            catch (Exception ex)
            {
                CaptureStopFailure(ref cleanupFailure, ex, "run-cancel");
            }
        }

        if (runTask is not null)
        {
            try
            {
                // Once shutdown owns this exact generation, caller cancellation must
                // not skip the drain and release its VPN lease underneath live proxy
                // sessions. Caller cancellation is restored only after this task and
                // all independent ownership releases complete.
                await runTask;
            }
            catch (OperationCanceledException) when (cancellation?.IsCancellationRequested == true)
            {
            }
            catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException)
            {
                System.Diagnostics.Debug.WriteLine(ex);
            }
            catch (Exception ex)
            {
                CaptureStopFailure(ref cleanupFailure, ex, "run-drain");
            }
        }

        if (cancellation is not null)
        {
            try
            {
                cancellation.Dispose();
            }
            catch (Exception ex)
            {
                CaptureStopFailure(ref cleanupFailure, ex, "run-token");
            }
        }

        if (lease is not null)
        {
            try
            {
                await lease.DisposeAsync();
            }
            catch (Exception ex)
            {
                CaptureStopFailure(ref cleanupFailure, ex, "vpn-lease");
            }
        }

        return new StopOwnershipResult(observer, cleanupFailure);
    }

    private static void CaptureStopFailure(
        ref Exception? primaryFailure,
        Exception failure,
        string phase)
    {
        if (primaryFailure is null)
        {
            primaryFailure = failure;
            return;
        }

        primaryFailure.Data[$"ProxyStop:{phase}"] =
            $"{failure.GetType().FullName}: {failure.Message}";
    }

    private static void RethrowStopFailure(Exception? cleanupFailure)
    {
        if (cleanupFailure is not null)
        {
            ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
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
        Exception? cleanupFailure;
        await _gate.WaitAsync();
        try
        {
            var stopResult = await StopPublishedOwnershipLockedAsync();
            observerToJoin = stopResult.Observer;
            cleanupFailure = stopResult.CleanupFailure;

            if (cleanupFailure is null)
            {
                Volatile.Write(ref _lastError, null);
                SetState(ProxyInstanceState.Paused);
            }
            else
            {
                Volatile.Write(ref _lastError, cleanupFailure.Message);
                SetState(ProxyInstanceState.Error);
            }
        }
        finally
        {
            _gate.Release();
        }

        await JoinObserverAsync(observerToJoin);
        RethrowStopFailure(cleanupFailure);

        // SemaphoreSlim only creates an OS wait handle if AvailableWaitHandle is
        // requested (it is not here). Do not race Dispose() against a caller that
        // passed the pre-wait disposed check immediately before disposal; once this
        // runtime becomes unreachable, the managed semaphore is collectible with it.
    }

    private readonly record struct StopOwnershipResult(
        Task? Observer,
        Exception? CleanupFailure);
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
