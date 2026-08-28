using System.Net;
using System.Reflection;
using ProxyToAnyConnect.Configuration;
using ProxyToAnyConnect.Runtime;
using ProxyToAnyConnect.Vpn;

namespace ProxyToAnyConnect.SelfTests;

internal static class TerminalRuntimeCleanupRetrySelfTests
{
    public static async Task<int> RunAsync()
    {
        try
        {
            await CoordinatorRetainsExactVpnOwnerUntilRetryAsync();
            await HostRetainsExactCoordinatorUntilRetryAsync();

            Console.WriteLine(
                "PASS: terminal runtime disposal retains exact failed VPN/coordinator cleanup owners until serialized retry succeeds");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: terminal runtime cleanup retry regression: {ex}");
            return 1;
        }
    }

    private static async Task CoordinatorRetainsExactVpnOwnerUntilRetryAsync()
    {
        var prepared = await CreateCoordinatorWithRetryingVpnAsync("coordinator-terminal-retry");
        var coordinator = prepared.Coordinator;

        try
        {
            await coordinator.DisposeAsync();
            throw new InvalidOperationException(
                "First terminal coordinator disposal unexpectedly succeeded despite synthetic VPN cleanup failure.");
        }
        catch (SyntheticCleanupException ex) when (ex.Message == "synthetic controller dispose failed once")
        {
        }

        if (prepared.Controller.DisposeCount != 1)
        {
            throw new InvalidOperationException(
                $"Expected one failed nested controller disposal, got {prepared.Controller.DisposeCount}.");
        }

        var residual = GetPrivateField<VpnLeaseManager[]>(coordinator, "_terminalCleanupVpns");
        if (residual.Length != 1 || !ReferenceEquals(residual[0], prepared.VpnManager))
        {
            throw new InvalidOperationException(
                "Coordinator did not retain the exact failed VPN manager as cleanup-only terminal ownership.");
        }

        await ExpectDisposedAsync(() => coordinator.StartEnabledAsync());

        await coordinator.DisposeAsync();
        if (prepared.Controller.DisposeCount != 2)
        {
            throw new InvalidOperationException(
                $"Second coordinator DisposeAsync did not retry the exact controller once; count={prepared.Controller.DisposeCount}.");
        }

        if (GetPrivateField<VpnLeaseManager[]>(coordinator, "_terminalCleanupVpns").Length != 0 ||
            GetPrivateField<int>(coordinator, "_terminalCleanupCompleted") == 0)
        {
            throw new InvalidOperationException(
                "Successful coordinator retry did not clear residual terminal cleanup ownership.");
        }

        await coordinator.DisposeAsync();
        if (prepared.Controller.DisposeCount != 2)
        {
            throw new InvalidOperationException(
                "Idempotent coordinator disposal retried an already-terminal VPN owner.");
        }
    }

    private static async Task HostRetainsExactCoordinatorUntilRetryAsync()
    {
        var options = CreateOptions("host-terminal-retry");
        var host = new ProxyRuntimeHost(options);
        var original = GetPrivateField<ProxyRuntimeCoordinator?>(host, "_current")
            ?? throw new InvalidOperationException("Runtime host did not create its initial coordinator.");
        await original.DisposeAsync();

        var prepared = await CreateCoordinatorWithRetryingVpnAsync("host-terminal-retry-replacement");
        SetPrivateField(host, "_current", prepared.Coordinator);

        try
        {
            await host.DisposeAsync();
            throw new InvalidOperationException(
                "First terminal host disposal unexpectedly succeeded despite synthetic coordinator cleanup failure.");
        }
        catch (SyntheticCleanupException ex) when (ex.Message == "synthetic controller dispose failed once")
        {
        }

        if (host.Current is not null)
        {
            throw new InvalidOperationException(
                "Disposed runtime host exposed its retained cleanup-only coordinator as a usable Current runtime.");
        }

        var retained = GetPrivateField<ProxyRuntimeCoordinator?>(host, "_current");
        if (!ReferenceEquals(retained, prepared.Coordinator) || prepared.Controller.DisposeCount != 1)
        {
            throw new InvalidOperationException(
                "Runtime host did not retain the exact failed coordinator for terminal cleanup retry.");
        }

        await ExpectDisposedAsync(() => host.StartEnabledAsync());

        await host.DisposeAsync();
        if (prepared.Controller.DisposeCount != 2 ||
            GetPrivateField<ProxyRuntimeCoordinator?>(host, "_current") is not null ||
            GetPrivateField<int>(host, "_terminalCleanupCompleted") == 0)
        {
            throw new InvalidOperationException(
                "Second host DisposeAsync did not complete and unpublish the exact retained coordinator.");
        }

        await host.DisposeAsync();
        if (prepared.Controller.DisposeCount != 2)
        {
            throw new InvalidOperationException(
                "Idempotent host disposal retried an already-terminal coordinator.");
        }
    }

