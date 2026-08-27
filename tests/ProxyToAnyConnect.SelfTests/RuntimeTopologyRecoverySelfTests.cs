using System.Reflection;
using ProxyToAnyConnect.Configuration;
using ProxyToAnyConnect.Runtime;

namespace ProxyToAnyConnect.SelfTests;

internal static class RuntimeTopologyRecoverySelfTests
{
    public static async Task<int> RunAsync()
    {
        try
        {
            await SameConfigurationRebuildsMissingProxyAndVpnAsync();
            Console.WriteLine(
                "PASS: same desired configuration repairs missing selective runtime topology after cleanup failure");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: runtime topology recovery regression: {ex}");
            return 1;
        }
    }

    private static async Task SameConfigurationRebuildsMissingProxyAndVpnAsync()
    {
        var options = CreateOptions();
        var coordinator = new ProxyRuntimeCoordinator(options);
        var proxyMap = GetPrivateField<Dictionary<string, ProxyInstanceRuntime>>(
            coordinator,
            "_proxyById");
        var vpnMap = GetPrivateField<Dictionary<string, VpnLeaseManager>>(
            coordinator,
            "_vpnById");

        var oldProxy = proxyMap["proxy-topology-recovery"];
        var oldVpn = vpnMap["vpn-topology-recovery"];

        // Model the exact degraded shape produced when selective reconfigure first
        // unpublishes affected owners and a later teardown step fails. Desired options
        // still describe both objects, but the runtime maps no longer contain them.
        proxyMap.Remove("proxy-topology-recovery");
        vpnMap.Remove("vpn-topology-recovery");
        await oldProxy.DisposeAsync();
        await oldVpn.DisposeAsync();

        if (coordinator.GetProxySnapshots().Count != 0 ||
            coordinator.GetL2tpSnapshots().Count != 0)
        {
            throw new InvalidOperationException(
                "Self-test could not establish the missing-runtime topology precondition.");
        }

        await coordinator.ReconfigureAsync(options, CancellationToken.None);

        if (!proxyMap.TryGetValue("proxy-topology-recovery", out var recoveredProxy) ||
            !vpnMap.TryGetValue("vpn-topology-recovery", out var recoveredVpn))
        {
            throw new InvalidOperationException(
                "Applying identical desired options did not reconstruct missing proxy/VPN runtime owners.");
        }

        if (ReferenceEquals(recoveredProxy, oldProxy) || ReferenceEquals(recoveredVpn, oldVpn))
        {
            throw new InvalidOperationException(
                "Topology recovery reused an already disposed runtime generation.");
        }

        var proxySnapshot = coordinator.GetProxySnapshots().Single();
        var vpnSnapshot = coordinator.GetL2tpSnapshots().Single();
        if (proxySnapshot.Id != "proxy-topology-recovery" ||
            proxySnapshot.State != ProxyInstanceState.Paused ||
            vpnSnapshot.Id != "vpn-topology-recovery" ||
            vpnSnapshot.ActiveProxyCount != 0)
        {
            throw new InvalidOperationException(
                $"Recovered topology has unexpected state: proxy={proxySnapshot.Id}/{proxySnapshot.State}, " +
                $"vpn={vpnSnapshot.Id}/leases={vpnSnapshot.ActiveProxyCount}.");
        }

        await coordinator.DisposeAsync();
    }

    private static AppOptions CreateOptions() =>
        new()
        {
            Proxies =
            [
                new ProxyOptions
                {
                    Id = "proxy-topology-recovery",
                    Name = "Topology recovery proxy",
                    Enabled = false,
                    ListenAddress = "127.0.0.1",
                    ListenPort = 18331,
                    VpnConnectionId = "vpn-topology-recovery",
                    MaxConcurrentConnections = 8,
                    MaxHeaderBytes = 8192,
                    ClientHeaderTimeoutSeconds = 5,
                    OutboundConnectTimeoutSeconds = 5,
                    DnsTimeoutMilliseconds = 1000
                }
            ],
            VpnConnections =
            [
                new L2tpOptions
                {
                    Id = "vpn-topology-recovery",
                    Name = "Topology recovery VPN",
                    Shared = false,
                    Mode = L2tpConnectionMode.ExistingWindowsProfile,
                    EntryName = "SelfTest-vpn-topology-recovery",
                    MonitorIntervalMilliseconds = 1000,
                    RouteMonitorIntervalMilliseconds = 5000,
                    ReconnectCooldownMilliseconds = 1000,
                    Verification = new VerificationOptions
                    {
                        PublicAddress = "vpn.example.com",
                        ProbeHost = "api.ipify.org",
                        ProbePort = 443,
                        ProbePath = "/",
                        TimeoutSeconds = 5
                    },
                    Keepalive = new KeepaliveOptions
                    {
                        Mode = L2tpKeepaliveMode.Off,
                        IntervalSeconds = 10,
                        TimeoutMilliseconds = 1000,
                        FailureThreshold = 3
                    }
                }
            ]
        };

    private static T GetPrivateField<T>(object owner, string fieldName)
    {
        var field = owner.GetType().GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(owner.GetType().FullName, fieldName);
        return field.GetValue(owner) is T value
            ? value
            : throw new InvalidOperationException(
                $"Runtime field '{fieldName}' did not contain {typeof(T).Name}.");
    }
}
