using System.Net;
using System.Runtime.InteropServices;
using ProxyToAnyConnect.Configuration;
using ProxyToAnyConnect.Diagnostics;

namespace ProxyToAnyConnect.Vpn;

internal sealed class RasConnectionManager : IAsyncDisposable
{
    private readonly L2tpOptions _options;
    private readonly WindowsVpnProfileInspector _profileInspector;
    private readonly WindowsDefaultRouteInspector _routeInspector;
    private readonly VpnConnectivityVerifier _connectivityVerifier;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();

    private nint _rasConnection;
    private VpnContext? _current;
    private VpnVerificationResult? _lastVerification;
    private Task? _monitorTask;
    private int _state = (int)VpnConnectionState.Disconnected;
    private int _disposed;

    public RasConnectionManager(L2tpOptions options)
    {
        _options = options;
        _profileInspector = new WindowsVpnProfileInspector();
        _routeInspector = new WindowsDefaultRouteInspector();
        _connectivityVerifier = new VpnConnectivityVerifier(options.Verification);
    }

    public VpnContext? Current => Volatile.Read(ref _current);
    public VpnVerificationResult? LastVerification => Volatile.Read(ref _lastVerification);
    public VpnConnectionState State => (VpnConnectionState)Volatile.Read(ref _state);

