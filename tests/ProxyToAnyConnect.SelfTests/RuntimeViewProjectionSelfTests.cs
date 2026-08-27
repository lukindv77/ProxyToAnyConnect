using ProxyToAnyConnect.Configuration;
using ProxyToAnyConnect.Gui;
using ProxyToAnyConnect.Runtime;
using ProxyToAnyConnect.Vpn;

namespace ProxyToAnyConnect.SelfTests;

internal static class RuntimeViewProjectionSelfTests
{
    public static int Run()
    {
        try
        {
            ProxyProjectionKeepsDesiredAndResidualRowsVisible();
            VpnProjectionKeepsDesiredAndResidualRowsVisible();
            Console.WriteLine(
                "PASS: GUI runtime projection exposes desired-missing and residual actual topology without false Running state");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: GUI runtime topology projection regression: {ex}");
            return 1;
        }
    }

    private static void ProxyProjectionKeepsDesiredAndResidualRowsVisible()
    {
        var desired = new[]
        {
            new ProxyOptions
            {
                Id = "proxy-live",
                Name = "A live",
                ListenAddress = "127.0.0.1",
                ListenPort = 18100,
                VpnConnectionId = "vpn-live"
            },
            new ProxyOptions
            {
                Id = "proxy-missing",
                Name = "B desired missing",
                ListenAddress = "127.0.0.1",
                ListenPort = 18101,
                VpnConnectionId = "vpn-missing"
            }
        };
        var actual = new[]
        {
            new ProxyRuntimeSnapshot(
                "proxy-live",
                "old live name",
                "127.0.0.1",
                18100,
                "vpn-live",
                ProxyInstanceState.Running,
                null,
                10,
                20),
            new ProxyRuntimeSnapshot(
                "proxy-residual",
                "C residual runtime",
                "127.0.0.1",
                18102,
                "vpn-residual",
                ProxyInstanceState.Error,
                "cleanup fault",
                30,
                40)
        };

        var rows = RuntimeViewProjection.ProjectProxies(
            desired,
            actual,
            "selective apply failed");

        if (rows.Count != 3)
        {
            throw new InvalidOperationException($"Expected desired ∪ actual proxy rows = 3, got {rows.Count}.");
        }

        var live = rows.Single(row => row.Id == "proxy-live");
        if (live.Name != "A live" ||
            live.State != ProxyInstanceState.Running.ToString() ||
            !live.IsDesired || !live.HasRuntime || !live.CanToggle || live.ActionText != "Пауза")
        {
            throw new InvalidOperationException("Desired/live proxy projection lost desired labels or actual runtime state.");
        }

        var missing = rows.Single(row => row.Id == "proxy-missing");
        if (missing.State != "Error" || missing.Status != "selective apply failed" ||
            !missing.IsDesired || missing.HasRuntime || missing.CanToggle || missing.ActionText.Length != 0)
        {
            throw new InvalidOperationException(
                "Saved proxy with no runtime was hidden or exposed as an actionable/healthy runtime.");
        }

        var residual = rows.Single(row => row.Id == "proxy-residual");
        if (residual.IsDesired || !residual.HasRuntime || residual.CanToggle ||
            !residual.Status.Contains(RuntimeViewProjection.RuntimeOnlyStatus, StringComparison.Ordinal) ||
            !residual.Status.Contains("cleanup fault", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Residual runtime-only proxy was hidden or presented as desired state.");
        }

        var pending = RuntimeViewProjection.ProjectProxies(
            desired.Where(item => item.Id == "proxy-missing"),
            Array.Empty<ProxyRuntimeSnapshot>(),
            configurationError: null).Single();
        if (pending.State != "Pending" || pending.Status != RuntimeViewProjection.DesiredRuntimeMissingStatus)
        {
            throw new InvalidOperationException("Desired runtime gap without a host error was not represented as Pending.");
        }
    }

    private static void VpnProjectionKeepsDesiredAndResidualRowsVisible()
    {
        var desired = new[]
        {
            new L2tpOptions
            {
                Id = "vpn-live",
                Name = "A live VPN",
                Mode = L2tpConnectionMode.ExistingWindowsProfile,
                EntryName = "Live",
                Verification = new VerificationOptions { PublicAddress = "vpn.example.com" }
            },
            new L2tpOptions
            {
                Id = "vpn-missing",
                Name = "B desired VPN",
                Mode = L2tpConnectionMode.ExistingWindowsProfile,
                EntryName = "Missing",
                Verification = new VerificationOptions { PublicAddress = "vpn.example.com" }
            }
        };
        var actual = new[]
        {
            new L2tpRuntimeSnapshot(
                "vpn-live",
                "old live VPN name",
                L2tpConnectionMode.ExistingWindowsProfile,
                false,
                VpnConnectionState.Ready,
                "10.0.0.2",
                42,
                1,
                100,
                200,
                12.5,
                4),
            new L2tpRuntimeSnapshot(
                "vpn-residual",
                "C residual VPN",
                L2tpConnectionMode.CustomEphemeral,
                true,
                VpnConnectionState.Disconnected,
                null,
                null,
                0,
                0,
                0,
                null,
                0)
        };

        var rows = RuntimeViewProjection.ProjectVpns(
            desired,
            actual,
            "vpn apply failed",
            id => id == "vpn-live" ? "verification ready" : id == "vpn-residual" ? "hangup retry pending" : null);

        if (rows.Count != 3)
        {
            throw new InvalidOperationException($"Expected desired ∪ actual VPN rows = 3, got {rows.Count}.");
        }

        var live = rows.Single(row => row.Id == "vpn-live");
        if (live.Name != "A live VPN" || live.State != VpnConnectionState.Ready.ToString() ||
            live.LocalIPv4 != "10.0.0.2" || live.Status != "verification ready" ||
            !live.IsDesired || !live.HasRuntime)
        {
            throw new InvalidOperationException("Desired/live VPN projection lost desired labels or actual Ready data.");
        }

        var missing = rows.Single(row => row.Id == "vpn-missing");
        if (missing.State != "Error" || missing.Status != "vpn apply failed" ||
            !missing.IsDesired || missing.HasRuntime)
        {
            throw new InvalidOperationException("Saved VPN with no runtime was hidden or presented as healthy.");
        }

        var residual = rows.Single(row => row.Id == "vpn-residual");
        if (residual.IsDesired || !residual.HasRuntime ||
            !residual.Status.Contains(RuntimeViewProjection.RuntimeOnlyStatus, StringComparison.Ordinal) ||
            !residual.Status.Contains("hangup retry pending", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Residual runtime-only VPN was hidden or lost its cleanup status.");
        }
    }
}
