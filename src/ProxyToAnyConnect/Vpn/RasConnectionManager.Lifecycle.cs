using System.Net;
using ProxyToAnyConnect.Configuration;
using ProxyToAnyConnect.Diagnostics;

namespace ProxyToAnyConnect.Vpn;

internal sealed partial class RasConnectionManager
{
    private async Task CleanupFailedConnectionAsync(ConnectionResult? result)
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
            // Cleanup must not replace the foreground dial/verification failure or
            // caller cancellation that caused this path. Keep the cleanup defect
            // visible in diagnostics while preserving the primary control flow.
            AppLog.Warning(
                "vpn.ras.cleanup_incomplete",
                "RAS teardown after a rejected connection did not drain cleanly.",
                new
                {
                    VpnId = _options.Id,
                    VpnName = _options.Name,
                    Error = ex.Message
                });
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
            var context = Interlocked.Exchange(ref _current, null);
            context?.MarkDisconnected();
            Volatile.Write(ref _lastVerification, null);
            SetState(VpnConnectionState.Disconnected);

            // Remove the current handle before cancelling the old monitor. Even if
            // the monitor was already entering fail-closed cleanup, its compare-
            // exchange cannot act on a future/replacement RAS session.
            var handle = Interlocked.Exchange(ref _rasConnection, 0);

            await StopMonitorLockedAsync();

            if (handle != 0)
            {
                await _dialer.HangUpAndDrainAsync(handle);
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

            var ephemeral = Interlocked.Exchange(ref _ephemeralPhonebook, null);
            ephemeral?.Dispose();
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
        _shutdown.Dispose();

        // As with the higher-level runtime gates, AvailableWaitHandle is never
        // requested. Avoid racing SemaphoreSlim.Dispose() against a ConnectAsync
        // caller that passed its pre-wait disposed check just before shutdown;
        // the managed gate is collectible with this manager.
    }

    private sealed record ConnectionResult(nint Handle, VpnContext Context);

    private sealed record DialPreparation(
        string? PhoneBookPath,
        string EntryName,
        RasNative.RasDialParams? DialParams,
        EphemeralRasPhonebook? EphemeralPhonebook);

    private readonly record struct PppProjection(IPAddress LocalIPv4, IPAddress? ServerIPv4);
}
