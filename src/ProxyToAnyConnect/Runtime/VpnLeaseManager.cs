using System.Runtime.ExceptionServices;
using ProxyToAnyConnect.Configuration;
using ProxyToAnyConnect.Diagnostics;
using ProxyToAnyConnect.Network;
using ProxyToAnyConnect.Vpn;

namespace ProxyToAnyConnect.Runtime;

internal sealed class VpnLeaseManager : IAsyncDisposable
{
    private static readonly TimeSpan DefaultMaintenanceInterval = TimeSpan.FromMilliseconds(500);

    private readonly L2tpOptions _options;
    private readonly IVpnConnectionController _connectionManager;
    private readonly TimeSpan _maintenanceInterval;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<string> _consumers = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _lifetimeToken;

    private CancellationTokenSource? _maintenanceCancellation;
    private Task? _maintenanceTask;
    private int _activeProxyCount;
    private int _disposed;

    public VpnLeaseManager(L2tpOptions options)
        : this(options, null, null)
    {
    }

    internal VpnLeaseManager(
        L2tpOptions options,
        IVpnConnectionController? connectionManager)
        : this(options, connectionManager, null)
    {
    }

    internal VpnLeaseManager(
        L2tpOptions options,
        IVpnConnectionController? connectionManager,
        TimeSpan? maintenanceInterval)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        Metrics = new L2tpRuntimeMetrics();
        DnsCache = new L2tpDnsCache();
        _connectionManager = connectionManager ?? new RasConnectionManager(options, Metrics);
        _maintenanceInterval = maintenanceInterval ?? DefaultMaintenanceInterval;
        if (_maintenanceInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maintenanceInterval),
                "VPN maintenance interval must be positive.");
        }
        _lifetimeToken = _lifetime.Token;
    }

    public string Id => _options.Id;
    public string Name => _options.Name;
    public bool Shared => _options.Shared;
    public L2tpOptions Options => _options;
    public IVpnConnectionController ConnectionManager => _connectionManager;
    public L2tpRuntimeMetrics Metrics { get; }
    public L2tpDnsCache DnsCache { get; }
    public int ActiveProxyCount => Volatile.Read(ref _activeProxyCount);

    public async Task<VpnLease> AcquireAsync(string proxyId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(proxyId);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeToken);
        var operationToken = operationCancellation.Token;

        await _gate.WaitAsync(operationToken);
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
                await _connectionManager.ConnectAsync(operationToken);
                // A controller can complete at the same instant owner shutdown is
                // requested. The lease must not escape after its manager lifetime
                // has ended even if the lower layer returned a context successfully.
                operationToken.ThrowIfCancellationRequested();
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
                "Proxy released an L2TP runtime lease.",
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

                Exception? cleanupFailure = null;
                try
                {
                    await StopMaintenanceLockedAsync();
                }
                catch (Exception ex)
                {
                    CaptureCleanupFailure(ref cleanupFailure, ex, "maintenance-stop");
                }

                try
                {
                    await _connectionManager.DisconnectAsync();
                }
                catch (Exception ex)
                {
                    CaptureCleanupFailure(ref cleanupFailure, ex, "connection-disconnect");
                }
                finally
                {
                    DnsCache.Clear();
                }

                RethrowCleanupFailure(cleanupFailure);
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
        _maintenanceCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeToken);
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

        Exception? cleanupFailure = null;
        try
        {
            try
            {
                cancellation.Cancel();
            }
            catch (Exception ex)
            {
                // Cancellation callbacks are cleanup participants, not owners of the
                // maintenance task. Even a throwing callback must not skip joining the
                // exact task or disposing its CTS.
                CaptureCleanupFailure(ref cleanupFailure, ex, "maintenance-cancel");
            }

            try
            {
                if (task is not null)
                {
                    await task;
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                CaptureCleanupFailure(ref cleanupFailure, ex, "maintenance-task");
            }
        }
        finally
        {
            try
            {
                cancellation.Dispose();
            }
            catch (Exception ex)
            {
                CaptureCleanupFailure(ref cleanupFailure, ex, "maintenance-token");
            }
        }

        RethrowCleanupFailure(cleanupFailure);
    }

    private async Task MaintainConnectionAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_maintenanceInterval);

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

                var cooldownRemaining = _connectionManager.ReconnectCooldownRemainingMilliseconds;
                if (cooldownRemaining > 0)
                {
                    // RasConnectionManager already recorded the cooldown when the
                    // fail-closed event occurred. Wait for that known eligibility
                    // boundary instead of manufacturing rejected ConnectAsync calls,
                    // exceptions and repeated JSONL/status records every maintenance
                    // tick. Last-lease release cancels this delay immediately.
                    await Task.Delay(
                        TimeSpan.FromMilliseconds(cooldownRemaining),
                        cancellationToken);

                    if (ActiveProxyCount == 0)
                    {
                        return;
                    }

                    if (_connectionManager.Current is { IsAlive: true } &&
                        _connectionManager.State == VpnConnectionState.Ready)
                    {
                        continue;
                    }
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
                        try
                        {
                            await _connectionManager.DisconnectAsync();
                        }
                        finally
                        {
                            DnsCache.Clear();
                        }

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

        Exception? cleanupFailure = null;
        try
        {
            try
            {
                _lifetime.Cancel();
            }
            catch (Exception ex)
            {
                // Manager lifetime cancellation is control flow. A throwing callback
                // must not strand leases, maintenance, controller, cache or status
                // ownership during DisposeAsync.
                CaptureCleanupFailure(ref cleanupFailure, ex, "lifetime-cancel");
            }

            await _gate.WaitAsync();
            try
            {
                _consumers.Clear();
                Volatile.Write(ref _activeProxyCount, 0);

                try
                {
                    await StopMaintenanceLockedAsync();
                }
                catch (Exception ex)
                {
                    CaptureCleanupFailure(ref cleanupFailure, ex, "maintenance-stop");
                }

                try
                {
                    await _connectionManager.DisposeAsync();
                }
                catch (Exception ex)
                {
                    CaptureCleanupFailure(ref cleanupFailure, ex, "connection-dispose");
                }
                finally
                {
                    DnsCache.Clear();
                    VpnLatestStatusRegistry.Remove(_options.Id);
                }
            }
            finally
            {
                _gate.Release();
            }
        }
        finally
        {
            try
            {
                _lifetime.Dispose();
            }
            catch (Exception ex)
            {
                CaptureCleanupFailure(ref cleanupFailure, ex, "lifetime-token");
            }
        }

        RethrowCleanupFailure(cleanupFailure);

        // Do not race SemaphoreSlim.Dispose() against a VpnLease.DisposeAsync()
        // caller that passed the pre-wait disposed check just before shutdown.
        // AvailableWaitHandle is never used, so there is no OS wait handle to
        // release; the managed gate becomes collectible with this manager.
    }

    private static void CaptureCleanupFailure(
        ref Exception? primaryFailure,
        Exception failure,
        string phase)
    {
        if (primaryFailure is null)
        {
            primaryFailure = failure;
            return;
        }

        primaryFailure.Data[$"VpnCleanup:{phase}"] =
            $"{failure.GetType().FullName}: {failure.Message}";
    }

    private static void RethrowCleanupFailure(Exception? cleanupFailure)
    {
        if (cleanupFailure is not null)
        {
            ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
        }
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

        public IVpnConnectionController ConnectionManager => _owner._connectionManager;
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
