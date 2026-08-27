using System.Reflection;
using ProxyToAnyConnect.Configuration;
using ProxyToAnyConnect.Runtime;

namespace ProxyToAnyConnect.SelfTests;

internal static class CoordinatorCleanupFailureSelfTests
{
    public static async Task<int> RunAsync()
    {
        try
        {
            await AllOwnersDisposeAfterEarlierFailureAsync();
            await IndependentOwnersOverlapAndPreserveInputOrderedFailuresAsync();
            await LifetimeCancellationFaultStillDisposesNestedOwnersAsync();
            Console.WriteLine(
                "PASS: coordinator cleanup overlaps independent owners, preserves deterministic failure ordering and drains nested ownership through cancellation callback faults");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: coordinator cleanup-failure regression: {ex}");
            return 1;
        }
    }

    private static async Task AllOwnersDisposeAfterEarlierFailureAsync()
    {
        var order = new List<string>();
        var primary = new SyntheticCleanupException("first owner failed");
        var secondary = new SyntheticCleanupException("third owner failed");
        var first = new RecordingDisposable("first", order, primary);
        var second = new RecordingDisposable("second", order, null);
        var third = new RecordingDisposable("third", order, secondary);

        var returned = await ProxyRuntimeCoordinator.DisposeOwnedResourcesAsync(
            new IAsyncDisposable[] { first, second, third },
            "self-test");

        if (!ReferenceEquals(returned, primary))
        {
            throw new InvalidOperationException(
                "Coordinator cleanup did not preserve the first teardown exception as primary.");
        }

        if (!order.SequenceEqual(["first", "second", "third"]))
        {
            throw new InvalidOperationException(
                $"Coordinator cleanup did not start synchronous owners in deterministic input order: {string.Join(",", order)}.");
        }

        if (first.DisposeCount != 1 || second.DisposeCount != 1 || third.DisposeCount != 1)
        {
            throw new InvalidOperationException(
                "Coordinator cleanup did not attempt every independent owner exactly once.");
        }

        var key = "CoordinatorCleanup:self-test:2";
        if (!primary.Data.Contains(key) ||
            primary.Data[key]?.ToString()?.Contains("third owner failed", StringComparison.Ordinal) != true)
        {
            throw new InvalidOperationException(
                "Coordinator cleanup did not attach the later teardown failure to the primary exception.");
        }
    }

    private static async Task IndependentOwnersOverlapAndPreserveInputOrderedFailuresAsync()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstFailure = new SyntheticCleanupException("parallel first owner failed");
        var secondFailure = new SyntheticCleanupException("parallel second owner failed");
        var first = new BlockingDisposable(firstEntered, release, firstFailure);
        var second = new BlockingDisposable(secondEntered, release, secondFailure);

        var cleanupTask = ProxyRuntimeCoordinator.DisposeOwnedResourcesAsync(
            new IAsyncDisposable[] { first, second },
            "parallel-self-test");

        await Task.WhenAll(firstEntered.Task, secondEntered.Task)
            .WaitAsync(TimeSpan.FromSeconds(1));
        if (cleanupTask.IsCompleted)
        {
            throw new InvalidOperationException(
                "Coordinator cleanup completed before blocked independent owners were released.");
        }

        release.TrySetResult();
        var returned = await cleanupTask.WaitAsync(TimeSpan.FromSeconds(1));
        if (!ReferenceEquals(returned, firstFailure))
        {
            throw new InvalidOperationException(
                "Concurrent coordinator cleanup did not preserve input-order primary failure selection.");
        }

