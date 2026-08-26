using ProxyToAnyConnect.Configuration;
using ProxyToAnyConnect.Diagnostics;
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

    private Task? _maintenanceTask;
    private int _disposed;

    public VpnLeaseManager(L2tpOptions options)
    {
        _options = options;
        Metrics = new L2tpRuntimeMetrics();
        _connectionManager = new RasConnectionManager(options, Metrics);
    }

    public string Id => _options.Id;
    public string Name => _options.Name;
    public bool Shared => _options.Shared;
    public L2tpOptions Options => _options;
    public RasConnectionManager ConnectionManager => _connectionManager;
    public L2tpRuntimeMetrics Metrics { get; }

    public int ActiveProxyCount
    {
        get
        {
            lock (_consumers)
            {
                return _consumers.Count;
            }
        }
    }

    public async Task<VpnLease> AcquireAsync(string proxyId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(proxyId);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        await _gate.WaitAsync(cancellationToken);
        try
        {
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

            try
            {
                await _connectionManager.ConnectAsync(cancellationToken);
            }
            catch
            {
                _consumers.Remove(proxyId);
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
            if (!_consumers.Remove(proxyId))
            {
                return;
            }

            var remaining = _consumers.Count;
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
                    "Last active proxy released the L2TP connection; disconnecting RAS.",
                    new { VpnId = _options.Id, VpnName = _options.Name });
                await _connectionManager.DisconnectAsync();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private void EnsureMaintenanceStartedLocked()
    {
        if (_maintenanceTask is null)
        {
            _maintenanceTask = MaintainConnectionAsync(_lifetime.Token);
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
                    continue;
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

                    // A proxy may have been paused while RasDial/verification was running.
                    // Never leave a freshly reconnected L2TP alive with zero consumers.
                    if (ActiveProxyCount == 0)
                    {
                        await _connectionManager.DisconnectAsync();
                        AppLog.Info(
                            "vpn.maintenance.reconnect_discarded",
                            "Reconnect completed after the last proxy lease was released; L2TP was disconnected again.",
                            new { VpnId = _options.Id, VpnName = _options.Name });
                        continue;
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
            await _connectionManager.DisposeAsync();
        }
        finally
        {
            _gate.Release();
        }

        if (_maintenanceTask is not null)
        {
            try
            {
                await _maintenanceTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        _lifetime.Dispose();
        _gate.Dispose();
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
