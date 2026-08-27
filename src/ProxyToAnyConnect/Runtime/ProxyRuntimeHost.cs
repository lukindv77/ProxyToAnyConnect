using ProxyToAnyConnect.Configuration;
using ProxyToAnyConnect.Diagnostics;

namespace ProxyToAnyConnect.Runtime;

internal sealed class ProxyRuntimeHost : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _lifetimeToken;
    private ProxyRuntimeCoordinator? _current;
    private string? _configurationError;
    private int _disposed;

    public ProxyRuntimeHost(AppOptions initialOptions)
    {
        ArgumentNullException.ThrowIfNull(initialOptions);
        _lifetimeToken = _lifetime.Token;
        TryBuildInitial(initialOptions);
    }

    public ProxyRuntimeCoordinator? Current => Volatile.Read(ref _current);
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
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // Wake any foreground Start/Pause/Apply operation before waiting for the
        // host gate it may currently own. Otherwise host shutdown could never reach
        // the coordinator's own cancellation/drain path.
        _lifetime.Cancel();

        await _gate.WaitAsync();
        try
        {
            var runtime = Interlocked.Exchange(ref _current, null);
            if (runtime is not null)
            {
                await runtime.DisposeAsync();
            }
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
            _lifetime.Dispose();
        }
    }
}
