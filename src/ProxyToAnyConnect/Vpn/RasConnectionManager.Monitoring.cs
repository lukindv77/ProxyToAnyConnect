using System.Net;
using ProxyToAnyConnect.Configuration;
using ProxyToAnyConnect.Diagnostics;

namespace ProxyToAnyConnect.Vpn;

internal sealed partial class RasConnectionManager
{
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
            // Claim this exact generation before native teardown. If RasHangUp or
            // invalid-handle drain fails, the shared helper restores this same
            // HRASCONN only when no newer generation occupies the slot. The monitor
            // then faults before PBK release, leaving both resources owned for the
            // next Disconnect/Connect cleanup attempt rather than permitting an
            // overlapping replacement dial.
            await HangUpClaimedRasHandleAsync(handle);
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
                    $"L2TP IPv4 changed from {context.LocalIPv4} to {projection.LocalIPv4} while the connection was Ready.");
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
}
