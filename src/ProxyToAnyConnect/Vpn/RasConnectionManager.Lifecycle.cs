using System.Net;
using System.Runtime.ExceptionServices;
using ProxyToAnyConnect.Configuration;
using ProxyToAnyConnect.Diagnostics;

namespace ProxyToAnyConnect.Vpn;

internal sealed partial class RasConnectionManager
{
    private async Task CleanupFailedConnectionAsync(
        ConnectionResult? result,
        Exception primaryFailure)
    {
        if (result is null)
        {
            return;
        }

        result.Context.MarkDisconnected();
        result.Context.Dispose();
        try
        {
            await _dialer.HangUpAndDrainAsync(result.Handle);
        }
        catch (Exception ex)
        {
            // A rejected dial/verification still owns its exact HRASCONN until RAS
            // proves terminal invalid-handle. Retain that handle for a later cleanup
            // attempt rather than losing it while preserving the foreground failure.
            _ = Interlocked.CompareExchange(ref _rasConnection, result.Handle, 0);
            primaryFailure.Data["RasCleanup:rejected-connection-hangup"] =
                $"{ex.GetType().FullName}: {ex.Message}";

            AppLog.Warning(
                "vpn.ras.cleanup_incomplete",
                "RAS teardown after a rejected connection did not drain cleanly; exact handle ownership was retained for retry.",
                new
                {
                    VpnId = _options.Id,
                    VpnName = _options.Name,
                    Error = ex.Message,
                    PendingHandleRetained = Volatile.Read(ref _rasConnection) == result.Handle
                });
        }
    }

