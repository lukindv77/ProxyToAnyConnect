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
                "vpn.connection.rejected",
                "connection rejected",
                new { VpnId = latestId },
                new InvalidOperationException("verification mismatch"));

            var rejected = VpnLatestStatusRegistry.Get(latestId)
                ?? throw new InvalidOperationException("Rejected VPN status is missing.");
            if (!rejected.Text.Contains("verification mismatch", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Unexpected rejection status text: '{rejected.Text}'.");
            }

            Console.WriteLine("PASS: latest L2TP status registry is bounded and replaces status in place");
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
