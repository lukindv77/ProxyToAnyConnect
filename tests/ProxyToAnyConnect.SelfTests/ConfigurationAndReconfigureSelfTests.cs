using System.Net;
using System.Reflection;
using ProxyToAnyConnect.Configuration;
using ProxyToAnyConnect.Runtime;

namespace ProxyToAnyConnect.SelfTests;

internal static class ConfigurationAndReconfigureSelfTests
{
    public static async Task<int> RunAsync()
    {
        try
        {
            EquivalentIpv4ListenerEndpointsAreRejected();
            await SelectiveReconfigurePreservesIndependentRuntimeGroupAsync();

            Console.WriteLine("PASS: canonical listener validation and selective reconfigure isolation");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: configuration/reconfigure regression: {ex}");
            return 1;
        }
    }

    private static void EquivalentIpv4ListenerEndpointsAreRejected()
    {
        if (!IPAddress.TryParse("127.1", out var compactLoopback) ||
            !compactLoopback.Equals(IPAddress.Loopback))
        {
            throw new InvalidOperationException(
                "Runtime no longer accepts the compact IPv4 form used by this collision regression.");
        }

        var options = new AppOptions
        {
            Proxies =
            [
                CreateProxy("proxy-a", "Proxy A", "127.0.0.1", 18120, "vpn-shared", enabled: true),
                CreateProxy("proxy-b", "Proxy B", "127.1", 18120, "vpn-shared", enabled: true)
            ],
            VpnConnections =
            [
                CreateVpn("vpn-shared", "Shared VPN", shared: true, monitorIntervalMilliseconds: 1000)
            ]
        };

        try
        {
            options.Validate();
        }
        catch (InvalidOperationException ex) when (
            ex.Message.Contains("same listener endpoint", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw new InvalidOperationException(
            "Equivalent IPv4 spellings were not rejected as the same enabled listener endpoint.");
    }

    private static async Task SelectiveReconfigurePreservesIndependentRuntimeGroupAsync()
    {
        var initial = CreateTwoGroupOptions(proxyAName: "Proxy A", vpnAMonitorIntervalMilliseconds: 1000);
        await using var coordinator = new ProxyRuntimeCoordinator(initial);

        var proxyMap = GetPrivateMap<ProxyInstanceRuntime>(coordinator, "_proxyById");
        var vpnMap = GetPrivateMap<VpnLeaseManager>(coordinator, "_vpnById");

        var proxyABefore = proxyMap["proxy-a"];
        var proxyBBefore = proxyMap["proxy-b"];
        var vpnABefore = vpnMap["vpn-a"];
        var vpnBBefore = vpnMap["vpn-b"];

        var proxyOnlyChange = CreateTwoGroupOptions(
            proxyAName: "Proxy A renamed",
            vpnAMonitorIntervalMilliseconds: 1000);
        await coordinator.ReconfigureAsync(proxyOnlyChange);

        var proxyAAfterProxyChange = proxyMap["proxy-a"];
        if (ReferenceEquals(proxyABefore, proxyAAfterProxyChange))
        {
            throw new InvalidOperationException("Changed proxy runtime was not replaced.");
        }

        if (!ReferenceEquals(proxyBBefore, proxyMap["proxy-b"]))
        {
            throw new InvalidOperationException(
                "Unchanged independent proxy runtime was recreated by an unrelated proxy edit.");
        }

        if (!ReferenceEquals(vpnABefore, vpnMap["vpn-a"]) ||
            !ReferenceEquals(vpnBBefore, vpnMap["vpn-b"]))
        {
            throw new InvalidOperationException(
                "A proxy-only edit unexpectedly recreated an unchanged L2TP runtime.");
        }

        var vpnAAfterProxyChange = vpnMap["vpn-a"];
        var vpnOnlyChange = CreateTwoGroupOptions(
            proxyAName: "Proxy A renamed",
            vpnAMonitorIntervalMilliseconds: 1500);
        await coordinator.ReconfigureAsync(vpnOnlyChange);

        if (ReferenceEquals(proxyAAfterProxyChange, proxyMap["proxy-a"]))
        {
            throw new InvalidOperationException(
                "Proxy depending on a changed L2TP runtime was not replaced.");
        }

        if (ReferenceEquals(vpnAAfterProxyChange, vpnMap["vpn-a"]))
        {
            throw new InvalidOperationException("Changed L2TP runtime was not replaced.");
        }

        if (!ReferenceEquals(proxyBBefore, proxyMap["proxy-b"]) ||
            !ReferenceEquals(vpnBBefore, vpnMap["vpn-b"]))
        {
            throw new InvalidOperationException(
                "Independent proxy/L2TP group was recreated by an unrelated L2TP edit.");
        }
    }

    private static AppOptions CreateTwoGroupOptions(
        string proxyAName,
        int vpnAMonitorIntervalMilliseconds) =>
        new()
        {
            Proxies =
            [
                CreateProxy("proxy-a", proxyAName, "127.0.0.1", 18121, "vpn-a", enabled: false),
                CreateProxy("proxy-b", "Proxy B", "127.0.0.1", 18122, "vpn-b", enabled: false)
            ],
            VpnConnections =
            [
                CreateVpn("vpn-a", "VPN A", shared: false, vpnAMonitorIntervalMilliseconds),
                CreateVpn("vpn-b", "VPN B", shared: false, monitorIntervalMilliseconds: 1000)
            ]
        };

    private static ProxyOptions CreateProxy(
        string id,
        string name,
        string listenAddress,
        int listenPort,
        string vpnId,
        bool enabled) =>
        new()
        {
            Id = id,
            Name = name,
            Enabled = enabled,
            ListenAddress = listenAddress,
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
        bool shared,
        int monitorIntervalMilliseconds) =>
        new()
        {
            Id = id,
            Name = name,
            Shared = shared,
            Mode = L2tpConnectionMode.ExistingWindowsProfile,
            EntryName = $"SelfTest-{id}",
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