    private void RetainEphemeralPhonebookForPendingRas(
        ref EphemeralRasPhonebook? localEphemeralPhonebook)
    {
        if (localEphemeralPhonebook is null || Volatile.Read(ref _rasConnection) == 0)
        {
            return;
        }

        if (Interlocked.CompareExchange(
                ref _ephemeralPhonebook,
                localEphemeralPhonebook,
                null) is null)
        {
            localEphemeralPhonebook = null;
        }
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

    private async Task HangUpClaimedRasHandleAsync(nint handle)
    {
        if (handle == 0)
        {
            return;
        }

        try
        {
            await _dialer.HangUpAndDrainAsync(handle);
        }
        catch
        {
            // The caller claimed this exact generation by moving the shared slot to
            // zero. Restore ownership only if nobody has published another handle in
            // the meantime; never overwrite a newer generation.
            _ = Interlocked.CompareExchange(ref _rasConnection, handle, 0);
            throw;
        }
    }

    private async Task<nint> DrainPendingRasOwnershipLockedAsync()
    {
        var handle = Interlocked.Exchange(ref _rasConnection, 0);
        if (handle != 0)
        {
            await HangUpClaimedRasHandleAsync(handle);
        }

        // The private phonebook is dependent on terminal RAS ownership. It is only
        // safe to delete after no non-terminal handle remains in the manager slot.
        if (Volatile.Read(ref _rasConnection) == 0)
        {
            var ephemeral = Interlocked.Exchange(ref _ephemeralPhonebook, null);
            ephemeral?.Dispose();
        }

        return handle;
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
            Exception? cleanupFailure = null;
            var context = Interlocked.Exchange(ref _current, null);
            Volatile.Write(ref _lastVerification, null);
            SetState(VpnConnectionState.Disconnected);

            try
            {
                context?.MarkDisconnected();
            }
            catch (Exception ex)
            {
                CaptureLifecycleCleanupFailure(ref cleanupFailure, ex, "context-disconnect");
            }

            // Claim the currently published handle before cancelling the monitor so
            // explicit disconnect owns this generation. If the monitor had already
            // claimed it, StopMonitorLockedAsync waits for that cleanup and a failed
            // monitor hangup restores the handle for the second claim below.
            var handle = Interlocked.Exchange(ref _rasConnection, 0);

            try
            {
                await StopMonitorLockedAsync();
            }
            catch (Exception ex)
            {
                CaptureLifecycleCleanupFailure(ref cleanupFailure, ex, "monitor-stop");
            }

            if (handle == 0)
            {
                handle = Interlocked.Exchange(ref _rasConnection, 0);
            }

            if (handle != 0)
            {
                try
                {
                    await HangUpClaimedRasHandleAsync(handle);
                    AppLog.Info(
                        "vpn.ras.hangup",
                        "RAS connection was disconnected.",
                        new
                        {
                            VpnId = _options.Id,
                            VpnName = _options.Name,
                            Mode = _options.Mode.ToString(),
                            EntryName = context?.EntryName
                        });
                }
                catch (Exception ex)
                {
                    CaptureLifecycleCleanupFailure(ref cleanupFailure, ex, "ras-hangup");
                }
            }

            if (Volatile.Read(ref _rasConnection) == 0)
            {
                var ephemeral = Interlocked.Exchange(ref _ephemeralPhonebook, null);
                if (ephemeral is not null)
                {
                    try
                    {
                        ephemeral.Dispose();
                    }
                    catch (Exception ex)
                    {
                        CaptureLifecycleCleanupFailure(ref cleanupFailure, ex, "ephemeral-phonebook");
                    }
                }
            }

            RethrowLifecycleCleanupFailure(cleanupFailure);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        var firstDispose = Interlocked.Exchange(ref _disposed, 1) == 0;
        if (!firstDispose)
        {
            // Disposal remains idempotent after successful teardown, but a previous
            // failed RasHangUp intentionally retained the exact handle because the
            // native state was not proven terminal. Allow later DisposeAsync calls to
            // retry that residual ownership instead of stranding it until process exit.
            if (Volatile.Read(ref _rasConnection) == 0)
            {
                return;
            }

            Exception? residualFailure = null;
            try
            {
                await DisconnectAsync();
            }
            catch (Exception ex)
            {
                CaptureLifecycleCleanupFailure(ref residualFailure, ex, "disposed-residual-retry");
            }

            RethrowLifecycleCleanupFailure(residualFailure);
            return;
        }

        Exception? cleanupFailure = null;
        try
        {
            try
            {
                _shutdown.Cancel();
            }
            catch (Exception ex)
            {
                // Cancellation callbacks are secondary teardown participants. A bad
                // callback must not prevent exact RAS/monitor/context cleanup below.
                CaptureLifecycleCleanupFailure(ref cleanupFailure, ex, "shutdown-cancel");
            }

            try
            {
                await DisconnectAsync();
            }
            catch (Exception ex)
            {
                CaptureLifecycleCleanupFailure(ref cleanupFailure, ex, "disconnect");
            }

            // A transient RasHangUp failure must not automatically strand the exact
            // handle until process exit. Make one bounded immediate retry while this
            // manager still owns its lifecycle gate; preserve the first cleanup error
            // as the caller-visible diagnostic even if the retry succeeds.
            if (Volatile.Read(ref _rasConnection) != 0)
            {
                try
                {
                    await DisconnectAsync();
                }
                catch (Exception ex)
                {
                    CaptureLifecycleCleanupFailure(ref cleanupFailure, ex, "disconnect-retry");
                }
            }
        }
        finally
        {
            try
            {
                _shutdown.Dispose();
            }
            catch (Exception ex)
            {
                CaptureLifecycleCleanupFailure(ref cleanupFailure, ex, "shutdown-token");
            }
        }

        RethrowLifecycleCleanupFailure(cleanupFailure);

        // As with the higher-level runtime gates, AvailableWaitHandle is never
        // requested. Avoid racing SemaphoreSlim.Dispose() against a ConnectAsync
        // caller that passed its pre-wait disposed check just before shutdown;
        // the managed gate is collectible with this manager.
    }

    private static void CaptureLifecycleCleanupFailure(
        ref Exception? primaryFailure,
        Exception failure,
        string phase)
    {
        if (primaryFailure is null)
        {
            primaryFailure = failure;
            return;
        }

        primaryFailure.Data[$"RasCleanup:{phase}"] =
            $"{failure.GetType().FullName}: {failure.Message}";
    }

    private static void RethrowLifecycleCleanupFailure(Exception? cleanupFailure)
    {
        if (cleanupFailure is not null)
        {
            ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
        }
    }

    private sealed record ConnectionResult(nint Handle, VpnContext Context);

    private sealed record DialPreparation(
        string? PhoneBookPath,
        string EntryName,
        RasNative.RasDialParams? DialParams,
        EphemeralRasPhonebook? EphemeralPhonebook);

    private readonly record struct PppProjection(IPAddress LocalIPv4, IPAddress? ServerIPv4);
}
