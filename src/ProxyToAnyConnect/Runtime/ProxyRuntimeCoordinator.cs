using System.Text.Json;
using ProxyToAnyConnect.Configuration;
using ProxyToAnyConnect.Diagnostics;
using ProxyToAnyConnect.Vpn;

namespace ProxyToAnyConnect.Runtime;

internal sealed class ProxyRuntimeCoordinator : IAsyncDisposable
{
    private readonly Dictionary<string, VpnLeaseManager> _vpnById;
    private readonly Dictionary<string, ProxyInstanceRuntime> _proxyById;
    private readonly HashSet<string> _pendingStartProxyIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _reconfigureGate = new(1, 1);
    private readonly object _collectionGate = new();
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

        ProxyInstanceRuntime[] proxies;
        lock (_collectionGate)
        {
            proxies = _proxyById.Values.Where(item => item.Options.Enabled).ToArray();
        }

        foreach (var proxy in proxies)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await TryStartProxyAsync(proxy, cancellationToken);
        }
    }

    public async Task StartProxyAsync(string proxyId, CancellationToken cancellationToken = default)
    {
        var proxy = GetProxy(proxyId);
        await proxy.StartAsync(cancellationToken);

        lock (_collectionGate)
        {
            if (_proxyById.TryGetValue(proxyId, out var current) && ReferenceEquals(current, proxy))
            {
                _pendingStartProxyIds.Remove(proxyId);
            }
        }
    }

    public async Task PauseProxyAsync(string proxyId, CancellationToken cancellationToken = default)
    {
        var proxy = GetProxy(proxyId);
        await proxy.PauseAsync(cancellationToken);

        lock (_collectionGate)
        {
            _pendingStartProxyIds.Remove(proxyId);
        }
    }

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

            lock (_collectionGate)
            {
                _pendingStartProxyIds.RemoveWhere(id =>
                    !newProxyOptions.TryGetValue(id, out var proxy) || !proxy.Enabled);
            }

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

            Dictionary<string, ProxyInstanceState> previousStates;
            ProxyInstanceRuntime[] proxiesToDispose;
            lock (_collectionGate)
            {
                previousStates = affectedProxyIds
                    .Where(_proxyById.ContainsKey)
                    .ToDictionary(
                        id => id,
                        id => _proxyById[id].Snapshot().State,
                        StringComparer.OrdinalIgnoreCase);

                proxiesToDispose = affectedProxyIds
                    .Where(_proxyById.ContainsKey)
                    .Select(id => _proxyById[id])
                    .ToArray();

                foreach (var proxyId in affectedProxyIds)
                {
                    _proxyById.Remove(proxyId);
                }
            }

            // Stop only proxies whose own settings changed or whose L2TP runtime must be replaced.
            foreach (var runtime in proxiesToDispose)
            {
                await runtime.DisposeAsync();
            }

            VpnLeaseManager[] vpnsToDispose;
            lock (_collectionGate)
            {
                vpnsToDispose = changedVpnIds
                    .Where(_vpnById.ContainsKey)
                    .Select(id => _vpnById[id])
                    .ToArray();

                foreach (var vpnId in changedVpnIds)
                {
                    _vpnById.Remove(vpnId);
                }
            }

            // Changed/removed L2TP managers are disposed after all dependent old proxy leases are released.
            foreach (var runtime in vpnsToDispose)
            {
                await runtime.DisposeAsync();
            }

            lock (_collectionGate)
            {
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
            }

            // Preserve runtime Pause/Running state for existing proxies. A newly enabled proxy,
            // or a proxy that was in Error and whose configuration changed, gets another start attempt.
            // Failed/cancelled desired starts remain pending so applying the same configuration again
            // reconciles runtime state even when there is no longer a configuration diff.
            var startCandidateIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            lock (_collectionGate)
            {
                foreach (var proxyId in _pendingStartProxyIds)
                {
                    if (_proxyById.ContainsKey(proxyId) &&
                        newProxyOptions.TryGetValue(proxyId, out var pendingProxy) &&
                        pendingProxy.Enabled)
                    {
                        startCandidateIds.Add(proxyId);
                    }
                }
            }

            foreach (var proxyId in affectedProxyIds)
            {
                if (!newProxyOptions.TryGetValue(proxyId, out var newProxy))
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
                    startCandidateIds.Add(proxyId);
                }
            }

            lock (_collectionGate)
            {
                foreach (var proxyId in startCandidateIds)
                {
                    _pendingStartProxyIds.Add(proxyId);
                }
            }

            foreach (var proxyId in startCandidateIds)
            {
                cancellationToken.ThrowIfCancellationRequested();

                ProxyInstanceRuntime? runtime;
                lock (_collectionGate)
                {
                    _proxyById.TryGetValue(proxyId, out runtime);
                }

                if (runtime is null ||
                    !newProxyOptions.TryGetValue(proxyId, out var newProxy) ||
                    !newProxy.Enabled)
                {
                    lock (_collectionGate)
                    {
                        _pendingStartProxyIds.Remove(proxyId);
                    }

                    continue;
                }

                if (await TryStartProxyAsync(runtime, cancellationToken))
                {
                    lock (_collectionGate)
                    {
                        if (_proxyById.TryGetValue(proxyId, out var current) &&
                            ReferenceEquals(current, runtime))
                        {
                            _pendingStartProxyIds.Remove(proxyId);
                        }
                    }
                }
            }

            int totalProxyCount;
            int affectedExistingProxyCount;
            int pendingStartCount;
            lock (_collectionGate)
            {
                totalProxyCount = _proxyById.Count;
                affectedExistingProxyCount = affectedProxyIds.Count(_proxyById.ContainsKey);
                pendingStartCount = _pendingStartProxyIds.Count;
            }

            AppLog.Info(
                "runtime.reconfigured.selective",
                "Runtime configuration was applied with selective proxy/L2TP restart.",
                new
                {
                    ChangedVpnCount = changedVpnIds.Count,
                    AffectedProxyCount = affectedProxyIds.Count,
                    UnaffectedProxyCount = Math.Max(0, totalProxyCount - affectedExistingProxyCount),
                    PendingStartCount = pendingStartCount
                });

            if (pendingStartCount > 0)
            {
                AppLog.Warning(
                    "runtime.reconfigure.start_pending",
                    "Configuration is applied, but one or more desired proxy starts remain pending reconciliation.",
                    new { PendingStartCount = pendingStartCount });
            }
        }
        finally
        {
            _reconfigureGate.Release();
        }
    }

    public IReadOnlyList<ProxyRuntimeSnapshot> GetProxySnapshots()
    {
        ProxyInstanceRuntime[] proxies;
        lock (_collectionGate)
        {
            proxies = _proxyById.Values.ToArray();
        }

        return proxies
            .Select(proxy => proxy.Snapshot())
            .OrderBy(snapshot => snapshot.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<L2tpRuntimeSnapshot> GetL2tpSnapshots()
    {
        VpnLeaseManager[] vpns;
        lock (_collectionGate)
        {
            vpns = _vpnById.Values.ToArray();
        }

        return vpns
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
    }

    private ProxyInstanceRuntime GetProxy(string proxyId)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        lock (_collectionGate)
        {
            if (!_proxyById.TryGetValue(proxyId, out var proxy))
            {
                throw new KeyNotFoundException($"Proxy '{proxyId}' was not found.");
            }

            return proxy;
        }
    }

    private static async Task<bool> TryStartProxyAsync(
        ProxyInstanceRuntime proxy,
        CancellationToken cancellationToken)
    {
        try
        {
            await proxy.StartAsync(cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Caller cancellation is control flow, not an isolated proxy/L2TP failure.
            // It must stop the foreground operation and remain visible to the caller.
            throw;
        }
        catch
        {
            // One failed proxy/L2TP group must not prevent unrelated groups from starting/reloading.
            return false;
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
            ProxyInstanceRuntime[] proxies;
            VpnLeaseManager[] vpns;
            lock (_collectionGate)
            {
                proxies = _proxyById.Values.ToArray();
                vpns = _vpnById.Values.ToArray();
                _proxyById.Clear();
                _vpnById.Clear();
                _pendingStartProxyIds.Clear();
            }

            foreach (var proxy in proxies)
            {
                await proxy.DisposeAsync();
            }

            foreach (var vpn in vpns)
            {
                await vpn.DisposeAsync();
            }
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
