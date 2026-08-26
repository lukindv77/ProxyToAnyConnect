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
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var runtime = Current;
        if (runtime is not null)
        {
            await runtime.StartEnabledAsync(cancellationToken);
        }
    }

    public async Task ApplyOptionsAsync(AppOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        // Build and validate the replacement before touching the currently running runtime.
        var replacement = new ProxyRuntimeCoordinator(options);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

            var previous = Interlocked.Exchange(ref _current, replacement);
            Volatile.Write(ref _configurationError, null);

            if (previous is not null)
            {
                await previous.DisposeAsync();
            }

            await replacement.StartEnabledAsync(cancellationToken);
            AppLog.Info(
                "runtime.reconfigured",
                "Runtime configuration was reloaded from GUI settings.",
                new
                {
                    ProxyCount = options.Proxies.Count,
                    L2tpCount = options.VpnConnections.Count
                });
        }
        catch
        {
            // If replacement startup fails catastrophically, keep the replacement as the current
            // runtime because its per-proxy StartEnabledAsync already isolates normal group errors.
            // Validation failures happen before the swap and therefore leave the old runtime intact.
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task StartProxyAsync(string proxyId, CancellationToken cancellationToken = default) =>
        GetRequiredRuntime().StartProxyAsync(proxyId, cancellationToken);

    public Task PauseProxyAsync(string proxyId, CancellationToken cancellationToken = default) =>
        GetRequiredRuntime().PauseProxyAsync(proxyId, cancellationToken);

    public IReadOnlyList<ProxyRuntimeSnapshot> GetProxySnapshots() =>
        Current?.GetProxySnapshots() ?? [];

    public IReadOnlyList<L2tpRuntimeSnapshot> GetL2tpSnapshots() =>
        Current?.GetL2tpSnapshots() ?? [];

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
