using ProxyToAnyConnect.Configuration;
using ProxyToAnyConnect.Diagnostics;
using ProxyToAnyConnect.Network;
using ProxyToAnyConnect.Vpn;

namespace ProxyToAnyConnect.Runtime;

internal sealed class VpnLeaseManager : IAsyncDisposable
{
    private static readonly TimeSpan MaintenanceInterval = TimeSpan.FromMilliseconds(500);

    private readonly L2tpOptions _options;
    private readonly RasConnectionManager _connectionManager;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<string> _consumers = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _lifetime = new();

    private CancellationTokenSource? _maintenanceCancellation;
    private Task? _maintenanceTask;
    private int _activeProxyCount;
    private int _disposed;

    public VpnLeaseManager(L2tpOptions options)
    {
        _options = options;
        Metrics = new L2tpRuntimeMetrics();
        DnsCache = new L2tpDnsCache();
        _connectionManager = new RasConnectionManager(options, Metrics);
    }

    public string Id => _options.Id;
    public string Name => _options.Name;
    public bool Shared => _options.Shared;
    public L2tpOptions Options => _options;
    public RasConnectionManager ConnectionManager => _connectionManager;
    public L2tpRuntimeMetrics Metrics { get; }
    public L2tpDnsCache DnsCache { get; }
    public int ActiveProxyCount => Volatile.Read(ref _activeProxyCount);

    public async Task<VpnLease> AcquireAsync(string proxyId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(proxyId);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

            if (!_options.Shared && _consumers.Count > 0 && !_consumers.Contains(proxyId))
            {
                throw new InvalidOperationException(
                    $"Dedicated L2TP connection '{_options.Name}' is already leased by another proxy.");
            }

            if (!_consumers.Add(proxyId))
            {
                throw new InvalidOperationException(
                    $"Proxy '{proxyId}' already holds L2TP lease '{_options.Name}'.");
            }

            Volatile.Write(ref _activeProxyCount, _consumers.Count);

            try
            {
                await _connectionManager.ConnectAsync(cancellationToken);
            }
            catch
            {
                _consumers.Remove(proxyId);
                Volatile.Write(ref _activeProxyCount, _consumers.Count);
                throw;
            }

            EnsureMaintenanceStartedLocked();

            AppLog.Info(
                "vpn.lease.acquired",
                "Proxy acquired an L2TP runtime lease.",
                new
                {
                    VpnId = _options.Id,
                    VpnName = _options.Name,
                    ProxyId = proxyId,
                    ActiveProxyCount = _consumers.Count,
                    _options.Shared
                });

            return new VpnLease(this, proxyId);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask ReleaseAsync(string proxyId)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        await _gate.WaitAsync();
        try
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            if (!_consumers.Remove(proxyId))
            {
                return;
            }

            var remaining = _consumers.Count;
            Volatile.Write(ref _activeProxyCount, remaining);

            AppLog.Info(
                "vpn.lease.released",
                "Proxy released its L2TP runtime lease.",
                new
                {
                    VpnId = _options.Id,
                    VpnName = _options.Name,
                    ProxyId = proxyId,
                    ActiveProxyCount = remaining
                });

            if (remaining == 0)
            {
                AppLog.Info(
                    "vpn.lease.last_released",
                    "Last active proxy released the L2TP connection; stopping maintenance and disconnecting RAS.",
                    new { VpnId = _options.Id, VpnName = _options.Name });

                await StopMaintenanceLockedAsync();
                await _connectionManager.DisconnectAsync();
                DnsCache.Clear();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private void EnsureMaintenanceStartedLocked()
    {
        if (_maintenanceTask is { IsCompleted: false })
        {
            return;
        }

        _maintenanceCancellation?.Dispose();
        _maintenanceCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _maintenanceTask = MaintainConnectionAsync(_maintenanceCancellation.Token);
    }

    private async Task StopMaintenanceLockedAsync()
    {
        var cancellation = _maintenanceCancellation;
        var task = _maintenanceTask;
        _maintenanceCancellation = null;
        _maintenanceTask = null;

        if (cancellation is null)
        {
            return;
        }

        cancellation.Cancel();
        try
        {
            if (task is not null)
            {
                await task;
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private async Task MaintainConnectionAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(MaintenanceInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                if (ActiveProxyCount == 0)
                {
                    return;
                }

                if (_connectionManager.Current is { IsAlive: true } &&
                    _connectionManager.State == VpnConnectionState.Ready)
                {
                    continue;
                }

                try
                {
                    AppLog.Info(
                        "vpn.maintenance.reconnect_attempt",
                        "Active proxy leases require L2TP; attempting reconnect and full verification.",
                        new
                        {
                            VpnId = _options.Id,
                            VpnName = _options.Name,
                            ActiveProxyCount
                        });

                    await _connectionManager.ConnectAsync(cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();

                    if (ActiveProxyCount == 0)
                    {
                        await _connectionManager.DisconnectAsync();
                        DnsCache.Clear();
                        AppLog.Info(
                            "vpn.maintenance.reconnect_discarded",
                            "Reconnect completed after the last proxy lease was released; L2TP was disconnected again.",
                            new { VpnId = _options.Id, VpnName = _options.Name });
                        return;
                    }

                    AppLog.Info(
                        "vpn.maintenance.reconnected",
                        "L2TP reconnect and verification completed while active proxy leases were present.",
                        new { VpnId = _options.Id, VpnName = _options.Name, ActiveProxyCount });
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException or NotSupportedException)
                {
                    AppLog.Warning(
                        "vpn.maintenance.reconnect_pending",
                        "L2TP is still unavailable; active dependent proxies remain fail-closed.",
                        new
                        {
                            VpnId = _options.Id,
                            VpnName = _options.Name,
                            ActiveProxyCount,
                            Error = ex.Message
                        });
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _lifetime.Cancel();

        await _gate.WaitAsync();
        try
        {
            _consumers.Clear();
            Volatile.Write(ref _activeProxyCount, 0);
            await StopMaintenanceLockedAsync();
            await _connectionManager.DisposeAsync();
            DnsCache.Clear();
            VpnLatestStatusRegistry.Remove(_options.Id);
        }
        finally
        {
            _gate.Release();
        }

        _lifetime.Dispose();

        // Do not race SemaphoreSlim.Dispose() against a VpnLease.DisposeAsync()
        // caller that passed the pre-wait disposed check just before shutdown.
        // AvailableWaitHandle is never used, so there is no OS wait handle to
        // release; the managed gate becomes collectible with this manager.
    }

    internal sealed class VpnLease : IAsyncDisposable
    {
        private readonly VpnLeaseManager _owner;
        private readonly string _proxyId;
        private int _disposed;

        internal VpnLease(VpnLeaseManager owner, string proxyId)
        {
            _owner = owner;
            _proxyId = proxyId;
        }

        public RasConnectionManager ConnectionManager => _owner._connectionManager;
        public L2tpDnsCache DnsCache => _owner.DnsCache;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            await _owner.ReleaseAsync(_proxyId);
        }
    }
}
