using System.Reflection;
using ProxyToAnyConnect.Configuration;
using ProxyToAnyConnect.Runtime;

namespace ProxyToAnyConnect.SelfTests;

internal static class CoordinatorOperationLifetimeSelfTests
{
    private static readonly TimeSpan ObservationTimeout = TimeSpan.FromSeconds(5);

    public static async Task<int> RunAsync()
    {
        try
        {
            await ForegroundStartSerializesReconfigureAsync();
            await DisposeCancelsPendingForegroundStartAsync();

            Console.WriteLine(
                "PASS: coordinator lifecycle serializes runtime generations and cancels pending starts on dispose");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: coordinator operation lifetime regression: {ex}");
            return 1;
        }
    }

    private static async Task ForegroundStartSerializesReconfigureAsync()
    {
        var initial = CreateOptions(enabled: true, proxyName: "Proxy A");
        await using var coordinator = new ProxyRuntimeCoordinator(initial);

        var proxyMap = GetPrivateMap<ProxyInstanceRuntime>(coordinator, "_proxyById");
        var vpnMap = GetPrivateMap<VpnLeaseManager>(coordinator, "_vpnById");
        var originalProxy = proxyMap["proxy-a"];
        var vpnGate = GetPrivateGate(vpnMap["vpn-a"]);

        await vpnGate.WaitAsync();
        try
        {
            using var startCancellation = new CancellationTokenSource();
            var startTask = coordinator.StartProxyAsync("proxy-a", startCancellation.Token);
            await WaitForProxyStateAsync(
                coordinator,
                ProxyInstanceState.Starting,
                startTask);

            var changed = CreateOptions(enabled: false, proxyName: "Proxy A disabled");
            var reconfigureTask = coordinator.ReconfigureAsync(changed);

            // Reconfigure must wait outside the current generation while a foreground
            // Start owns the coordinator operation gate. Before this fix it removed
            // proxy-a from the map first and then blocked trying to dispose that stale
            // generation while Start still owned its per-runtime gate.
            await Task.Delay(100);
            if (reconfigureTask.IsCompleted)
            {
                await reconfigureTask;
                throw new InvalidOperationException(
                    "Reconfigure completed while a foreground Start generation was still pending.");
            }

            if (!proxyMap.TryGetValue("proxy-a", out var mappedProxy) ||
                !ReferenceEquals(mappedProxy, originalProxy))
            {
                throw new InvalidOperationException(
                    "Concurrent reconfigure removed/replaced the runtime generation still owned by foreground Start.");
            }

            startCancellation.Cancel();
            await ExpectCancellationAsync(startTask);
            await reconfigureTask.WaitAsync(ObservationTimeout);

            if (!proxyMap.TryGetValue("proxy-a", out var replacement) ||
                ReferenceEquals(replacement, originalProxy))
            {
                throw new InvalidOperationException(
                    "Reconfigure did not replace the cancelled old proxy generation after serialization released it.");
            }

            var snapshot = coordinator.GetProxySnapshots().Single(item => item.Id == "proxy-a");
            if (snapshot.State != ProxyInstanceState.Paused || snapshot.Name != "Proxy A disabled")
            {
                throw new InvalidOperationException(
                    $"Serialized reconfigure produced unexpected replacement state/name: {snapshot.State}, '{snapshot.Name}'.");
            }
        }
        finally
        {
            vpnGate.Release();
        }
    }

    private static async Task DisposeCancelsPendingForegroundStartAsync()
    {
        var coordinator = new ProxyRuntimeCoordinator(
            CreateOptions(enabled: true, proxyName: "Proxy A dispose race"));
        var disposed = false;
        var vpnMap = GetPrivateMap<VpnLeaseManager>(coordinator, "_vpnById");
        var vpnGate = GetPrivateGate(vpnMap["vpn-a"]);

        await vpnGate.WaitAsync();
        try
        {
            var startTask = coordinator.StartProxyAsync("proxy-a", CancellationToken.None);
            await WaitForProxyStateAsync(
                coordinator,
                ProxyInstanceState.Starting,
                startTask);

            var disposeTask = coordinator.DisposeAsync().AsTask();

            var startCompleted = await Task.WhenAny(
                startTask,
                Task.Delay(ObservationTimeout));
            if (!ReferenceEquals(startCompleted, startTask))
            {
                throw new TimeoutException(
                    "Coordinator Dispose did not cancel a foreground Start blocked in VPN acquisition.");
            }

            await ExpectCancellationAsync(startTask);

            // Dispose itself must still wait for exact lower-level ownership to drain;
            // the test deliberately owns the VPN gate until this assertion completes.
            if (disposeTask.IsCompleted)
            {
                await disposeTask;
                throw new InvalidOperationException(
                    "Coordinator Dispose completed before the held VPN ownership gate was released.");
            }

            vpnGate.Release();
            await disposeTask.WaitAsync(ObservationTimeout);
            disposed = true;
        }
        finally
        {
            if (vpnGate.CurrentCount == 0)
            {
                vpnGate.Release();
            }

            if (!disposed)
            {
                await coordinator.DisposeAsync();
            }
        }
    }

    private static async Task WaitForProxyStateAsync(
        ProxyRuntimeCoordinator coordinator,
        ProxyInstanceState expectedState,
        Task operation)
    {
        var deadline = DateTime.UtcNow + ObservationTimeout;
        while (DateTime.UtcNow < deadline)
        {
            var snapshot = coordinator.GetProxySnapshots().Single(item => item.Id == "proxy-a");
            if (snapshot.State == expectedState)
            {
                return;
            }

            if (operation.IsCompleted)
            {
                await operation;
                throw new InvalidOperationException(
                    $"Operation completed before proxy-a reached {expectedState}.");
            }

            await Task.Delay(10);
        }

        throw new TimeoutException($"proxy-a did not reach {expectedState}.");
    }

    private static async Task ExpectCancellationAsync(Task operation)
    {
        try
        {
            await operation;
        }
        catch (OperationCanceledException)
        {
            return;
        }

        throw new InvalidOperationException("Expected lifecycle operation cancellation was not propagated.");
    }

    private static AppOptions CreateOptions(bool enabled, string proxyName) =>
        new()
        {
            Proxies =
            [
                new ProxyOptions
                {
                    Id = "proxy-a",
                    Name = proxyName,
                    Enabled = enabled,
                    ListenAddress = "127.0.0.1",
                    ListenPort = 18241,
                    VpnConnectionId = "vpn-a",
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
                    Id = "vpn-a",
                    Name = "VPN A",
                    Shared = false,
                    Mode = L2tpConnectionMode.ExistingWindowsProfile,
                    EntryName = "SelfTest-vpn-a",
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

    private static Dictionary<string, T> GetPrivateMap<T>(
        ProxyRuntimeCoordinator coordinator,
        string fieldName) =>
        GetPrivateField<Dictionary<string, T>>(coordinator, fieldName);

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
