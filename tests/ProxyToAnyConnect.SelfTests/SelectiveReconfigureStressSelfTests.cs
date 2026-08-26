using System.Reflection;
using ProxyToAnyConnect.Configuration;
using ProxyToAnyConnect.Runtime;

namespace ProxyToAnyConnect.SelfTests;

internal static class SelectiveReconfigureStressSelfTests
{
    private const int Cycles = 250;
    private const int MaximumFixedRetainedRoots = 4;

    public static async Task<int> RunAsync()
    {
        try
        {
            var (proxyReferences, vpnReferences) = await RunCyclesAsync();

            // Forced GC is test-only. Production code must never trim/force GC.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var retainedProxies = proxyReferences.Count(reference => reference.IsAlive);
            var retainedVpns = vpnReferences.Count(reference => reference.IsAlive);
            if (retainedProxies > MaximumFixedRetainedRoots ||
                retainedVpns > MaximumFixedRetainedRoots)
            {
                throw new InvalidOperationException(
                    $"Selective reconfigure retained {retainedProxies}/{Cycles} replaced proxy runtimes " +
                    $"and {retainedVpns}/{Cycles} replaced L2TP runtimes; expected at most " +
                    $"{MaximumFixedRetainedRoots} fixed async/JIT roots of each type.");
            }

            Console.WriteLine(
                $"PASS: {Cycles} selective reconfigure cycles preserve independent group identity " +
                $"with bounded retention (proxy={retainedProxies}, L2TP={retainedVpns})");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: selective reconfigure stress regression: {ex}");
            return 1;
        }
    }

    private static async Task<(WeakReference[] ProxyReferences, WeakReference[] VpnReferences)> RunCyclesAsync()
    {
        var coordinator = new ProxyRuntimeCoordinator(CreateOptions("Proxy A initial", 1000));
        try
        {
            var proxyMap = GetPrivateMap<ProxyInstanceRuntime>(coordinator, "_proxyById");
            var vpnMap = GetPrivateMap<VpnLeaseManager>(coordinator, "_vpnById");
            var stableProxy = proxyMap["proxy-b"];
            var stableVpn = vpnMap["vpn-b"];
            var proxyReferences = new WeakReference[Cycles];
            var vpnReferences = new WeakReference[Cycles];

            for (var cycle = 0; cycle < Cycles; cycle++)
            {
                proxyReferences[cycle] = new WeakReference(proxyMap["proxy-a"]);
                vpnReferences[cycle] = new WeakReference(vpnMap["vpn-a"]);

                var monitorInterval = cycle % 2 == 0 ? 1250 : 1000;
                await coordinator.ReconfigureAsync(
                    CreateOptions($"Proxy A cycle {cycle}", monitorInterval));

                if (!ReferenceEquals(stableProxy, proxyMap["proxy-b"]) ||
                    !ReferenceEquals(stableVpn, vpnMap["vpn-b"]))
                {
                    throw new InvalidOperationException(
                        $"Independent proxy/L2TP group was recreated during cycle {cycle}.");
                }
            }

            return (proxyReferences, vpnReferences);
        }
        finally
        {
            await coordinator.DisposeAsync();
        }
    }

    private static AppOptions CreateOptions(string proxyAName, int vpnAMonitorIntervalMilliseconds) =>
        new()
        {
            Proxies =
            [
                CreateProxy("proxy-a", proxyAName, 18131, "vpn-a"),
                CreateProxy("proxy-b", "Proxy B stable", 18132, "vpn-b")
            ],
            VpnConnections =
            [
                CreateVpn("vpn-a", "VPN A", vpnAMonitorIntervalMilliseconds),
                CreateVpn("vpn-b", "VPN B stable", 1000)
            ]
        };

    private static ProxyOptions CreateProxy(
        string id,
        string name,
        int listenPort,
        string vpnId) =>
        new()
        {
            Id = id,
            Name = name,
            Enabled = false,
            ListenAddress = "127.0.0.1",
            ListenPort = listenPort,
            VpnConnectionId = vpnId,
            MaxConcurrentConnections = 8,
            MaxHeaderBytes = 8192,
            ClientHeaderTimeoutSeconds = 5,
            OutboundConnectTimeoutSeconds = 5,
            DnsTimeoutMilliseconds = 1000
        };

    private static L2tpOptions CreateVpn(
        string id,
        string name,
        int monitorIntervalMilliseconds) =>
        new()
        {
            Id = id,
            Name = name,
            Shared = false,
            Mode = L2tpConnectionMode.ExistingWindowsProfile,
            EntryName = $"Stress-{id}",
            MonitorIntervalMilliseconds = monitorIntervalMilliseconds,
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
        };

    private static Dictionary<string, T> GetPrivateMap<T>(
        ProxyRuntimeCoordinator coordinator,
        string fieldName)
    {
        var field = typeof(ProxyRuntimeCoordinator).GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(ProxyRuntimeCoordinator).FullName, fieldName);

        return field.GetValue(coordinator) as Dictionary<string, T>
            ?? throw new InvalidOperationException(
                $"Runtime field '{fieldName}' did not contain the expected dictionary type.");
    }
}
