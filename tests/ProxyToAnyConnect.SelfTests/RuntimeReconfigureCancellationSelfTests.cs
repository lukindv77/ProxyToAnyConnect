using System.Reflection;
using ProxyToAnyConnect.Configuration;
using ProxyToAnyConnect.Runtime;

namespace ProxyToAnyConnect.SelfTests;

internal static class RuntimeReconfigureCancellationSelfTests
{
    public static async Task<int> RunAsync()
    {
        try
        {
            await StartEnabledPropagatesCallerCancellationAsync();
            await ReconfigureCancellationPreservesIndependentGroupAndRetriesPendingStartAsync();
            await RuntimeHostDoesNotReportCallerCancellationAsConfigurationErrorAsync();

            Console.WriteLine(
                "PASS: runtime start/reconfigure cancellation propagates, preserves independent groups and retries pending desired starts");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: runtime reconfigure cancellation regression: {ex}");
            return 1;
        }
    }

    private static async Task StartEnabledPropagatesCallerCancellationAsync()
    {
        var options = CreateTwoGroupOptions(proxyAEnabled: true, proxyAName: "Proxy A");
        await using var coordinator = new ProxyRuntimeCoordinator(options);

        var vpnMap = GetPrivateMap<VpnLeaseManager>(coordinator, "_vpnById");
        var vpnGate = GetPrivateGate(vpnMap["vpn-a"]);
        await vpnGate.WaitAsync();
        try
        {
            using var cancellation = new CancellationTokenSource();
            var startTask = coordinator.StartEnabledAsync(cancellation.Token);

            await WaitForProxyStateAsync(
                coordinator.GetProxySnapshots,
                "proxy-a",
                ProxyInstanceState.Starting,
                startTask);

            cancellation.Cancel();
            await ExpectCallerCancellationAsync(startTask, cancellation.Token);
        }
        finally
        {
            vpnGate.Release();
        }
    }

    private static async Task ReconfigureCancellationPreservesIndependentGroupAndRetriesPendingStartAsync()
    {
        var initial = CreateTwoGroupOptions(proxyAEnabled: false, proxyAName: "Proxy A");
        await using var coordinator = new ProxyRuntimeCoordinator(initial);

        var proxyMap = GetPrivateMap<ProxyInstanceRuntime>(coordinator, "_proxyById");
        var vpnMap = GetPrivateMap<VpnLeaseManager>(coordinator, "_vpnById");
        var pendingStarts = GetPendingStartSet(coordinator);

        var proxyBBefore = proxyMap["proxy-b"];
        var vpnABefore = vpnMap["vpn-a"];
        var vpnBBefore = vpnMap["vpn-b"];
        var vpnGate = GetPrivateGate(vpnABefore);

        var enabledA = CreateTwoGroupOptions(proxyAEnabled: true, proxyAName: "Proxy A enabled");

        await vpnGate.WaitAsync();
        try
        {
            using var cancellation = new CancellationTokenSource();
            var reconfigureTask = coordinator.ReconfigureAsync(enabledA, cancellation.Token);

            await WaitForProxyStateAsync(
                coordinator.GetProxySnapshots,
                "proxy-a",
                ProxyInstanceState.Starting,
                reconfigureTask);

            cancellation.Cancel();
            await ExpectCallerCancellationAsync(reconfigureTask, cancellation.Token);
        }
        finally
        {
            vpnGate.Release();
        }

        AssertIndependentGroupIdentity(proxyMap, vpnMap, proxyBBefore, vpnBBefore);
        if (!ReferenceEquals(vpnABefore, vpnMap["vpn-a"]))
        {
            throw new InvalidOperationException(
                "Proxy-only interrupted reconfigure unexpectedly recreated its unchanged L2TP runtime.");
        }

        if (!pendingStarts.Contains("proxy-a"))
        {
            throw new InvalidOperationException(
                "Cancelled desired proxy start was not retained for later reconciliation.");
        }

        // Applying the same options again has no configuration diff. It must still retry the
        // unfinished desired start instead of returning a false success. Hold the unchanged VPN
        // gate so the retry can be observed and cancelled before any real RAS work begins.
        await vpnGate.WaitAsync();
        try
        {
            using var retryCancellation = new CancellationTokenSource();
            var retryTask = coordinator.ReconfigureAsync(enabledA, retryCancellation.Token);

            await WaitForProxyStateAsync(
                coordinator.GetProxySnapshots,
                "proxy-a",
                ProxyInstanceState.Starting,
                retryTask);

            retryCancellation.Cancel();
            await ExpectCallerCancellationAsync(retryTask, retryCancellation.Token);
        }
        finally
        {
            vpnGate.Release();
        }

        AssertIndependentGroupIdentity(proxyMap, vpnMap, proxyBBefore, vpnBBefore);
        if (!pendingStarts.Contains("proxy-a"))
        {
            throw new InvalidOperationException(
                "Repeatedly cancelled desired start was unexpectedly removed from pending reconciliation.");
        }

        // Disabling the affected proxy is a successful follow-up reconfigure that needs no RAS.
        // It must clear the pending start and leave the independent group untouched.
        var disabledA = CreateTwoGroupOptions(proxyAEnabled: false, proxyAName: "Proxy A disabled again");
        await coordinator.ReconfigureAsync(disabledA);

        if (pendingStarts.Contains("proxy-a"))
        {
            throw new InvalidOperationException("Disabling a proxy did not clear its pending desired start.");
        }

        var proxyA = coordinator.GetProxySnapshots().Single(snapshot => snapshot.Id == "proxy-a");
        if (proxyA.State != ProxyInstanceState.Paused)
        {
            throw new InvalidOperationException(
                $"Disabled proxy was not left Paused after recovery reconfigure: {proxyA.State}.");
        }

        AssertIndependentGroupIdentity(proxyMap, vpnMap, proxyBBefore, vpnBBefore);
    }

