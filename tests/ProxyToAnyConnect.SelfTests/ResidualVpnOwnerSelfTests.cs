using System.Net;
using System.Reflection;
using ProxyToAnyConnect.Configuration;
using ProxyToAnyConnect.Runtime;
using ProxyToAnyConnect.Vpn;

namespace ProxyToAnyConnect.SelfTests;

internal static class ResidualVpnOwnerSelfTests
{
    public static async Task<int> RunAsync()
    {
        try
        {
            await LeaseManagerRetriesOnlyFailedNestedControllerDisposeAsync();
            await ReconfigureRetainsExactFailedVpnOwnerUntilRetryAsync();

            Console.WriteLine(
                "PASS: failed VPN teardown retains the exact manager and retries residual controller ownership before replacement");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: residual VPN owner retry regression: {ex}");
            return 1;
        }
    }

    private static async Task LeaseManagerRetriesOnlyFailedNestedControllerDisposeAsync()
    {
        var controller = new RetryableDisposeController();
        var manager = new VpnLeaseManager(
            CreateVpn("vpn-residual-retry", "Residual retry VPN"),
            controller,
            TimeSpan.FromSeconds(30));

        try
        {
            await manager.DisposeAsync();
            throw new InvalidOperationException(
                "First synthetic controller cleanup failure was not propagated.");
        }
        catch (SyntheticCleanupException ex) when (ex.Message == RetryableDisposeController.FirstFailureMessage)
        {
        }

        if (controller.DisposeCount != 1)
        {
            throw new InvalidOperationException(
                $"Expected one nested controller dispose attempt, got {controller.DisposeCount}.");
        }

        await manager.DisposeAsync();
        if (controller.DisposeCount != 2)
        {
            throw new InvalidOperationException(
                "Second manager DisposeAsync did not retry the failed nested controller ownership.");
        }

        await manager.DisposeAsync();
        if (controller.DisposeCount != 2)
        {
            throw new InvalidOperationException(
                "Successful nested controller disposal was retried unnecessarily.");
        }
    }

    private static async Task ReconfigureRetainsExactFailedVpnOwnerUntilRetryAsync()
    {
        var initial = CreateOptions("Initial residual owner VPN");
        var desired = CreateOptions("Replacement residual owner VPN");
        var coordinator = new ProxyRuntimeCoordinator(initial);
        var vpnMap = GetPrivateField<Dictionary<string, VpnLeaseManager>>(coordinator, "_vpnById");

        var constructorManager = vpnMap["vpn-residual-owner"];
        await constructorManager.DisposeAsync();

        var controller = new RetryableDisposeController();
        var retainedManager = new VpnLeaseManager(
            initial.VpnConnections.Single(),
            controller,
            TimeSpan.FromSeconds(30));
        vpnMap["vpn-residual-owner"] = retainedManager;

        try
        {
            await coordinator.ReconfigureAsync(desired, CancellationToken.None);
            throw new InvalidOperationException(
                "Synthetic changed-VPN cleanup failure was not propagated from reconfigure.");
        }
        catch (SyntheticCleanupException ex) when (ex.Message == RetryableDisposeController.FirstFailureMessage)
        {
        }

        if (!vpnMap.TryGetValue("vpn-residual-owner", out var afterFailure) ||
            !ReferenceEquals(afterFailure, retainedManager))
        {
            throw new InvalidOperationException(
                "Failed changed VPN owner was discarded instead of being retained for exact cleanup retry.");
        }

        if (controller.DisposeCount != 1)
        {
            throw new InvalidOperationException(
                $"Expected one failed retained-owner dispose attempt, got {controller.DisposeCount}.");
        }

        await coordinator.ReconfigureAsync(desired, CancellationToken.None);

        if (controller.DisposeCount != 2)
        {
            throw new InvalidOperationException(
                "Same desired reconfigure did not retry the exact failed VPN owner before replacement.");
        }

        if (!vpnMap.TryGetValue("vpn-residual-owner", out var replacement) ||
            ReferenceEquals(replacement, retainedManager) ||
            replacement.Options.Name != "Replacement residual owner VPN")
        {
            throw new InvalidOperationException(
                "Replacement VPN generation was not installed only after retained owner cleanup succeeded.");
        }

        await coordinator.DisposeAsync();
    }

    private static AppOptions CreateOptions(string vpnName) =>
        new()
        {
            Proxies =
            [
                new ProxyOptions
                {
                    Id = "proxy-residual-owner",
                    Name = "Residual owner proxy",
                    Enabled = false,
                    ListenAddress = "127.0.0.1",
                    ListenPort = 18341,
                    VpnConnectionId = "vpn-residual-owner",
                    MaxConcurrentConnections = 8,
                    MaxHeaderBytes = 8192,
                    ClientHeaderTimeoutSeconds = 5,
                    OutboundConnectTimeoutSeconds = 5,
                    DnsTimeoutMilliseconds = 1000
                }
            ],
            VpnConnections = [CreateVpn("vpn-residual-owner", vpnName)]
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

    private sealed class RetryableDisposeController : IVpnConnectionController
    {
        public const string FirstFailureMessage = "first nested controller dispose failed";
        private int _disposeCount;

        public int DisposeCount => Volatile.Read(ref _disposeCount);
        public VpnContext? Current => null;
        public VpnConnectionState State => VpnConnectionState.Disconnected;
        public long ReconnectCooldownRemainingMilliseconds => 0;

        public Task<VpnContext> ConnectAsync(CancellationToken cancellationToken) =>
            Task.FromException<VpnContext>(
                new InvalidOperationException("Residual-owner self-test controller must never connect."));

        public Task DisconnectAsync() => Task.CompletedTask;

        public ValueTask DisposeAsync()
        {
            var attempt = Interlocked.Increment(ref _disposeCount);
            return attempt == 1
                ? ValueTask.FromException(new SyntheticCleanupException(FirstFailureMessage))
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
}