using System.Reflection;
using ProxyToAnyConnect.Configuration;
using ProxyToAnyConnect.Runtime;

namespace ProxyToAnyConnect.SelfTests;

internal static class RuntimeHostOperationLifetimeSelfTests
{
    private static readonly TimeSpan ObservationTimeout = TimeSpan.FromSeconds(5);

    public static async Task<int> RunAsync()
    {
        try
        {
            await DisposeCancelsForegroundStartBeforeWaitingForHostGateAsync();
            await DisposeContinuesAfterHostLifetimeCancellationCallbackFailureAsync();

            Console.WriteLine(
                "PASS: runtime host shutdown cancels foreground lifecycle work and releases exact ownership through cancellation callback faults");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: runtime host operation lifetime regression: {ex}");
            return 1;
        }
    }

    private static async Task DisposeCancelsForegroundStartBeforeWaitingForHostGateAsync()
    {
        var host = new ProxyRuntimeHost(CreateOptions());
        var disposed = false;
        var coordinator = host.Current
            ?? throw new InvalidOperationException("Valid self-test options did not create a runtime coordinator.");
        var vpnMap = GetPrivateMap<VpnLeaseManager>(coordinator, "_vpnById");
        var vpnGate = GetPrivateGate(vpnMap["vpn-host"]);

        await vpnGate.WaitAsync();
        try
        {
            var startTask = host.StartProxyAsync("proxy-host", CancellationToken.None);
            await WaitForProxyStateAsync(host, ProxyInstanceState.Starting, startTask);

            var disposeTask = host.DisposeAsync().AsTask();

            var startCompleted = await Task.WhenAny(startTask, Task.Delay(ObservationTimeout));
            if (!ReferenceEquals(startCompleted, startTask))
            {
                throw new TimeoutException(
                    "Runtime host Dispose did not cancel foreground Start before waiting for its host gate.");
            }

            await ExpectCancellationAsync(startTask);

            if (host.ConfigurationError is not null)
            {
                throw new InvalidOperationException(
                    $"Host shutdown cancellation was incorrectly recorded as configuration error: {host.ConfigurationError}");
            }

            // The outer host gate is now free, but coordinator/VPN disposal must
            // still wait for the exact held lower-level ownership to drain.
            if (disposeTask.IsCompleted)
            {
                await disposeTask;
                throw new InvalidOperationException(
                    "Runtime host Dispose completed before the held VPN ownership gate was released.");
            }

            vpnGate.Release();
            await disposeTask.WaitAsync(ObservationTimeout);
            disposed = true;

            if (host.Current is not null)
            {
                throw new InvalidOperationException("Disposed runtime host retained its coordinator.");
            }
        }
        finally
        {
            if (vpnGate.CurrentCount == 0)
            {
                vpnGate.Release();
            }

            if (!disposed)
            {
                await host.DisposeAsync();
            }
        }
    }

    private static async Task DisposeContinuesAfterHostLifetimeCancellationCallbackFailureAsync()
    {
        var host = new ProxyRuntimeHost(CreateOptions());
        var coordinator = host.Current
            ?? throw new InvalidOperationException("Valid self-test options did not create a runtime coordinator.");
        var hostLifetime = GetPrivateField<CancellationTokenSource>(host, "_lifetime");
        var coordinatorLifetime = GetPrivateField<CancellationTokenSource>(coordinator, "_lifetime");
        _ = hostLifetime.Token.Register(
            static () => throw new SyntheticCleanupException("host lifetime cancellation callback failed"));

        try
        {
            await host.DisposeAsync();
            throw new InvalidOperationException(
                "Throwing host lifetime cancellation callback was not surfaced from DisposeAsync.");
        }
        catch (AggregateException ex) when (
            ex.InnerExceptions.Any(inner =>
                inner is SyntheticCleanupException synthetic &&
                synthetic.Message == "host lifetime cancellation callback failed"))
        {
        }

        if (host.Current is not null)
        {
            throw new InvalidOperationException(
                "Host lifetime cancellation callback fault prevented coordinator ownership release.");
        }

        if (!CancellationSourceWasDisposed(hostLifetime) ||
            !CancellationSourceWasDisposed(coordinatorLifetime) ||
            GetPrivateField<int>(coordinator, "_disposed") == 0)
        {
            throw new InvalidOperationException(
                "Host lifetime cancellation callback fault prevented host/coordinator lifetime disposal.");
        }

        // The cleanup defect was already reported by the first call. Once exact
        // ownership is gone, repeated host disposal is a harmless no-op.
        await host.DisposeAsync();
    }

    private static async Task WaitForProxyStateAsync(
        ProxyRuntimeHost host,
        ProxyInstanceState expectedState,
        Task operation)
    {
        var deadline = DateTime.UtcNow + ObservationTimeout;
        while (DateTime.UtcNow < deadline)
        {
            var snapshot = host.GetProxySnapshots().Single(item => item.Id == "proxy-host");
            if (snapshot.State == expectedState)
            {
                return;
            }

            if (operation.IsCompleted)
            {
                await operation;
                throw new InvalidOperationException(
                    $"Foreground host operation completed before proxy reached {expectedState}.");
            }

            await Task.Delay(10);
        }

        throw new TimeoutException($"proxy-host did not reach {expectedState}.");
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

        throw new InvalidOperationException("Expected runtime-host lifecycle cancellation was not propagated.");
    }

    private static AppOptions CreateOptions() =>
        new()
        {
            Proxies =
            [
                new ProxyOptions
                {
                    Id = "proxy-host",
                    Name = "Host lifecycle proxy",
                    Enabled = true,
                    ListenAddress = "127.0.0.1",
                    ListenPort = 18311,
                    VpnConnectionId = "vpn-host",
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
                    Id = "vpn-host",
                    Name = "Host lifecycle VPN",
                    Shared = false,
                    Mode = L2tpConnectionMode.ExistingWindowsProfile,
                    EntryName = "SelfTest-vpn-host",
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

    private static T GetPrivateField<T>(object owner, string fieldName)
    {
        var field = owner.GetType().GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(owner.GetType().FullName, fieldName);
        var value = field.GetValue(owner);
        if (value is null)
        {
            return default!;
        }

        return (T)value;
    }

    private static SemaphoreSlim GetPrivateGate(VpnLeaseManager manager) =>
        GetPrivateField<SemaphoreSlim>(manager, "_gate");

    private static bool CancellationSourceWasDisposed(CancellationTokenSource source)
    {
        try
        {
            _ = source.Token;
            return false;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }

    private sealed class SyntheticCleanupException : Exception
    {
        public SyntheticCleanupException(string message)
            : base(message)
        {
        }
    }
}