    private static async Task RuntimeHostDoesNotReportCallerCancellationAsConfigurationErrorAsync()
    {
        var initial = CreateTwoGroupOptions(proxyAEnabled: false, proxyAName: "Proxy A");
        await using var host = new ProxyRuntimeHost(initial);
        var coordinator = host.Current
            ?? throw new InvalidOperationException("Runtime host did not create the valid initial coordinator.");

        var vpnMap = GetPrivateMap<VpnLeaseManager>(coordinator, "_vpnById");
        var vpnGate = GetPrivateGate(vpnMap["vpn-a"]);
        var enabledA = CreateTwoGroupOptions(proxyAEnabled: true, proxyAName: "Proxy A host apply");

        await vpnGate.WaitAsync();
        try
        {
            using var cancellation = new CancellationTokenSource();
            var applyTask = host.ApplyOptionsAsync(enabledA, cancellation.Token);

            await WaitForProxyStateAsync(
                host.GetProxySnapshots,
                "proxy-a",
                ProxyInstanceState.Starting,
                applyTask);

            cancellation.Cancel();
            await ExpectCallerCancellationAsync(applyTask, cancellation.Token);
        }
        finally
        {
            vpnGate.Release();
        }

        if (host.ConfigurationError is not null)
        {
            throw new InvalidOperationException(
                $"Caller cancellation was incorrectly exposed as a configuration error: {host.ConfigurationError}");
        }

        // Recover with a disabled config so no external RAS dependency is needed.
        await host.ApplyOptionsAsync(
            CreateTwoGroupOptions(proxyAEnabled: false, proxyAName: "Proxy A host recovered"));

        if (host.ConfigurationError is not null)
        {
            throw new InvalidOperationException(
                $"Runtime host did not remain usable after cancelled apply: {host.ConfigurationError}");
        }
    }

    private static async Task WaitForProxyStateAsync(
        Func<IReadOnlyList<ProxyRuntimeSnapshot>> snapshots,
        string proxyId,
        ProxyInstanceState expectedState,
        Task operation)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var snapshot = snapshots().Single(item => item.Id == proxyId);
            if (snapshot.State == expectedState)
            {
                return;
            }

            if (operation.IsCompleted)
            {
                await operation;
                throw new InvalidOperationException(
                    $"Operation completed before proxy '{proxyId}' reached {expectedState}.");
            }

            await Task.Delay(10);
        }

        throw new TimeoutException(
            $"Proxy '{proxyId}' did not reach {expectedState} while the operation was in progress.");
    }

    private static async Task ExpectCallerCancellationAsync(Task operation, CancellationToken cancellationToken)
    {
        try
        {
            await operation;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        throw new InvalidOperationException("Caller cancellation was swallowed by the runtime operation.");
    }

    private static void AssertIndependentGroupIdentity(
        Dictionary<string, ProxyInstanceRuntime> proxyMap,
        Dictionary<string, VpnLeaseManager> vpnMap,
        ProxyInstanceRuntime expectedProxyB,
        VpnLeaseManager expectedVpnB)
    {
        if (!ReferenceEquals(expectedProxyB, proxyMap["proxy-b"]) ||
            !ReferenceEquals(expectedVpnB, vpnMap["vpn-b"]))
        {
            throw new InvalidOperationException(
                "Interrupted/failed work in proxy group A recreated independent proxy/L2TP group B.");
        }
    }

    private static AppOptions CreateTwoGroupOptions(bool proxyAEnabled, string proxyAName) =>
        new()
        {
            Proxies =
            [
                CreateProxy("proxy-a", proxyAName, 18131, "vpn-a", proxyAEnabled),
                CreateProxy("proxy-b", "Proxy B", 18132, "vpn-b", enabled: false)
            ],
            VpnConnections =
            [
                CreateVpn("vpn-a", "VPN A"),
                CreateVpn("vpn-b", "VPN B")
            ]
        };

    private static ProxyOptions CreateProxy(
        string id,
        string name,
        int listenPort,
        string vpnId,
        bool enabled) =>
        new()
        {
            Id = id,
            Name = name,
            Enabled = enabled,
            ListenAddress = "127.0.0.1",
            ListenPort = listenPort,
            VpnConnectionId = vpnId,
            MaxConcurrentConnections = 8,
            MaxHeaderBytes = 8192,
            ClientHeaderTimeoutSeconds = 5,
            OutboundConnectTimeoutSeconds = 5,
            DnsTimeoutMilliseconds = 1000
        };

    private static L2tpOptions CreateVpn(string id, string name) =>
        new()
        {
            Id = id,
            Name = name,
            Shared = false,
            Mode = L2tpConnectionMode.ExistingWindowsProfile,
            EntryName = $"SelfTest-{id}",
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
        };

    private static Dictionary<string, T> GetPrivateMap<T>(
        ProxyRuntimeCoordinator coordinator,
        string fieldName) =>
        GetPrivateField<Dictionary<string, T>>(coordinator, fieldName);

    private static HashSet<string> GetPendingStartSet(ProxyRuntimeCoordinator coordinator) =>
        GetPrivateField<HashSet<string>>(coordinator, "_pendingStartProxyIds");

    private static SemaphoreSlim GetPrivateGate(VpnLeaseManager manager) =>
        GetPrivateField<SemaphoreSlim>(manager, "_gate");

    private static T GetPrivateField<T>(object instance, string fieldName)
        where T : class
    {
        var field = instance.GetType().GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(instance.GetType().FullName, fieldName);

        return field.GetValue(instance) as T
            ?? throw new InvalidOperationException(
                $"Runtime field '{fieldName}' did not contain {typeof(T).Name}.");
    }
}
