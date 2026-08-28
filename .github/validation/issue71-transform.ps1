Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Read-Lf([string]$Path) {
    return [IO.File]::ReadAllText($Path).Replace("`r`n", "`n")
}

function Write-Lf([string]$Path, [string]$Text) {
    [IO.File]::WriteAllText($Path, $Text.Replace("`r`n", "`n"), [Text.UTF8Encoding]::new($false))
}

function Replace-Exact([string]$Text, [string]$Old, [string]$New, [string]$Label) {
    $oldLf = $Old.Replace("`r`n", "`n")
    $newLf = $New.Replace("`r`n", "`n")
    $first = $Text.IndexOf($oldLf, [StringComparison]::Ordinal)
    if ($first -lt 0) { throw "Missing transform anchor: $Label" }
    if ($Text.IndexOf($oldLf, $first + $oldLf.Length, [StringComparison]::Ordinal) -ge 0) {
        throw "Non-unique transform anchor: $Label"
    }
    return $Text.Substring(0, $first) + $newLf + $Text.Substring($first + $oldLf.Length)
}

$leasePath = 'src/ProxyToAnyConnect/Runtime/VpnLeaseManager.cs'
$lease = Read-Lf $leasePath
$lease = Replace-Exact $lease @'
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _lifetimeToken;

    private CancellationTokenSource? _maintenanceCancellation;
'@ @'
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _lifetimeToken;
    private readonly SemaphoreSlim _disposeGate = new(1, 1);

    private CancellationTokenSource? _maintenanceCancellation;
'@ 'lease dispose gate field'

$lease = Replace-Exact $lease @'
    private int _activeProxyCount;
    private int _disposed;
'@ @'
    private int _activeProxyCount;
    private int _disposed;
    private int _connectionDisposeCompleted;
'@ 'lease nested dispose completion field'

$lease = Replace-Exact $lease @'
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Exception? cleanupFailure = null;
'@ @'
    public async ValueTask DisposeAsync()
    {
        await _disposeGate.WaitAsync();
        try
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                await RetryConnectionDisposeAsync();
                return;
            }

            Exception? cleanupFailure = null;
'@ 'lease serialized dispose entry'

$lease = Replace-Exact $lease @'
                try
                {
                    await _connectionManager.DisposeAsync();
                }
                catch (Exception ex)
'@ @'
                try
                {
                    await _connectionManager.DisposeAsync();
                    Volatile.Write(ref _connectionDisposeCompleted, 1);
                }
                catch (Exception ex)
'@ 'lease nested dispose success publication'