    public async Task<VpnContext> ConnectAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_current is { IsAlive: true } existing && State == VpnConnectionState.Ready)
            {
                return existing;
            }

            SetState(VpnConnectionState.Dialing);
            ConnectionResult? result = null;

            try
            {
                var profile = await _profileInspector.InspectAsync(_options.EntryName, cancellationToken);
                WindowsVpnProfileInspector.ValidateForProxy(profile);
                AppLog.Info(
                    "vpn.profile.validated",
                    "Windows VPN profile passed L2TP split-tunnel validation.",
                    new { profile.Name, profile.TunnelType, profile.SplitTunneling, profile.AllUserConnection });

                var routesBefore = await _routeInspector.CaptureIPv4Async(cancellationToken);

                result = await Task.Run(ConnectCore, cancellationToken);
                SetState(VpnConnectionState.Verifying);

                var routesAfter = await _routeInspector.CaptureIPv4Async(cancellationToken);
                WindowsDefaultRouteInspector.EnsureUnchanged(routesBefore, routesAfter);
                AppLog.Info(
                    "vpn.routes.validated",
                    "IPv4 default-route set remained unchanged after RasDial.",
                    new { DefaultRouteCount = routesBefore.Routes.Count });

                var verification = await _connectivityVerifier.VerifyAsync(
                    result.Context,
                    cancellationToken);

                if (!result.Context.IsAlive)
                {
                    throw new IOException("L2TP disappeared before verification completed.");
                }

                _rasConnection = result.Handle;
                _lastVerification = verification;
                _current = result.Context;
                SetState(VpnConnectionState.Ready);

                _monitorTask = MonitorAsync(
                    result.Handle,
                    result.Context,
                    routesBefore,
                    _shutdown.Token);

                return result.Context;
            }
            catch (OperationCanceledException)
            {
                CleanupFailedConnection(result);
                SetState(VpnConnectionState.Disconnected);
                throw;
            }
            catch (Exception ex)
            {
                CleanupFailedConnection(result);
                SetState(VpnConnectionState.Disconnected);
                AppLog.Error(
                    "vpn.connection.rejected",
                    "L2TP connection did not pass fail-closed verification.",
                    ex,
                    new { EntryName = _options.EntryName });
                throw new InvalidOperationException(
                    $"L2TP connection '{_options.EntryName}' did not pass fail-closed verification.",
                    ex);
            }
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

        var getParamsResult = RasNative.RasGetEntryDialParamsW(null, dialParams, out var hasSavedPassword);
        if (getParamsResult != RasNative.ErrorSuccess)
        {
            throw new InvalidOperationException(
                $"Unable to load RAS entry '{_options.EntryName}': {RasNative.DescribeError(getParamsResult)}");
        }

        AppLog.Info(
            "vpn.ras.parameters_loaded",
            "RAS dial parameters were loaded from the Windows phone book.",
            new { EntryName = _options.EntryName, HasSavedPassword = hasSavedPassword });

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
            var interfaceInfo = VpnInterfaceResolver.ResolveByAddress(localAddress);
            var context = new VpnContext(_options.EntryName, localAddress, interfaceInfo);
            AppLog.Info(
                "vpn.ras.connected",
                "RAS established the L2TP connection and assigned an IPv4 address.",
                new
                {
                    EntryName = _options.EntryName,
                    LocalIPv4 = localAddress.ToString(),
                    interfaceInfo.Name,
                    interfaceInfo.InterfaceIndex
                });
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

    private async Task MonitorAsync(
        nint handle,
        VpnContext context,
        DefaultRouteSnapshot routeBaseline,
        CancellationToken cancellationToken)
    {
        using var monitorCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var projectionTask = MonitorProjectionAsync(handle, context, monitorCancellation.Token);
        var routeTask = MonitorDefaultRoutesAsync(routeBaseline, monitorCancellation.Token);

        try
        {
            var completedTask = await Task.WhenAny(projectionTask, routeTask);
            await completedTask;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            AppLog.Error(
                "vpn.monitor.fail_closed",
                "Continuous L2TP guard rejected the active connection.",
                ex,
                new { context.EntryName, LocalIPv4 = context.LocalIPv4.ToString(), context.InterfaceIndex });
            Console.Error.WriteLine($"L2TP fail-closed monitor rejected the active connection: {ex.Message}");
        }
        finally
        {
            monitorCancellation.Cancel();

            try
            {
                await Task.WhenAll(projectionTask, routeTask);
            }
            catch (OperationCanceledException) when (monitorCancellation.IsCancellationRequested)
            {
                // Expected after the first monitor completes/fails or on shutdown.
            }
            catch
            {
                // The first failure has already been handled above. A concurrent secondary
                // monitor failure does not change the fail-closed action below.
            }
        }

        if (!cancellationToken.IsCancellationRequested)
        {
            MarkCurrentDisconnected(context);
            _ = RasNative.RasHangUpW(handle);
            AppLog.Warning(
                "vpn.ras.hangup",
                "RAS connection was hung up after a continuous fail-closed guard failure.",
                new { context.EntryName });
        }
    }

    private async Task MonitorProjectionAsync(
        nint handle,
        VpnContext context,
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_options.MonitorIntervalMilliseconds));

        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            var currentAddress = GetAssignedIPv4(handle);
            if (!currentAddress.Equals(context.LocalIPv4))
            {
                throw new IOException(
                    $"L2TP IPv4 changed from {context.LocalIPv4} to {currentAddress} while the connection was Ready.");
            }
        }
    }

    private async Task MonitorDefaultRoutesAsync(
        DefaultRouteSnapshot routeBaseline,
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(
            TimeSpan.FromMilliseconds(_options.RouteMonitorIntervalMilliseconds));

        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            var currentRoutes = await _routeInspector.CaptureIPv4Async(cancellationToken);
            WindowsDefaultRouteInspector.EnsureUnchanged(routeBaseline, currentRoutes);
        }
    }

    private void MarkCurrentDisconnected(VpnContext context)
    {
        context.MarkDisconnected();
        if (ReferenceEquals(Interlocked.CompareExchange(ref _current, null, context), context))
        {
            SetState(VpnConnectionState.Disconnected);
        }
    }

    private static void CleanupFailedConnection(ConnectionResult? result)
    {
        if (result is null)
        {
            return;
        }

        result.Context.MarkDisconnected();
        result.Context.Dispose();
        _ = RasNative.RasHangUpW(result.Handle);
    }

    private void SetState(VpnConnectionState state)
    {
        var previous = (VpnConnectionState)Interlocked.Exchange(ref _state, (int)state);
        if (previous != state)
        {
            AppLog.Info(
                "vpn.state",
                "VPN lifecycle state changed.",
                new { Previous = previous.ToString(), Current = state.ToString(), EntryName = _options.EntryName });
        }
    }

    public async Task DisconnectAsync()
    {
        await _gate.WaitAsync();
        try
        {
            var context = Interlocked.Exchange(ref _current, null);
            context?.MarkDisconnected();
            SetState(VpnConnectionState.Disconnected);

            var handle = _rasConnection;
            _rasConnection = 0;
            if (handle != 0)
            {
                _ = RasNative.RasHangUpW(handle);
                AppLog.Info(
                    "vpn.ras.hangup",
                    "RAS connection was disconnected.",
                    new { EntryName = _options.EntryName });
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
