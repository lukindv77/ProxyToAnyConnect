using System.Net;
using System.Runtime.InteropServices;
using ProxyToAnyConnect.Configuration;

namespace ProxyToAnyConnect.Vpn;

internal sealed class RasConnectionManager : IAsyncDisposable
{
    private readonly L2tpOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();

    private nint _rasConnection;
    private VpnContext? _current;
    private Task? _monitorTask;
    private int _disposed;

    public RasConnectionManager(L2tpOptions options)
    {
        _options = options;
    }

    public VpnContext? Current => Volatile.Read(ref _current);

    public async Task<VpnContext> ConnectAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_current is { IsAlive: true } existing)
            {
                return existing;
            }

            var result = await Task.Run(ConnectCore, cancellationToken);
            _rasConnection = result.Handle;
            _current = result.Context;
            _monitorTask = MonitorAsync(result.Handle, result.Context, _shutdown.Token);
            return result.Context;
        }
        finally
        {
            _gate.Release();
        }
    }

    private ConnectionResult ConnectCore()
    {
        var dialParams = new RasNative.RasDialParams
        {
            DwSize = (uint)Marshal.SizeOf<RasNative.RasDialParams>(),
            SzEntryName = _options.EntryName
        };

        var getParamsResult = RasNative.RasGetEntryDialParamsW(null, dialParams, out _);
        if (getParamsResult != RasNative.ErrorSuccess)
        {
            throw new InvalidOperationException(
                $"Unable to load RAS entry '{_options.EntryName}': {RasNative.DescribeError(getParamsResult)}");
        }

        var dialResult = RasNative.RasDialW(
            0,
            null,
            dialParams,
            0,
            0,
            out var handle);

        if (dialResult != RasNative.ErrorSuccess)
        {
            throw new InvalidOperationException(
                $"Unable to establish RAS entry '{_options.EntryName}': {RasNative.DescribeError(dialResult)}");
        }

        try
        {
            var localAddress = GetAssignedIPv4(handle);
            var context = new VpnContext(_options.EntryName, localAddress);
            return new ConnectionResult(handle, context);
        }
        catch
        {
            _ = RasNative.RasHangUpW(handle);
            throw;
        }
    }

    private static IPAddress GetAssignedIPv4(nint handle)
    {
        var projection = new RasNative.RasPppIp
        {
            DwSize = (uint)Marshal.SizeOf<RasNative.RasPppIp>()
        };

        var size = projection.DwSize;
        var result = RasNative.RasGetProjectionInfoW(
            handle,
            RasNative.RaspPppIp,
            projection,
            ref size);

        if (result != RasNative.ErrorSuccess)
        {
            throw new InvalidOperationException(
                $"Unable to obtain PPP IPv4 projection: {RasNative.DescribeError(result)}");
        }

        if (projection.DwError != RasNative.ErrorSuccess)
        {
            throw new InvalidOperationException(
                $"PPP IPv4 negotiation failed: {RasNative.DescribeError(projection.DwError)}");
        }

        if (!IPAddress.TryParse(projection.SzIpAddress, out var address) ||
            address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            throw new InvalidOperationException(
                $"RAS returned an invalid IPv4 address: '{projection.SzIpAddress}'.");
        }

        return address;
    }

    private async Task MonitorAsync(nint handle, VpnContext context, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_options.MonitorIntervalMilliseconds));

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                IPAddress currentAddress;
                try
                {
                    currentAddress = GetAssignedIPv4(handle);
                }
                catch
                {
                    context.MarkDisconnected();
                    Interlocked.CompareExchange(ref _current, null, context);
                    return;
                }

                if (!currentAddress.Equals(context.LocalIPv4))
                {
                    context.MarkDisconnected();
                    Interlocked.CompareExchange(ref _current, null, context);
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal application shutdown.
        }
    }

    public async Task DisconnectAsync()
    {
        await _gate.WaitAsync();
        try
        {
            var context = Interlocked.Exchange(ref _current, null);
            context?.MarkDisconnected();

            var handle = _rasConnection;
            _rasConnection = 0;
            if (handle != 0)
            {
                _ = RasNative.RasHangUpW(handle);
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

        _shutdown.Cancel();
        await DisconnectAsync();

        if (_monitorTask is not null)
        {
            try
            {
                await _monitorTask;
            }
            catch (OperationCanceledException)
            {
                // Normal application shutdown.
            }
        }

        _shutdown.Dispose();
        _gate.Dispose();
    }

    private sealed record ConnectionResult(nint Handle, VpnContext Context);
}
