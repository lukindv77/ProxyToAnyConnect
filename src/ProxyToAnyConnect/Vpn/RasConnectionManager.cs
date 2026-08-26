using System.Net;
using System.Runtime.InteropServices;
using ProxyToAnyConnect.Configuration;
using ProxyToAnyConnect.Diagnostics;
using ProxyToAnyConnect.Runtime;

namespace ProxyToAnyConnect.Vpn;

internal sealed class RasConnectionManager : IAsyncDisposable
{
    private readonly L2tpOptions _options;
    private readonly L2tpRuntimeMetrics? _metrics;
    private readonly WindowsVpnProfileInspector _profileInspector;
    private readonly WindowsDefaultRouteInspector _routeInspector;
    private readonly VpnConnectivityVerifier _connectivityVerifier;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();

    private nint _rasConnection;
    private VpnContext? _current;
    private VpnVerificationResult? _lastVerification;
    private EphemeralRasPhonebook? _ephemeralPhonebook;
    private CancellationTokenSource? _monitorCancellation;
    private Task? _monitorTask;
    private long _retryNotBeforeTickCount64;
    private int _state = (int)VpnConnectionState.Disconnected;
    private int _disposed;

    public RasConnectionManager(L2tpOptions options, L2tpRuntimeMetrics? metrics = null)
    {
        _options = options;
        _metrics = metrics;
        _profileInspector = new WindowsVpnProfileInspector();
        _routeInspector = new WindowsDefaultRouteInspector();
        _connectivityVerifier = new VpnConnectivityVerifier(options.Verification);
    }

    public VpnContext? Current => Volatile.Read(ref _current);
    public VpnVerificationResult? LastVerification => Volatile.Read(ref _lastVerification);
    public VpnConnectionState State => (VpnConnectionState)Volatile.Read(ref _state);
    public long ReconnectCooldownRemainingMilliseconds => GetReconnectCooldownRemainingMilliseconds(
        Environment.TickCount64,
        Volatile.Read(ref _retryNotBeforeTickCount64));

    public async Task<VpnContext> ConnectAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

            if (_current is { IsAlive: true } existing && State == VpnConnectionState.Ready)
            {
                return existing;
            }

            // A previous fail-closed monitor may already be completed while its
            // session CTS/task are still referenced by the manager. Join and dispose
            // that bounded per-session state before considering a new connection.
            await StopMonitorLockedAsync();

            var retryRemaining = GetReconnectCooldownRemainingMilliseconds(
                Environment.TickCount64,
                Volatile.Read(ref _retryNotBeforeTickCount64));
            if (retryRemaining > 0)
            {
                AppLog.Warning(
                    "vpn.reconnect.cooldown_active",
                    "L2TP reconnect was skipped because a previous fail-closed attempt is cooling down.",
                    new { VpnId = _options.Id, VpnName = _options.Name, RetryAfterMilliseconds = retryRemaining });
                throw new InvalidOperationException(
                    $"L2TP reconnect cooldown is active for another {retryRemaining} ms.");
            }

            SetState(VpnConnectionState.Dialing);
            ConnectionResult? result = null;
            EphemeralRasPhonebook? localEphemeralPhonebook = null;

