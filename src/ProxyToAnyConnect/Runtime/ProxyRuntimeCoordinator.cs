using ProxyToAnyConnect.Configuration;
using ProxyToAnyConnect.Vpn;

namespace ProxyToAnyConnect.Runtime;

internal sealed class ProxyRuntimeCoordinator : IAsyncDisposable
{
    private readonly Dictionary<string, VpnLeaseManager> _vpnById;
    private readonly Dictionary<string, ProxyInstanceRuntime> _proxyById;
    private int _disposed;

    public ProxyRuntimeCoordinator(AppOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        _vpnById = options.VpnConnections.ToDictionary(
            vpn => vpn.Id,
            vpn => new VpnLeaseManager(vpn),
            StringComparer.OrdinalIgnoreCase);

        _proxyById = options.Proxies.ToDictionary(
            proxy => proxy.Id,
            proxy => new ProxyInstanceRuntime(proxy, _vpnById[proxy.VpnConnectionId]),
            StringComparer.OrdinalIgnoreCase);
    }

    public async Task StartEnabledAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        foreach (var proxy in _proxyById.Values.Where(item => item.Options.Enabled))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await proxy.StartAsync(cancellationToken);
            }
            catch
            {
                // One failed proxy/L2TP group must not prevent unrelated groups from starting.
            }
        }
    }

    public Task StartProxyAsync(string proxyId, CancellationToken cancellationToken = default) =>
        GetProxy(proxyId).StartAsync(cancellationToken);

    public Task PauseProxyAsync(string proxyId, CancellationToken cancellationToken = default) =>
        GetProxy(proxyId).PauseAsync(cancellationToken);

    public IReadOnlyList<ProxyRuntimeSnapshot> GetProxySnapshots() =>
        _proxyById.Values
            .Select(proxy => proxy.Snapshot())
            .OrderBy(snapshot => snapshot.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

    public IReadOnlyList<L2tpRuntimeSnapshot> GetL2tpSnapshots() =>
        _vpnById.Values
            .Select(vpn =>
            {
                var manager = vpn.ConnectionManager;
                var context = manager.Current;
                var metrics = vpn.Metrics.Snapshot();
                return new L2tpRuntimeSnapshot(
                    vpn.Id,
                    vpn.Name,
                    vpn.Options.Mode,
                    vpn.Shared,
                    manager.State,
                    context?.LocalIPv4.ToString(),
                    context?.InterfaceIndex,
                    vpn.ActiveProxyCount,
                    metrics.ReceivedBytes,
                    metrics.SentBytes,
                    metrics.AveragePingMilliseconds,
                    metrics.PingSampleCount);
            })
            .OrderBy(snapshot => snapshot.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

    private ProxyInstanceRuntime GetProxy(string proxyId)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!_proxyById.TryGetValue(proxyId, out var proxy))
        {
            throw new KeyNotFoundException($"Proxy '{proxyId}' was not found.");
        }

        return proxy;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var proxy in _proxyById.Values)
        {
            await proxy.DisposeAsync();
        }

        foreach (var vpn in _vpnById.Values)
        {
            await vpn.DisposeAsync();
        }
    }
}

internal readonly record struct L2tpRuntimeSnapshot(
    string Id,
    string Name,
    L2tpConnectionMode Mode,
    bool Shared,
    VpnConnectionState State,
    string? LocalIPv4,
    int? InterfaceIndex,
    int ActiveProxyCount,
    long ReceivedBytes,
    long SentBytes,
    double? AveragePingMilliseconds,
    int PingSampleCount);
