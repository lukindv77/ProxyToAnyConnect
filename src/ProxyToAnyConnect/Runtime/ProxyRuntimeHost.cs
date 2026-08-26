using ProxyToAnyConnect.Configuration;
using ProxyToAnyConnect.Diagnostics;

namespace ProxyToAnyConnect.Runtime;

internal sealed class ProxyRuntimeHost : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ProxyRuntimeCoordinator? _current;
    private string? _configurationError;
    private int _disposed;

    public ProxyRuntimeHost(AppOptions initialOptions)
    {
        ArgumentNullException.ThrowIfNull(initialOptions);
        TryBuildInitial(initialOptions);
    }

    public ProxyRuntimeCoordinator? Current => Volatile.Read(ref _current);
    public string? ConfigurationError => Volatile.Read(ref _configurationError);

    public async Task StartEnabledAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            var runtime = Current;
            if (runtime is not null)
            {
                await runtime.StartEnabledAsync(cancellationToken);
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

        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

            var current = Current;
            if (current is null)
            {
                var replacement = new ProxyRuntimeCoordinator(options);
                Volatile.Write(ref _current, replacement);
                Volatile.Write(ref _configurationError, null);
                await replacement.StartEnabledAsync(cancellationToken);
                return;
            }

            await current.ReconfigureAsync(options, cancellationToken);
            Volatile.Write(ref _configurationError, null);
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
            runtime => runtime.StartProxyAsync(proxyId, cancellationToken),
            cancellationToken);

    public Task PauseProxyAsync(string proxyId, CancellationToken cancellationToken = default) =>
        ExecuteRuntimeActionAsync(
            runtime => runtime.PauseProxyAsync(proxyId, cancellationToken),
            cancellationToken);

    public IReadOnlyList<ProxyRuntimeSnapshot> GetProxySnapshots() =>
        Current?.GetProxySnapshots() ?? [];

    public IReadOnlyList<L2tpRuntimeSnapshot> GetL2tpSnapshots() =>
        Current?.GetL2tpSnapshots() ?? [];

    private async Task ExecuteRuntimeActionAsync(
        Func<ProxyRuntimeCoordinator, Task> action,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var runtime = GetRequiredRuntime();
            await action(runtime);
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
        }
    }
}