$lease = Replace-Exact $lease @'
        RethrowCleanupFailure(cleanupFailure);

        // Do not race SemaphoreSlim.Dispose() against a VpnLease.DisposeAsync()
        // caller that passed the pre-wait disposed check just before shutdown.
        // AvailableWaitHandle is never used, so there is no OS wait handle to
        // release; the managed gate becomes collectible with this manager.
    }

    private static void CaptureCleanupFailure(
'@ @'
            RethrowCleanupFailure(cleanupFailure);

            // Do not race SemaphoreSlim.Dispose() against a VpnLease.DisposeAsync()
            // caller that passed the pre-wait disposed check just before shutdown.
            // AvailableWaitHandle is never used, so there is no OS wait handle to
            // release; both managed gates become collectible with this manager.
        }
        finally
        {
            _disposeGate.Release();
        }
    }

    private async ValueTask RetryConnectionDisposeAsync()
    {
        if (Volatile.Read(ref _connectionDisposeCompleted) != 0)
        {
            return;
        }

        // First-pass manager cleanup already unpublished consumers/maintenance/cache
        // and ended its lifetime. The only retryable residual owner is the nested VPN
        // controller, whose RasConnectionManager may intentionally retain an exact
        // HRASCONN/PBK after a failed terminal hangup.
        await _connectionManager.DisposeAsync();
        Volatile.Write(ref _connectionDisposeCompleted, 1);
    }

    private static void CaptureCleanupFailure(
'@ 'lease retry helper and dispose gate exit'
Write-Lf $leasePath $lease

$coordinatorPath = 'src/ProxyToAnyConnect/Runtime/ProxyRuntimeCoordinator.cs'
$coordinator = Read-Lf $coordinatorPath
$coordinator = Replace-Exact $coordinator @'
            cleanupFailure = await DisposeOwnedResourcesAsync(
                vpnsToDispose.Cast<IAsyncDisposable>(),
                "reconfigure-vpn",
                cleanupFailure);

            RethrowCoordinatorCleanupFailure(cleanupFailure);
'@ @'
            cleanupFailure = await DisposeChangedVpnOwnersAsync(
                vpnsToDispose,
                cleanupFailure);

            RethrowCoordinatorCleanupFailure(cleanupFailure);
'@ 'coordinator retained failed VPN cleanup call'

$coordinator = Replace-Exact $coordinator @'
    internal static async Task<Exception?> DisposeOwnedResourcesAsync(
'@ @'
    private async Task<Exception?> DisposeChangedVpnOwnersAsync(
        IReadOnlyList<VpnLeaseManager> vpns,
        Exception? primaryFailure)
    {
        var attempts = new Task<Exception?>[vpns.Count];
        for (var index = 0; index < vpns.Count; index++)
        {
            attempts[index] = DisposeOneOwnedResourceAsync(vpns[index]);
        }

        for (var index = 0; index < attempts.Length; index++)
        {
            var failure = await attempts[index];
            if (failure is null)
            {
                continue;
            }

            var vpn = vpns[index];
            lock (_collectionGate)
            {
                // Re-publish only cleanup ownership, not a usable generation. The
                // manager is already disposed to new Acquire calls, but retaining the
                // exact instance prevents missing-topology recovery from replacing it
                // before its nested controller reaches terminal cleanup.
                _vpnById.TryAdd(vpn.Id, vpn);
            }

            if (primaryFailure is null)
            {
                primaryFailure = failure;
            }
            else
            {
                primaryFailure.Data[$"CoordinatorCleanup:reconfigure-vpn:{index}"] =
                    $"{failure.GetType().FullName}: {failure.Message}";
            }
        }

        return primaryFailure;
    }

    internal static async Task<Exception?> DisposeOwnedResourcesAsync(
'@ 'coordinator failed VPN retention helper'
Write-Lf $coordinatorPath $coordinator

$runnerPath = 'tests/ProxyToAnyConnect.SelfTests/CombinedTestRunner.cs'
$runner = Read-Lf $runnerPath
$runner = Replace-Exact $runner @'
        await RunAsync(nameof(VpnLeaseCleanupFailureSelfTests), VpnLeaseCleanupFailureSelfTests.RunAsync);
        await RunAsync(nameof(VpnSharedFailClosedSelfTests), VpnSharedFailClosedSelfTests.RunAsync);
'@ @'
        await RunAsync(nameof(VpnLeaseCleanupFailureSelfTests), VpnLeaseCleanupFailureSelfTests.RunAsync);
        await RunAsync(nameof(ResidualVpnOwnerSelfTests), ResidualVpnOwnerSelfTests.RunAsync);
        await RunAsync(nameof(VpnSharedFailClosedSelfTests), VpnSharedFailClosedSelfTests.RunAsync);
'@ 'aggregate residual VPN owner suite'
$runner = $runner.Replace(
    'VPN-lease-owner/shared/dedicated lifecycle/cleanup-failure,',
    'VPN-lease-owner/shared/dedicated lifecycle/cleanup-failure/residual-owner-retry,')
Write-Lf $runnerPath $runner

$testPath = 'tests/ProxyToAnyConnect.SelfTests/ResidualVpnOwnerSelfTests.cs'
$tests = @'
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
'@
Write-Lf $testPath $tests

Write-Host 'Issue #71 transform applied.'
