using ProxyToAnyConnect.Configuration;
using ProxyToAnyConnect.Diagnostics;
using ProxyToAnyConnect.Vpn;

namespace ProxyToAnyConnect.Runtime;

internal sealed class VpnLeaseManager : IAsyncDisposable
{
    private readonly L2tpOptions _options;
    private readonly RasConnectionManager _connectionManager;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<string> _consumers = new(StringComparer.OrdinalIgnoreCase);
    private int _disposed;

    public VpnLeaseManager(L2tpOptions options)
    {
        _options = options;
        _connectionManager = new RasConnectionManager(options);
    }

    public string Id => _options.Id;
    public string Name => _options.Name;
    public bool Shared => _options.Shared;
    public L2tpOptions Options => _options;
    public RasConnectionManager ConnectionManager => _connectionManager;
    public L2tpRuntimeMetrics Metrics { get; } = new();

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

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _gate.WaitAsync();
        try
        {
            _consumers.Clear();
            await _connectionManager.DisposeAsync();
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
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