    private static async Task<PreparedRuntime> CreateCoordinatorWithRetryingVpnAsync(string id)
    {
        var options = CreateOptions(id);
        var coordinator = new ProxyRuntimeCoordinator(options);
        var vpnMap = GetPrivateField<Dictionary<string, VpnLeaseManager>>(coordinator, "_vpnById");
        var original = vpnMap[id];
        await original.DisposeAsync();

        var controller = new RetryOnceController();
        var replacement = new VpnLeaseManager(
            options.VpnConnections[0],
            controller,
            TimeSpan.FromMinutes(5));
        vpnMap[id] = replacement;
        return new PreparedRuntime(coordinator, replacement, controller);
    }

    private static async Task ExpectDisposedAsync(Func<Task> action)
    {
        try
        {
            await action();
            throw new InvalidOperationException("Disposed runtime API unexpectedly remained usable.");
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static AppOptions CreateOptions(string id) =>
        new()
        {
            Proxies =
            [
                new ProxyOptions
                {
                    Id = $"proxy-{id}",
                    Name = $"proxy-{id}",
                    Enabled = false,
                    ListenAddress = "127.0.0.1",
                    ListenPort = 18431,
                    VpnConnectionId = id,
                    MaxConcurrentConnections = 4,
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
                    Id = id,
                    Name = id,
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
                }
            ]
        };

    private static T GetPrivateField<T>(object owner, string fieldName)
    {
        var field = owner.GetType().GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(owner.GetType().FullName, fieldName);
        var value = field.GetValue(owner);
        return value is null ? default! : (T)value;
    }

    private static void SetPrivateField(object owner, string fieldName, object? value)
    {
        var field = owner.GetType().GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(owner.GetType().FullName, fieldName);
        field.SetValue(owner, value);
    }

    private sealed class RetryOnceController : IVpnConnectionController
    {
        private int _disposeCount;

        public int DisposeCount => Volatile.Read(ref _disposeCount);
        public VpnContext? Current => null;
        public VpnConnectionState State => VpnConnectionState.Disconnected;

        public Task<VpnContext> ConnectAsync(CancellationToken cancellationToken) =>
            Task.FromException<VpnContext>(new InvalidOperationException("Synthetic controller must not connect."));

        public Task DisconnectAsync() => Task.CompletedTask;

        public ValueTask DisposeAsync()
        {
            var count = Interlocked.Increment(ref _disposeCount);
            return count == 1
                ? ValueTask.FromException(new SyntheticCleanupException("synthetic controller dispose failed once"))
                : ValueTask.CompletedTask;
        }
    }

    private sealed class SyntheticCleanupException : Exception
    {
        public SyntheticCleanupException(string message)
            : base(message)
        {
        }
    }

    private readonly record struct PreparedRuntime(
        ProxyRuntimeCoordinator Coordinator,
        VpnLeaseManager VpnManager,
        RetryOnceController Controller);
}
