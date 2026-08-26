using System.Text.Json;
using ProxyToAnyConnect.Configuration;
using ProxyToAnyConnect.Diagnostics;
using ProxyToAnyConnect.Vpn;

namespace ProxyToAnyConnect.Runtime;

internal sealed class ProxyRuntimeCoordinator : IAsyncDisposable
{
    private readonly Dictionary<string, VpnLeaseManager> _vpnById;
    private readonly Dictionary<string, ProxyInstanceRuntime> _proxyById;
    private readonly SemaphoreSlim _reconfigureGate = new(1, 1);
    private AppOptions _options;
    private int _disposed;

    public ProxyRuntimeCoordinator(AppOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;

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
            await TryStartProxyAsync(proxy, cancellationToken);
        }
    }

    public Task StartProxyAsync(string proxyId, CancellationToken cancellationToken = default) =>
        GetProxy(proxyId).StartAsync(cancellationToken);

    public Task PauseProxyAsync(string proxyId, CancellationToken cancellationToken = default) =>
        GetProxy(proxyId).PauseAsync(cancellationToken);

    public async Task ReconfigureAsync(AppOptions newOptions, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(newOptions);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        newOptions.Validate();

        await _reconfigureGate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

            var oldVpnOptions = _options.VpnConnections.ToDictionary(vpn => vpn.Id, StringComparer.OrdinalIgnoreCase);
            var newVpnOptions = newOptions.VpnConnections.ToDictionary(vpn => vpn.Id, StringComparer.OrdinalIgnoreCase);
            var oldProxyOptions = _options.Proxies.ToDictionary(proxy => proxy.Id, StringComparer.OrdinalIgnoreCase);
            var newProxyOptions = newOptions.Proxies.ToDictionary(proxy => proxy.Id, StringComparer.OrdinalIgnoreCase);

            var changedVpnIds = UnionIds(oldVpnOptions.Keys, newVpnOptions.Keys)
                .Where(id => !oldVpnOptions.TryGetValue(id, out var oldValue) ||
                             !newVpnOptions.TryGetValue(id, out var newValue) ||
                             !ConfigurationEquals(oldValue, newValue))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var affectedProxyIds = UnionIds(oldProxyOptions.Keys, newProxyOptions.Keys)
                .Where(id => !oldProxyOptions.TryGetValue(id, out var oldValue) ||
                             !newProxyOptions.TryGetValue(id, out var newValue) ||
                             !ConfigurationEquals(oldValue, newValue))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var proxy in oldProxyOptions.Values)
            {
                if (changedVpnIds.Contains(proxy.VpnConnectionId))
                {
                    affectedProxyIds.Add(proxy.Id);
                }
            }

            foreach (var proxy in newProxyOptions.Values)
            {
                if (changedVpnIds.Contains(proxy.VpnConnectionId))
                {
                    affectedProxyIds.Add(proxy.Id);
                }
            }

            var previousStates = affectedProxyIds
                .Where(_proxyById.ContainsKey)
                .ToDictionary(
                    id => id,
                    id => _proxyById[id].Snapshot().State,
                    StringComparer.OrdinalIgnoreCase);

            // Stop only proxies whose own settings changed or whose L2TP runtime must be replaced.
            foreach (var proxyId in affectedProxyIds.Where(_proxyById.ContainsKey).ToArray())
            {
                var runtime = _proxyById[proxyId];
                await runtime.DisposeAsync();
                _proxyById.Remove(proxyId);
            }

            // Changed/removed L2TP managers are safe to dispose after all dependent old proxy leases are released.
            foreach (var vpnId in changedVpnIds.Where(_vpnById.ContainsKey).ToArray())
            {
                var runtime = _vpnById[vpnId];
                await runtime.DisposeAsync();
                _vpnById.Remove(vpnId);
            }

            foreach (var vpnId in changedVpnIds)
            {
                if (newVpnOptions.TryGetValue(vpnId, out var vpnOptions))
                {
                    _vpnById[vpnId] = new VpnLeaseManager(vpnOptions);
                }
            }

            foreach (var proxyId in affectedProxyIds)
            {
                if (newProxyOptions.TryGetValue(proxyId, out var proxyOptions))
                {
                    _proxyById[proxyId] = new ProxyInstanceRuntime(
                        proxyOptions,
                        _vpnById[proxyOptions.VpnConnectionId]);
                }
            }

            _options = newOptions;

            // Preserve runtime Pause/Running state for existing proxies. A newly enabled proxy,
            // or a proxy that was in Error and whose configuration changed, gets another start attempt.
            foreach (var proxyId in affectedProxyIds)
            {
                if (!_proxyById.TryGetValue(proxyId, out var runtime) ||
                    !newProxyOptions.TryGetValue(proxyId, out var newProxy))
                {
                    continue;
                }

                oldProxyOptions.TryGetValue(proxyId, out var oldProxy);
                previousStates.TryGetValue(proxyId, out var previousState);

                var shouldStart = oldProxy is null
                    ? newProxy.Enabled
                    : !oldProxy.Enabled && newProxy.Enabled ||
                      previousState is ProxyInstanceState.Running or ProxyInstanceState.Starting ||
                      previousState == ProxyInstanceState.Error && newProxy.Enabled;

                if (oldProxy is not null && oldProxy.Enabled && !newProxy.Enabled)
                {
                    shouldStart = false;
                }

                if (shouldStart)
                {
                    await TryStartProxyAsync(runtime, cancellationToken);
                }
            }

            AppLog.Info(
                "runtime.reconfigured.selective",
                "Runtime configuration was applied with selective proxy/L2TP restart.",
                new
                {
                    ChangedVpnCount = changedVpnIds.Count,
                    AffectedProxyCount = affectedProxyIds.Count,
                    UnaffectedProxyCount = _proxyById.Count - affectedProxyIds.Count(id => _proxyById.ContainsKey(id))
                });
        }
        finally
        {
            _reconfigureGate.Release();
        }
    }

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

    private static async Task TryStartProxyAsync(
        ProxyInstanceRuntime proxy,
        CancellationToken cancellationToken)
    {
        try
        {
            await proxy.StartAsync(cancellationToken);
        }
        catch
        {
            // One failed proxy/L2TP group must not prevent unrelated groups from starting/reloading.
        }
    }

    private static IEnumerable<string> UnionIds(IEnumerable<string> first, IEnumerable<string> second) =>
        first.Concat(second).Distinct(StringComparer.OrdinalIgnoreCase);

    private static bool ConfigurationEquals<T>(T left, T right) =>
        JsonSerializer.Serialize(left) == JsonSerializer.Serialize(right);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _reconfigureGate.WaitAsync();
        try
        {
            foreach (var proxy in _proxyById.Values.ToArray())
            {
                await proxy.DisposeAsync();
            }
            _proxyById.Clear();

            foreach (var vpn in _vpnById.Values.ToArray())
            {
                await vpn.DisposeAsync();
            }
            _vpnById.Clear();
        }
        finally
        {
            _reconfigureGate.Release();
            _reconfigureGate.Dispose();
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