            try
            {
                var preparation = await PrepareDialAsync(cancellationToken);
                localEphemeralPhonebook = preparation.EphemeralPhonebook;

                var routesBefore = await _routeInspector.CaptureIPv4Async(cancellationToken);

                result = await Task.Run(
                    () => ConnectCore(
                        preparation.PhoneBookPath,
                        preparation.EntryName,
                        preparation.DialParams),
                    cancellationToken);
                SetState(VpnConnectionState.Verifying);

                var routesAfter = await _routeInspector.CaptureIPv4Async(cancellationToken);
                WindowsDefaultRouteInspector.EnsureUnchanged(routesBefore, routesAfter);
                AppLog.Info(
                    "vpn.routes.validated",
                    "IPv4 default-route set remained unchanged after RasDial.",
                    new { VpnId = _options.Id, VpnName = _options.Name, DefaultRouteCount = routesBefore.Routes.Count });

                var verification = await _connectivityVerifier.VerifyAsync(
                    result.Context,
                    cancellationToken);

                if (!result.Context.IsAlive)
                {
                    throw new IOException("L2TP disappeared before verification completed.");
                }

                AppLog.Info(
                    "vpn.verification.succeeded",
                    "L2TP-bound connectivity verification completed successfully.",
                    new
                    {
                        VpnId = _options.Id,
                        VpnName = _options.Name,
                        ProbeTargetIPv4 = verification.ProbeTargetIPv4.ToString(),
                        ObservedPublicIPv4 = verification.ObservedPublicIPv4?.ToString(),
                        verification.PublicIPv4ComparisonPerformed,
                        ExpectedPublicIPv4 = verification.ExpectedPublicIPv4?.ToString(),
                        LocalIPv4 = result.Context.LocalIPv4.ToString(),
                        result.Context.InterfaceIndex
                    });

                _rasConnection = result.Handle;
                _lastVerification = verification;
                _current = result.Context;

                var ownedEphemeralPhonebook = localEphemeralPhonebook;
                if (ownedEphemeralPhonebook is not null)
                {
                    _ephemeralPhonebook = ownedEphemeralPhonebook;
                    localEphemeralPhonebook = null;
                }

                Volatile.Write(ref _retryNotBeforeTickCount64, 0);
                SetState(VpnConnectionState.Ready);

                var monitorCancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
                _monitorCancellation = monitorCancellation;
                _monitorTask = MonitorAsync(
                    result.Handle,
                    result.Context,
                    routesBefore,
                    ownedEphemeralPhonebook,
                    monitorCancellation.Token);

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
                ArmReconnectCooldown("Dialing or verification failed.");
                AppLog.Error(
                    "vpn.connection.rejected",
                    "L2TP connection did not pass fail-closed verification.",
                    ex,
                    new
                    {
                        VpnId = _options.Id,
                        VpnName = _options.Name,
                        Mode = _options.Mode.ToString(),
                        EntryName = _options.Mode == L2tpConnectionMode.ExistingWindowsProfile
                            ? _options.EntryName
                            : null
                    });
                throw new InvalidOperationException(
                    $"L2TP connection '{_options.Name}' did not pass fail-closed verification.",
                    ex);
            }
            finally
            {
                localEphemeralPhonebook?.Dispose();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<DialPreparation> PrepareDialAsync(CancellationToken cancellationToken)
    {
        switch (_options.Mode)
        {
            case L2tpConnectionMode.ExistingWindowsProfile:
            {
                var profile = await _profileInspector.InspectAsync(_options.EntryName, cancellationToken);
                WindowsVpnProfileInspector.ValidateForProxy(profile);
                var phoneBook = WindowsVpnProfileInspector.ResolveRasPhoneBook(profile);

                AppLog.Info(
                    "vpn.profile.validated",
                    "Windows VPN profile passed L2TP split-tunnel validation.",
                    new
                    {
                        VpnId = _options.Id,
                        VpnName = _options.Name,
                        profile.Name,
                        profile.TunnelType,
                        profile.SplitTunneling,
                        profile.AllUserConnection
                    });

                var dialParams = new RasNative.RasDialParams
                {
                    DwSize = checked((uint)Marshal.SizeOf<RasNative.RasDialParams>()),
                    SzEntryName = _options.EntryName
                };

                return new DialPreparation(phoneBook, _options.EntryName, dialParams, null);
            }

            case L2tpConnectionMode.CustomEphemeral:
            {
                var phoneBook = EphemeralRasPhonebook.Create(_options);
                try
                {
                    var dialParams = phoneBook.CreateDialParameters();
                    AppLog.Info(
                        "vpn.ephemeral.prepared",
                        "Custom L2TP private RAS phonebook was prepared for dialing.",
                        new
                        {
                            VpnId = _options.Id,
                            VpnName = _options.Name,
                            EntryName = phoneBook.EntryName,
                            _options.Custom.Server,
                            _options.Custom.UseCurrentWindowsCredentials,
                            IpsecAuthentication = _options.Custom.IpsecAuthentication.ToString()
                        });
                    return new DialPreparation(
                        phoneBook.PhoneBookPath,
                        phoneBook.EntryName,
                        dialParams,
                        phoneBook);
                }
                catch
                {
                    phoneBook.Dispose();
                    throw;
                }
            }

            default:
                throw new InvalidOperationException($"Unsupported L2TP mode '{_options.Mode}'.");
        }
    }

    private ConnectionResult ConnectCore(
        string? phoneBookPath,
        string entryName,
        RasNative.RasDialParams dialParams)
    {
        var dialExtensions = new RasNative.RasDialExtensions
        {
            DwSize = checked((uint)Marshal.SizeOf<RasNative.RasDialExtensions>())
        };

        var result = RasNative.RasDialW(
            ref dialExtensions,
            phoneBookPath,
            ref dialParams,
            0,
            0,
            out var handle);

        if (result != RasNative.ErrorSuccess)
        {
            throw new InvalidOperationException(
                $"RasDial failed for '{entryName}': {RasNative.DescribeError(result)}");
        }

        try
        {
            var projection = GetProjection(handle);
            var interfaceInfo = VpnInterfaceResolver.ResolveByAddress(projection.LocalIPv4);
            var context = new VpnContext(
                entryName,
                projection.LocalIPv4,
                interfaceInfo,
                projection.ServerIPv4);
            AppLog.Info(
                "vpn.ras.connected",
                "RAS established the L2TP connection and assigned an IPv4 address.",
                new
                {
                    VpnId = _options.Id,
                    VpnName = _options.Name,
                    EntryName = entryName,
                    Mode = _options.Mode.ToString(),
                    LocalIPv4 = projection.LocalIPv4.ToString(),
                    ServerIPv4 = projection.ServerIPv4?.ToString(),
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

    private static PppProjection GetProjection(nint handle)
    {
        var projection = new RasNative.RasPppIp
        {
            DwSize = checked((uint)Marshal.SizeOf<RasNative.RasPppIp>())
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

        if (!IPAddress.TryParse(projection.SzIpAddress, out var localAddress) ||
            localAddress.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            throw new InvalidOperationException(
                $"RAS returned an invalid IPv4 address: '{projection.SzIpAddress}'.");
        }

        IPAddress? serverAddress = null;
        if (!string.IsNullOrWhiteSpace(projection.SzServerIpAddress))
        {
            if (!IPAddress.TryParse(projection.SzServerIpAddress, out serverAddress) ||
                serverAddress.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            {
                throw new InvalidOperationException(
                    $"RAS returned an invalid PPP server IPv4 address: '{projection.SzServerIpAddress}'.");
            }
        }

        return new PppProjection(localAddress, serverAddress);
    }

    private async Task MonitorAsync(
        nint handle,
        VpnContext context,
        DefaultRouteSnapshot routeBaseline,
        EphemeralRasPhonebook? ephemeralPhonebook,
        CancellationToken cancellationToken)
    {
        using var monitorCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var projectionTask = MonitorProjectionAsync(handle, context, monitorCancellation.Token);
        var routeTask = MonitorDefaultRoutesAsync(routeBaseline, monitorCancellation.Token);
        var keepaliveTask = MonitorKeepaliveAsync(context, monitorCancellation.Token);

        try
        {
            var completedTask = await Task.WhenAny(projectionTask, routeTask, keepaliveTask);
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
                new
                {
                    VpnId = _options.Id,
                    VpnName = _options.Name,
                    context.EntryName,
                    LocalIPv4 = context.LocalIPv4.ToString(),
                    context.InterfaceIndex
                });
        }
        finally
        {
            monitorCancellation.Cancel();

            try
            {
                await Task.WhenAll(projectionTask, routeTask, keepaliveTask);
            }
            catch (OperationCanceledException) when (monitorCancellation.IsCancellationRequested)
            {
            }
            catch
            {
            }
        }

        // Explicit DisconnectAsync removes Current before cancelling this session
        // monitor. That makes this check robust even if cancellation races the
        // fail-closed branch between two instructions.
        if (cancellationToken.IsCancellationRequested || !ReferenceEquals(Current, context))
        {
            return;
        }

        ArmReconnectCooldown("Continuous fail-closed monitor rejected the active VPN.");
        MarkCurrentDisconnected(context);

        if (Interlocked.CompareExchange(ref _rasConnection, 0, handle) == handle)
        {
            _ = RasNative.RasHangUpW(handle);
        }

        ReleaseEphemeralPhonebook(ephemeralPhonebook);
        AppLog.Warning(
            "vpn.ras.hangup",
            "RAS connection was hung up after a continuous fail-closed guard failure.",
            new { VpnId = _options.Id, VpnName = _options.Name, context.EntryName });
    }

    private async Task MonitorProjectionAsync(
        nint handle,
        VpnContext context,
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_options.MonitorIntervalMilliseconds));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            var projection = GetProjection(handle);
            if (!projection.LocalIPv4.Equals(context.LocalIPv4))
            {
                throw new IOException(
                    $"L2TP PPP IPv4 changed from {context.LocalIPv4} to {projection.LocalIPv4}.");
            }
        }
    }

    private async Task MonitorDefaultRoutesAsync(
        DefaultRouteSnapshot routeBaseline,
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_options.RouteMonitorIntervalMilliseconds));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            var currentRoutes = await _routeInspector.CaptureIPv4Async(cancellationToken);
            WindowsDefaultRouteInspector.EnsureUnchanged(routeBaseline, currentRoutes);
        }
    }

