using ProxyToAnyConnect.Configuration;
using ProxyToAnyConnect.Diagnostics;
using ProxyToAnyConnect.Network;
using ProxyToAnyConnect.Proxy;

namespace ProxyToAnyConnect.Runtime;

internal enum ProxyInstanceState
{
    Paused,
    Starting,
    Running,
    Stopping,
    Error
}

internal sealed class ProxyInstanceRuntime : IAsyncDisposable
{
    private readonly ProxyOptions _options;
    private readonly VpnLeaseManager _vpn;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private VpnLeaseManager.VpnLease? _lease;
    private CancellationTokenSource? _runCancellation;
    private Task? _runTask;
    private int _generation;
    private int _state = (int)ProxyInstanceState.Paused;
    private string? _lastError;
    private int _disposed;

    public ProxyInstanceRuntime(ProxyOptions options, VpnLeaseManager vpn)
    {
        _options = options;
        _vpn = vpn;
    }

    public ProxyOptions Options => _options;
    public ProxyRuntimeMetrics Metrics { get; } = new();
    public ProxyInstanceState State => (ProxyInstanceState)Volatile.Read(ref _state);
    public string? LastError => Volatile.Read(ref _lastError);

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (State is ProxyInstanceState.Running or ProxyInstanceState.Starting)
            {
                return;
            }

            SetState(ProxyInstanceState.Starting);
            Volatile.Write(ref _lastError, null);

            VpnLeaseManager.VpnLease? lease = null;
            CancellationTokenSource? runCancellation = null;
            try
            {
                lease = await _vpn.AcquireAsync(_options.Id, cancellationToken);
                runCancellation = new CancellationTokenSource();

                var dnsResolver = new L2tpDnsResolver(
                    _options.DnsTimeoutMilliseconds,
                    lease.DnsCache);
                var socketFactory = new L2tpSocketFactory(lease.ConnectionManager, dnsResolver);
                var proxyServer = new ProxyServer(_options, socketFactory, Metrics, _vpn.Metrics);

                var generation = unchecked(++_generation);
                var runTask = proxyServer.RunAsync(runCancellation.Token);

                _lease = lease;
                _runCancellation = runCancellation;
                _runTask = runTask;

                await proxyServer.WaitUntilListeningAsync(cancellationToken);
                SetState(ProxyInstanceState.Running);

                AppLog.Info(
                    "proxy.running",
                    "Proxy listener entered Running state.",
                    new
                    {
                        ProxyId = _options.Id,
                        ProxyName = _options.Name,
                        Bind = $"{_options.ListenAddress}:{_options.ListenPort}",
                        VpnId = _options.VpnConnectionId
                    });

                _ = ObserveRunCompletionAsync(runTask, generation);
                lease = null;
                runCancellation = null;
            }
            catch (Exception ex)
            {
                runCancellation?.Cancel();
                runCancellation?.Dispose();
                if (lease is not null)
                {
                    await lease.DisposeAsync();
                }

                Volatile.Write(ref _lastError, ex.Message);
                SetState(ProxyInstanceState.Error);
                AppLog.Error(
                    "proxy.start.failed",
                    "Proxy failed to start.",
                    ex,
                    new { ProxyId = _options.Id, ProxyName = _options.Name });
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task PauseAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (State == ProxyInstanceState.Paused && _lease is null)
            {
                return;
            }

            SetState(ProxyInstanceState.Stopping);
            unchecked { _generation++; }

            var cancellation = _runCancellation;
            var runTask = _runTask;
            var lease = _lease;

            _runCancellation = null;
            _runTask = null;
            _lease = null;

            cancellation?.Cancel();
            if (runTask is not null)
            {
                try
                {
                    await runTask.WaitAsync(cancellationToken);
                }
                catch (OperationCanceledException) when (cancellation?.IsCancellationRequested == true)
                {
                }
                catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException)
                {
                    System.Diagnostics.Debug.WriteLine(ex);
                }
            }

            cancellation?.Dispose();
            if (lease is not null)
            {
                await lease.DisposeAsync();
            }

            SetState(ProxyInstanceState.Paused);
            AppLog.Info(
                "proxy.paused",
                "Proxy listener was paused and its L2TP lease released.",
                new { ProxyId = _options.Id, ProxyName = _options.Name });
        }
        finally
        {
            _gate.Release();
        }
    }

    public ProxyRuntimeSnapshot Snapshot()
    {
        var traffic = Metrics.Traffic.Snapshot();
        return new ProxyRuntimeSnapshot(
            _options.Id,
            _options.Name,
            _options.ListenAddress,
            _options.ListenPort,
            _options.VpnConnectionId,
            State,
            LastError,
            traffic.ReceivedBytes,
            traffic.SentBytes);
    }

    private async Task ObserveRunCompletionAsync(Task runTask, int generation)
    {
        Exception? failure = null;
        try
        {
            await runTask;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        await _gate.WaitAsync();
        try
        {
            if (generation != _generation || !ReferenceEquals(_runTask, runTask))
            {
                return;
            }

            var cancellation = _runCancellation;
            var lease = _lease;
            _runCancellation = null;
            _runTask = null;
            _lease = null;

            cancellation?.Dispose();
            if (lease is not null)
            {
                await lease.DisposeAsync();
            }

            if (failure is not null)
            {
                Volatile.Write(ref _lastError, failure.Message);
                SetState(ProxyInstanceState.Error);
                AppLog.Error(
                    "proxy.runtime.failed",
                    "Proxy listener stopped unexpectedly.",
                    failure,
                    new { ProxyId = _options.Id, ProxyName = _options.Name });
            }
            else if (State != ProxyInstanceState.Stopping)
            {
                SetState(ProxyInstanceState.Paused);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private void SetState(ProxyInstanceState state) =>
        Interlocked.Exchange(ref _state, (int)state);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _gate.WaitAsync();
        try
        {
            unchecked { _generation++; }
            _runCancellation?.Cancel();
            if (_runTask is not null)
            {
                try
                {
                    await _runTask;
                }
                catch
                {
                }
            }

            _runCancellation?.Dispose();
            _runCancellation = null;
            _runTask = null;

            if (_lease is not null)
            {
                await _lease.DisposeAsync();
                _lease = null;
            }

            SetState(ProxyInstanceState.Paused);
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }
}

internal readonly record struct ProxyRuntimeSnapshot(
    string Id,
    string Name,
    string ListenAddress,
    int ListenPort,
    string VpnConnectionId,
    ProxyInstanceState State,
    string? LastError,
    long ReceivedBytes,
    long SentBytes);
