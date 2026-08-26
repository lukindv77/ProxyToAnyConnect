using System.Net;
using System.Runtime.InteropServices;
using ProxyToAnyConnect.Configuration;
using ProxyToAnyConnect.Diagnostics;
using ProxyToAnyConnect.Runtime;

namespace ProxyToAnyConnect.Vpn;

internal sealed partial class RasConnectionManager : IAsyncDisposable
{
    private readonly L2tpOptions _options;
    private readonly L2tpRuntimeMetrics? _metrics;
    private readonly WindowsVpnProfileInspector _profileInspector;
    private readonly WindowsDefaultRouteInspector _routeInspector;
    private readonly VpnConnectivityVerifier _connectivityVerifier;
    private readonly RasDialer _dialer = new();
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

                result = await ConnectCoreAsync(
                    preparation.PhoneBookPath,
                    preparation.EntryName,
                    preparation.DialParams,
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
                await CleanupFailedConnectionAsync(result);
                SetState(VpnConnectionState.Disconnected);
                throw;
            }
            catch (Exception ex)
            {
                await CleanupFailedConnectionAsync(result);
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
                        profile.AllUserConnection,
                        PhoneBookScope = profile.AllUserConnection ? "AllUsers" : "CurrentUser"
                    });

                return new DialPreparation(phoneBook, _options.EntryName, null, null);
            }

            case L2tpConnectionMode.CustomEphemeral:
            {
                var ephemeral = EphemeralRasPhonebook.Create(_options);
                try
                {
                    var dialParams = ephemeral.CreateDialParams(_options.Custom);
                    AppLog.Info(
                        "vpn.ephemeral.prepared",
                        "Custom L2TP private phonebook is ready for RasDial.",
                        new
                        {
                            VpnId = _options.Id,
                            VpnName = _options.Name,
                            ephemeral.EntryName,
                            ServerAddress = _options.Custom.ServerAddress,
                            _options.Custom.UseCurrentWindowsCredentials,
                            IpsecAuthentication = _options.Custom.IpsecAuthentication.ToString()
                        });
                    return new DialPreparation(
                        ephemeral.PhoneBookPath,
                        ephemeral.EntryName,
                        dialParams,
                        ephemeral);
                }
                catch
                {
                    ephemeral.Dispose();
                    throw;
                }
            }

            default:
                throw new NotSupportedException($"Unsupported L2TP mode '{_options.Mode}'.");
        }
    }

    private async Task<ConnectionResult> ConnectCoreAsync(
        string? phoneBook,
        string entryName,
        RasNative.RasDialParams? explicitDialParams,
        CancellationToken cancellationToken)
    {
        RasNative.RasDialParams dialParams;
        if (explicitDialParams is null)
        {
            dialParams = new RasNative.RasDialParams
            {
                DwSize = checked((uint)Marshal.SizeOf<RasNative.RasDialParams>()),
                SzEntryName = entryName
            };

            var getParamsResult = RasNative.RasGetEntryDialParamsW(phoneBook, dialParams, out var hasSavedPassword);
            if (getParamsResult != RasNative.ErrorSuccess)
            {
                throw new InvalidOperationException(
                    $"Unable to load RAS entry '{entryName}': {RasNative.DescribeError(getParamsResult)}");
            }

            AppLog.Info(
                "vpn.ras.parameters_loaded",
                "RAS dial parameters were loaded from the Windows phone book.",
                new
                {
                    VpnId = _options.Id,
                    VpnName = _options.Name,
                    EntryName = entryName,
                    HasSavedPassword = hasSavedPassword,
                    PhoneBookScope = phoneBook is null ? "CurrentUserDefault" : "ExplicitPhoneBook"
                });
        }
        else
        {
            dialParams = explicitDialParams;
            AppLog.Info(
                "vpn.ras.parameters_loaded",
                "RAS dial parameters were prepared from the custom ephemeral L2TP configuration.",
                new
                {
                    VpnId = _options.Id,
                    VpnName = _options.Name,
                    EntryName = entryName,
                    Mode = "CustomEphemeral",
                    HasExplicitUserName = !_options.Custom.UseCurrentWindowsCredentials
                });
        }

        var handle = await _dialer.DialAsync(
            phoneBook,
            dialParams,
            cancellationToken);

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
            await _dialer.HangUpAndDrainAsync(handle);
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
}