    private async Task MonitorKeepaliveAsync(VpnContext context, CancellationToken cancellationToken)
    {
        if (_options.Keepalive.Mode == L2tpKeepaliveMode.Off)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return;
        }

        var target = ResolveKeepaliveTarget(context);
        var failureCount = 0;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_options.Keepalive.IntervalSeconds));

        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            var result = await IcmpBoundPing.SendAsync(
                context.LocalIPv4,
                target,
                TimeSpan.FromMilliseconds(_options.Keepalive.TimeoutMilliseconds),
                cancellationToken);

            if (result.Success && result.RoundTripTime is TimeSpan rtt)
            {
                var recovered = failureCount > 0;
                failureCount = 0;
                _metrics?.Ping.AddSuccessfulSample(rtt);
                VpnLatestStatusRegistry.UpdateKeepaliveSuccess(
                    _options.Id,
                    target.ToString(),
                    rtt);

                if (recovered)
                {
                    AppLog.Info(
                        "vpn.keepalive.recovered",
                        "L2TP keepalive recovered after previous failures.",
                        new
                        {
                            VpnId = _options.Id,
                            VpnName = _options.Name,
                            Target = target.ToString(),
                            RoundTripMilliseconds = rtt.TotalMilliseconds
                        });
                }

                continue;
            }

            failureCount++;
            AppLog.Warning(
                "vpn.keepalive.failed",
                "L2TP keepalive probe failed.",
                new
                {
                    VpnId = _options.Id,
                    VpnName = _options.Name,
                    Target = target.ToString(),
                    FailureCount = failureCount,
                    FailureThreshold = _options.Keepalive.FailureThreshold,
                    result.ErrorCode
                });

            if (failureCount >= _options.Keepalive.FailureThreshold)
            {
                throw new IOException(
                    $"L2TP keepalive failed {failureCount} consecutive times for {target}.");
            }
        }
    }

    private IPAddress ResolveKeepaliveTarget(VpnContext context)
    {
        return _options.Keepalive.Mode switch
        {
            L2tpKeepaliveMode.VpnServerInternalIPv4 => context.ServerIPv4
                ?? throw new InvalidOperationException(
                    "RAS did not provide the PPP server IPv4 required by VpnServerInternalIPv4 keepalive."),
            L2tpKeepaliveMode.CustomIPv4 => IPAddress.Parse(_options.Keepalive.CustomIPv4),
            _ => throw new InvalidOperationException($"Unsupported keepalive mode '{_options.Keepalive.Mode}'.")
        };
    }

    private void ArmReconnectCooldown(string reason)
    {
        if (_options.ReconnectCooldownMilliseconds <= 0)
        {
            Volatile.Write(ref _retryNotBeforeTickCount64, 0);
            return;
        }

        var retryNotBefore = Environment.TickCount64 + _options.ReconnectCooldownMilliseconds;
        Volatile.Write(ref _retryNotBeforeTickCount64, retryNotBefore);
        AppLog.Warning(
            "vpn.reconnect.cooldown_armed",
            "L2TP reconnect cooldown was armed after a fail-closed event.",
            new
            {
                VpnId = _options.Id,
                VpnName = _options.Name,
                _options.ReconnectCooldownMilliseconds,
                Reason = reason
            });
    }

    internal static long GetReconnectCooldownRemainingMilliseconds(long now, long retryNotBefore) =>
        retryNotBefore <= now ? 0 : retryNotBefore - now;

    private void MarkCurrentDisconnected(VpnContext context)
    {
        context.MarkDisconnected();
        if (ReferenceEquals(Interlocked.CompareExchange(ref _current, null, context), context))
        {
            Volatile.Write(ref _lastVerification, null);
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

    private void ReleaseEphemeralPhonebook(EphemeralRasPhonebook? expected)
    {
        if (expected is null)
        {
            return;
        }

        if (ReferenceEquals(
                Interlocked.CompareExchange(ref _ephemeralPhonebook, null, expected),
                expected))
        {
            expected.Dispose();
        }
    }

    private async Task StopMonitorLockedAsync()
    {
        var cancellation = _monitorCancellation;
        var task = _monitorTask;
        _monitorCancellation = null;
        _monitorTask = null;

        if (cancellation is null)
        {
            if (task is not null)
            {
                try
                {
                    await task;
                }
                catch (OperationCanceledException)
                {
                }
            }
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

    private void SetState(VpnConnectionState state)
    {
        var previous = (VpnConnectionState)Interlocked.Exchange(ref _state, (int)state);
        if (previous != state)
        {
            AppLog.Info(
                "vpn.state",
                "VPN lifecycle state changed.",
                new
                {
                    VpnId = _options.Id,
                    VpnName = _options.Name,
                    Previous = previous.ToString(),
                    Current = state.ToString(),
                    Mode = _options.Mode.ToString(),
                    EntryName = _options.Mode == L2tpConnectionMode.ExistingWindowsProfile
                        ? _options.EntryName
                        : null
                });
        }
    }

    public async Task DisconnectAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await DisconnectLockedAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task DisconnectLockedAsync()
    {
        var current = Interlocked.Exchange(ref _current, null);
        if (current is not null)
        {
            current.MarkDisconnected();
            current.ReleaseManagerReference();
            Volatile.Write(ref _lastVerification, null);
        }

        SetState(VpnConnectionState.Disconnected);
        await StopMonitorLockedAsync();

        var handle = Interlocked.Exchange(ref _rasConnection, 0);
        if (handle != 0)
        {
            _ = RasNative.RasHangUpW(handle);
            AppLog.Info(
                "vpn.ras.hangup",
                "RAS connection was disconnected.",
                new { VpnId = _options.Id, VpnName = _options.Name });
        }

        var ephemeralPhonebook = Interlocked.Exchange(ref _ephemeralPhonebook, null);
        ephemeralPhonebook?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _shutdown.Cancel();
        await _gate.WaitAsync();
        try
        {
            await DisconnectLockedAsync();
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
            _shutdown.Dispose();
        }
    }

    private sealed record DialPreparation(
        string? PhoneBookPath,
        string EntryName,
        RasNative.RasDialParams DialParams,
        EphemeralRasPhonebook? EphemeralPhonebook);

    private sealed record ConnectionResult(nint Handle, VpnContext Context);
    private sealed record PppProjection(IPAddress LocalIPv4, IPAddress? ServerIPv4);
}
