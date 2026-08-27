using System.Reflection;
using ProxyToAnyConnect.Configuration;
using ProxyToAnyConnect.Runtime;

namespace ProxyToAnyConnect.SelfTests;

internal static class InitialDesiredStartReconciliationSelfTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public static async Task<int> RunAsync()
    {
        try
        {
            await CancelledInitialStartRetriesOnIdenticalConfigurationAsync();
            Console.WriteLine(
                "PASS: interrupted initial enabled starts remain pending and reconcile on identical configuration");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: initial desired-start reconciliation regression: {ex}");
            return 1;
        }
    }

    private static async Task CancelledInitialStartRetriesOnIdenticalConfigurationAsync()
    {
        var enabled = CreateOptions(enabled: true);
        await using var coordinator = new ProxyRuntimeCoordinator(enabled);
        var vpnMap = GetPrivateField<Dictionary<string, VpnLeaseManager>>(coordinator, "_vpnById");
        var pending = GetPrivateField<HashSet<string>>(coordinator, "_pendingStartProxyIds");
        var vpnGate = GetPrivateField<SemaphoreSlim>(vpnMap["vpn-a"], "_gate");

        await vpnGate.WaitAsync();
        try
        {
            using var cancellation = new CancellationTokenSource();
            var startTask = coordinator.StartEnabledAsync(cancellation.Token);
            await WaitForStateAsync(coordinator, ProxyInstanceState.Starting, startTask);
            cancellation.Cancel();
            await ExpectCancellationAsync(startTask, cancellation.Token);
        }
        finally
        {
            vpnGate.Release();
        }

        if (!pending.Contains("proxy-a"))
        {
            throw new InvalidOperationException(
                "Cancelled initial enabled start was not retained as pending desired state.");
        }

        // There is deliberately no configuration diff here. The only reason this
        // second operation should attempt a start is the pending marker produced by
        // StartEnabledAsync above.
        await vpnGate.WaitAsync();
        try
        {
            using var cancellation = new CancellationTokenSource();
            var retryTask = coordinator.ReconfigureAsync(enabled, cancellation.Token);
            await WaitForStateAsync(coordinator, ProxyInstanceState.Starting, retryTask);
            cancellation.Cancel();
            await ExpectCancellationAsync(retryTask, cancellation.Token);
        }
        finally
        {
            vpnGate.Release();
        }

        if (!pending.Contains("proxy-a"))
        {
            throw new InvalidOperationException(
                "Repeatedly cancelled same-config retry unexpectedly cleared pending desired state.");
        }

        await coordinator.ReconfigureAsync(CreateOptions(enabled: false));

        if (pending.Contains("proxy-a"))
        {
            throw new InvalidOperationException(
                "Disabling the proxy did not clear its pending desired start.");
        }

        var snapshot = coordinator.GetProxySnapshots().Single(item => item.Id == "proxy-a");
        if (snapshot.State != ProxyInstanceState.Paused)
        {
            throw new InvalidOperationException(
                $"Disabled proxy was not left Paused after desired-state reconciliation: {snapshot.State}.");
        }
    }

    private static async Task WaitForStateAsync(
        ProxyRuntimeCoordinator coordinator,
        ProxyInstanceState expected,
        Task operation)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < deadline)
        {
            var state = coordinator.GetProxySnapshots().Single(item => item.Id == "proxy-a").State;
            if (state == expected)
            {
                return;
            }

            if (operation.IsCompleted)
            {
                await operation;
                throw new InvalidOperationException(
                    $"Operation completed before proxy reached {expected}.");
            }

            await Task.Delay(10);
        }

        throw new TimeoutException($"Proxy did not reach {expected}.");
    }

    private static async Task ExpectCancellationAsync(Task operation, CancellationToken token)
    {
        try
        {
            await operation;
        }
        catch (OperationCanceledException ex) when (
            token.IsCancellationRequested &&
            ex.CancellationToken == token)
        {
            return;
        }

        throw new InvalidOperationException(
            "Desired-start operation did not preserve caller cancellation.");
    }

    private static AppOptions CreateOptions(bool enabled) =>
        new()
        {
            Proxies =
            [
                new ProxyOptions
                {
                    Id = "proxy-a",
                    Name = "Initial desired-state proxy",
                    Enabled = enabled,
                    ListenAddress = "127.0.0.1",
                    ListenPort = 18181,
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
                    Name = "Initial desired-state VPN",
                    Shared = false,
                    Mode = L2tpConnectionMode.ExistingWindowsProfile,
                    EntryName = "SelfTest-Initial-Desired-State",
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
        where T : class
    {
        var field = owner.GetType().GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(owner.GetType().FullName, fieldName);

        return field.GetValue(owner) as T
            ?? throw new InvalidOperationException(
                $"Field '{fieldName}' did not contain {typeof(T).Name}.");
    }
}
