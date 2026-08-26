using ProxyToAnyConnect.Diagnostics;

namespace ProxyToAnyConnect.SelfTests;

internal static class VpnLatestStatusSelfTests
{
    public static int Run()
    {
        const int generated = 300;
        try
        {
            for (var i = 0; i < generated; i++)
            {
                var vpnId = $"status-test-{i:D3}";
                VpnLatestStatusRegistry.UpdateFromLog(
                    "vpn.keepalive.failed",
                    "probe failed",
                    new
                    {
                        VpnId = vpnId,
                        FailureCount = 2,
                        FailureThreshold = 3,
                        Target = "10.0.0.1"
                    },
                    exception: null);
            }

            if (VpnLatestStatusRegistry.Count > 256)
            {
                throw new InvalidOperationException(
                    $"Latest VPN status registry exceeded its bound: {VpnLatestStatusRegistry.Count}.");
            }

            var latestId = $"status-test-{generated - 1:D3}";
            var latest = VpnLatestStatusRegistry.Get(latestId)
                ?? throw new InvalidOperationException("Latest inserted VPN status is missing.");
            if (!latest.Text.Contains("2/3", StringComparison.Ordinal) ||
                !latest.Text.Contains("10.0.0.1", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Unexpected keepalive status text: '{latest.Text}'.");
            }

            VpnLatestStatusRegistry.UpdateFromLog(
                "vpn.verification.succeeded",
                "verification passed",
                new
                {
                    VpnId = latestId,
                    ProbeTargetIPv4 = "203.0.113.10",
                    ObservedPublicIPv4 = "198.51.100.20",
                    PublicIPv4ComparisonPerformed = true,
                    ExpectedPublicIPv4 = "198.51.100.20",
                    LocalIPv4 = "10.10.0.2",
                    InterfaceIndex = 42
                },
                exception: null);

            var verified = VpnLatestStatusRegistry.Get(latestId)
                ?? throw new InvalidOperationException("Verified VPN status is missing.");
            if (!verified.Text.Contains("Verified:", StringComparison.Ordinal) ||
                !verified.Text.Contains("198.51.100.20", StringComparison.Ordinal) ||
                !verified.Text.Contains("if42", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Unexpected verification status text: '{verified.Text}'.");
            }

            VpnLatestStatusRegistry.UpdateKeepaliveSuccess(
                latestId,
                "10.0.0.1",
                TimeSpan.FromMilliseconds(17.4));

            var keepalive = VpnLatestStatusRegistry.Get(latestId)
                ?? throw new InvalidOperationException("Successful keepalive status is missing.");
            if (!keepalive.Text.Contains("Keepalive: 17 ms", StringComparison.Ordinal) ||
                keepalive.Text.Contains("Keepalive failed", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Unexpected successful keepalive status text: '{keepalive.Text}'.");
            }

            VpnLatestStatusRegistry.UpdateFromLog(
                "vpn.monitor.fail_closed",
                "monitor rejected active VPN",
                new { VpnId = latestId },
                new IOException("default-route set changed"));
            VpnLatestStatusRegistry.UpdateFromLog(
                "vpn.state",
                "state changed",
                new { VpnId = latestId, Current = "Ready" },
                exception: null);
            VpnLatestStatusRegistry.UpdateFromLog(
                "vpn.reconnect.cooldown_armed",
                "cooldown armed",
                new { VpnId = latestId, Reason = "monitor failed" },
                exception: null);

            var failed = VpnLatestStatusRegistry.Get(latestId)
                ?? throw new InvalidOperationException("Fail-closed VPN status is missing.");
            if (!failed.Text.Contains("default-route set changed", StringComparison.Ordinal) ||
                !failed.Text.Contains("Reconnect cooldown", StringComparison.Ordinal) ||
                !failed.Text.Contains("Verified:", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Structured VPN status did not preserve verification/failure/cooldown details: '{failed.Text}'.");
            }

            VpnLatestStatusRegistry.UpdateFromLog(
                "vpn.maintenance.reconnected",
                "reconnected",
                new { VpnId = latestId },
                exception: null);

            var reconnected = VpnLatestStatusRegistry.Get(latestId)
                ?? throw new InvalidOperationException("Reconnect VPN status is missing.");
            if (!reconnected.Text.Contains("Reconnect completed", StringComparison.Ordinal) ||
                !reconnected.Text.Contains("default-route set changed", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Unexpected reconnect status text: '{reconnected.Text}'.");
            }

            Console.WriteLine(
                "PASS: latest L2TP status registry is bounded and preserves verification/keepalive/reconnect/fail-closed detail");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: latest L2TP status registry regression: {ex}");
            return 1;
        }
        finally
        {
            for (var i = 0; i < generated; i++)
            {
                VpnLatestStatusRegistry.Remove($"status-test-{i:D3}");
            }
        }
    }
}
