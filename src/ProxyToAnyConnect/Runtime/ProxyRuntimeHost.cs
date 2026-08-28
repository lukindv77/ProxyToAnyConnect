using System.Runtime.ExceptionServices;
using ProxyToAnyConnect.Configuration;
using ProxyToAnyConnect.Diagnostics;

namespace ProxyToAnyConnect.Runtime;

internal sealed class ProxyRuntimeHost : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _disposeGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _lifetimeToken;
    private ProxyRuntimeCoordinator? _current;
    private string? _configurationError;
    private int _disposed;
    private int _terminalCleanupCompleted;

    public ProxyRuntimeHost(AppOptions initialOptions)
    {
        ArgumentNullException.ThrowIfNull(initialOptions);
        _lifetimeToken = _lifetime.Token;
        TryBuildInitial(initialOptions);
    }

    public ProxyRuntimeCoordinator? Current =>
        Volatile.Read(ref _disposed) != 0 ? null : Volatile.Read(ref _current);
    public string? ConfigurationError => Volatile.Read(ref _configurationError);

    public async Task StartEnabledAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeToken);
        var operationToken = operationCancellation.Token;

        await _gate.WaitAsync(operationToken);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            var runtime = Current;
            if (runtime is not null)
            {
                await runtime.StartEnabledAsync(operationToken);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ApplyOptionsAsync(AppOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        options.Validate();

        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeToken);
        var operationToken = operationCancellation.Token;

        await _gate.WaitAsync(operationToken);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

            var current = Current;
            if (current is null)
            {
                var replacement = new ProxyRuntimeCoordinator(options);
                Volatile.Write(ref _current, replacement);
                Volatile.Write(ref _configurationError, null);
                await replacement.StartEnabledAsync(operationToken);
                return;
            }

            await current.ReconfigureAsync(options, operationToken);
            Volatile.Write(ref _configurationError, null);
        }
        catch (OperationCanceledException) when (operationToken.IsCancellationRequested)
        {
            // Caller cancellation and host shutdown are lifecycle control flow, not
            // configuration-validation failures. Coordinator pending-start state is
            // retained only when the runtime itself is still alive to reconcile it.
            AppLog.Warning(
                "runtime.reconfigure.cancelled",
                cancellationToken.IsCancellationRequested
                    ? "Selective runtime reconfiguration was cancelled by the caller."
                    : "Selective runtime reconfiguration was cancelled by runtime host shutdown.");
            throw;
        }
        catch (Exception ex)
        {
            // Existing unaffected runtime groups remain alive if selective reconfiguration fails.
            Volatile.Write(ref _configurationError, ex.Message);
            AppLog.Error(
                "runtime.reconfigure.failed",
                "Selective runtime reconfiguration failed.",
                ex);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task StartProxyAsync(string proxyId, CancellationToken cancellationToken = default) =>
        ExecuteRuntimeActionAsync(
            (runtime, operationToken) => runtime.StartProxyAsync(proxyId, operationToken),
            cancellationToken);

    public Task PauseProxyAsync(string proxyId, CancellationToken cancellationToken = default) =>
        ExecuteRuntimeActionAsync(
            (runtime, operationToken) => runtime.PauseProxyAsync(proxyId, operationToken),
            cancellationToken);

    public IReadOnlyList<ProxyRuntimeSnapshot> GetProxySnapshots() =>
        Current?.GetProxySnapshots() ?? [];

    public IReadOnlyList<L2tpRuntimeSnapshot> GetL2tpSnapshots() =>
        Current?.GetL2tpSnapshots() ?? [];

    private async Task ExecuteRuntimeActionAsync(
        Func<ProxyRuntimeCoordinator, CancellationToken, Task> action,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeToken);
        var operationToken = operationCancellation.Token;

        await _gate.WaitAsync(operationToken);
        try
        {
            var runtime = GetRequiredRuntime();
            await action(runtime, operationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private ProxyRuntimeCoordinator GetRequiredRuntime()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return Current ?? throw new InvalidOperationException(
            ConfigurationError is { Length: > 0 } error
                ? $"Runtime configuration is invalid: {error}"
                : "Runtime is not configured.");
    }

    private void TryBuildInitial(AppOptions options)
    {
        try
        {
            _current = new ProxyRuntimeCoordinator(options);
            _configurationError = null;
        }
        catch (Exception ex)
        {
            _current = null;
            _configurationError = ex.Message;
            AppLog.Error(
                "configuration.invalid",
                "Runtime was not created because configuration validation failed.",
                ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _disposeGate.WaitAsync();
        try
        {
            if (Volatile.Read(ref _terminalCleanupCompleted) != 0)
            {
                return;
            }

            var firstDispose = Interlocked.Exchange(ref _disposed, 1) == 0;
            if (!firstDispose)
            {
                var retained = Volatile.Read(ref _current);
                if (retained is null)
                {
                    Volatile.Write(ref _terminalCleanupCompleted, 1);
                    return;
                }

                await retained.DisposeAsync();
                _ = Interlocked.CompareExchange(ref _current, null, retained);
                if (Volatile.Read(ref _current) is null)
                {
                    Volatile.Write(ref _terminalCleanupCompleted, 1);
                }
                return;
            }

            Exception? cleanupFailure = null;
            var gateEntered = false;

            // Wake any foreground Start/Pause/Apply operation before waiting for the
            // host gate it may currently own. A throwing linked-token callback is a
            // cleanup defect, but it must not prevent disposal of the exact coordinator.
            try
            {
                _lifetime.Cancel();
            }
            catch (Exception ex)
            {
                CaptureHostCleanupFailure(ref cleanupFailure, ex, "lifetime-cancel");
            }

            try
            {
                await _gate.WaitAsync();
                gateEntered = true;
                var runtime = Volatile.Read(ref _current);
                if (runtime is not null)
                {
                    try
                    {
                        await runtime.DisposeAsync();
                        _ = Interlocked.CompareExchange(ref _current, null, runtime);
                    }
                    catch (Exception ex)
                    {
                        // Keep the exact coordinator private as cleanup-only ownership.
                        // Public Current is already hidden once _disposed is set, and a
                        // later DisposeAsync call can retry the coordinator's residual VPNs.
                        CaptureHostCleanupFailure(ref cleanupFailure, ex, "coordinator-dispose");
                    }
                }
            }
            catch (Exception ex)
            {
                CaptureHostCleanupFailure(ref cleanupFailure, ex, "dispose-body");
            }
            finally
            {
                if (gateEntered)
                {
                    _gate.Release();
                }

                try
                {
                    _gate.Dispose();
                }
                catch (Exception ex)
                {
                    CaptureHostCleanupFailure(ref cleanupFailure, ex, "gate-token");
                }

                try
                {
                    _lifetime.Dispose();
                }
                catch (Exception ex)
                {
                    CaptureHostCleanupFailure(ref cleanupFailure, ex, "lifetime-token");
                }
            }

            if (Volatile.Read(ref _current) is null)
            {
                Volatile.Write(ref _terminalCleanupCompleted, 1);
            }

            RethrowHostCleanupFailure(cleanupFailure);
        }
        finally
        {
            _disposeGate.Release();
        }
    }
    private static void CaptureHostCleanupFailure(
        ref Exception? primaryFailure,
        Exception failure,
        string phase)
    {
        if (primaryFailure is null)
        {
            primaryFailure = failure;
            return;
        }

        primaryFailure.Data[$"RuntimeHostCleanup:{phase}"] =
            $"{failure.GetType().FullName}: {failure.Message}";
    }

    private static void RethrowHostCleanupFailure(Exception? cleanupFailure)
    {
        if (cleanupFailure is not null)
        {
            ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
        }
    }
}