        var secondaryKey = "CoordinatorCleanup:parallel-self-test:1";
        if (!firstFailure.Data.Contains(secondaryKey) ||
            firstFailure.Data[secondaryKey]?.ToString()?.Contains(
                "parallel second owner failed",
                StringComparison.Ordinal) != true)
        {
            throw new InvalidOperationException(
                "Concurrent coordinator cleanup did not retain the later input-order failure diagnostically.");
        }
    }

    private static async Task LifetimeCancellationFaultStillDisposesNestedOwnersAsync()
    {
        var coordinator = new ProxyRuntimeCoordinator(CreateOptions());
        var lifetime = GetPrivateField<CancellationTokenSource>(coordinator, "_lifetime");
        var vpnMap = GetPrivateField<Dictionary<string, VpnLeaseManager>>(coordinator, "_vpnById");
        var proxyMap = GetPrivateField<Dictionary<string, ProxyInstanceRuntime>>(coordinator, "_proxyById");
        var pendingStarts = GetPrivateField<HashSet<string>>(coordinator, "_pendingStartProxyIds");
        var nestedVpn = vpnMap["vpn-coordinator-cleanup"];
        var nestedLifetime = GetPrivateField<CancellationTokenSource>(nestedVpn, "_lifetime");

        _ = lifetime.Token.Register(
            static () => throw new SyntheticCleanupException(
                "coordinator lifetime cancellation callback failed"));

        try
        {
            await coordinator.DisposeAsync();
            throw new InvalidOperationException(
                "Throwing coordinator lifetime cancellation callback was not surfaced from DisposeAsync.");
        }
        catch (AggregateException ex) when (
            ex.InnerExceptions.Any(inner =>
                inner is SyntheticCleanupException synthetic &&
                synthetic.Message == "coordinator lifetime cancellation callback failed"))
        {
        }

        if (proxyMap.Count != 0 || vpnMap.Count != 0 || pendingStarts.Count != 0)
        {
            throw new InvalidOperationException(
                "Coordinator lifetime cancellation callback fault left nested runtime ownership published.");
        }

        if (GetPrivateField<int>(coordinator, "_disposed") == 0 ||
            !CancellationSourceWasDisposed(lifetime) ||
            !CancellationSourceWasDisposed(nestedLifetime) ||
            GetPrivateField<int>(nestedVpn, "_disposed") == 0)
        {
            throw new InvalidOperationException(
                "Coordinator lifetime cancellation callback fault prevented parent or nested VPN lifetime disposal.");
        }

        // The first call reported the callback defect but completed all independent
        // ownership release. Repeated coordinator disposal is therefore a no-op.
        await coordinator.DisposeAsync();
    }

    private static AppOptions CreateOptions() =>
        new()
        {
            Proxies =
            [
                new ProxyOptions
                {
                    Id = "proxy-coordinator-cleanup",
                    Name = "Coordinator cleanup proxy",
                    Enabled = false,
                    ListenAddress = "127.0.0.1",
                    ListenPort = 18321,
                    VpnConnectionId = "vpn-coordinator-cleanup",
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
                    Id = "vpn-coordinator-cleanup",
                    Name = "Coordinator cleanup VPN",
                    Shared = false,
                    Mode = L2tpConnectionMode.ExistingWindowsProfile,
                    EntryName = "SelfTest-vpn-coordinator-cleanup",
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
        if (value is null)
        {
            return default!;
        }

        return (T)value;
    }

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

    private sealed class BlockingDisposable : IAsyncDisposable
    {
        private readonly TaskCompletionSource _entered;
        private readonly TaskCompletionSource _release;
        private readonly Exception? _failure;

        public BlockingDisposable(
            TaskCompletionSource entered,
            TaskCompletionSource release,
            Exception? failure)
        {
            _entered = entered;
            _release = release;
            _failure = failure;
        }

        public async ValueTask DisposeAsync()
        {
            _entered.TrySetResult();
            await _release.Task;
            if (_failure is not null)
            {
                throw _failure;
            }
        }
    }

    private sealed class RecordingDisposable : IAsyncDisposable
    {
        private readonly string _name;
        private readonly List<string> _order;
        private readonly Exception? _failure;

        public RecordingDisposable(string name, List<string> order, Exception? failure)
        {
            _name = name;
            _order = order;
            _failure = failure;
        }

        public int DisposeCount { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            _order.Add(_name);
            return _failure is null
                ? ValueTask.CompletedTask
                : ValueTask.FromException(_failure);
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
