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
            await IndependentProxyStartsOverlapAndIsolateFailureAsync();
            await LifetimeCancellationFaultStillDisposesNestedOwnersAsync();
            Console.WriteLine(
                "PASS: coordinator starts and cleanup overlap independent owners, preserve isolated/pending failures and deterministic cleanup ordering");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: coordinator cleanup/start-independence regression: {ex}");
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

    private static async Task IndependentProxyStartsOverlapAndIsolateFailureAsync()
    {
        var options = CreateIndependentStartOptions();
        var coordinator = new ProxyRuntimeCoordinator(options);
        var proxyMap = GetPrivateField<Dictionary<string, ProxyInstanceRuntime>>(coordinator, "_proxyById");
        var oldRuntimes = proxyMap.Values.ToArray();
        foreach (var old in oldRuntimes)
        {
            await old.DisposeAsync();
        }

        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstFactory = new BlockingStartFactory(firstEntered, release, fail: true);
        var secondFactory = new BlockingStartFactory(secondEntered, release, fail: false);

        proxyMap["proxy-start-a"] = new ProxyInstanceRuntime(options.Proxies[0], firstFactory);
        proxyMap["proxy-start-b"] = new ProxyInstanceRuntime(options.Proxies[1], secondFactory);

        var startTask = coordinator.StartEnabledAsync();
        await Task.WhenAll(firstEntered.Task, secondEntered.Task)
            .WaitAsync(TimeSpan.FromSeconds(1));
        if (startTask.IsCompleted)
        {
            throw new InvalidOperationException(
                "Coordinator startup completed before blocked independent start generations were released.");
        }

        release.TrySetResult();
        await startTask.WaitAsync(TimeSpan.FromSeconds(2));

        var snapshots = coordinator.GetProxySnapshots().ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        if (snapshots["proxy-start-a"].State != ProxyInstanceState.Error ||
            snapshots["proxy-start-b"].State != ProxyInstanceState.Running)
        {
            throw new InvalidOperationException(
                $"Independent start failure was not isolated: a={snapshots["proxy-start-a"].State}, b={snapshots["proxy-start-b"].State}.");
        }

        var pending = GetPrivateField<HashSet<string>>(coordinator, "_pendingStartProxyIds");
        if (!pending.Contains("proxy-start-a") || pending.Contains("proxy-start-b"))
        {
            throw new InvalidOperationException(
                "Coordinator did not retain only the failed desired start for later same-config reconciliation.");
        }

        if (firstFactory.CreateCount != 1 || secondFactory.CreateCount != 1)
        {
            throw new InvalidOperationException(
                "Independent start generations were not attempted exactly once.");
        }

        await coordinator.DisposeAsync();
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

        await coordinator.DisposeAsync();
    }

    private static AppOptions CreateOptions() =>
        new()
        {
            Proxies =
            [
                CreateProxy("proxy-coordinator-cleanup", "vpn-coordinator-cleanup", 18321, enabled: false)
            ],
            VpnConnections =
            [
                CreateVpn("vpn-coordinator-cleanup")
            ]
        };

    private static AppOptions CreateIndependentStartOptions() =>
        new()
        {
            Proxies =
            [
                CreateProxy("proxy-start-a", "vpn-start-a", 18322, enabled: true),
                CreateProxy("proxy-start-b", "vpn-start-b", 18323, enabled: true)
            ],
            VpnConnections =
            [
                CreateVpn("vpn-start-a"),
                CreateVpn("vpn-start-b")
            ]
        };

    private static ProxyOptions CreateProxy(
        string id,
        string vpnId,
        int port,
        bool enabled) =>
        new()
        {
            Id = id,
            Name = id,
            Enabled = enabled,
            ListenAddress = "127.0.0.1",
            ListenPort = port,
            VpnConnectionId = vpnId,
            MaxConcurrentConnections = 8,
            MaxHeaderBytes = 8192,
            ClientHeaderTimeoutSeconds = 5,
            OutboundConnectTimeoutSeconds = 5,
            DnsTimeoutMilliseconds = 1000
        };

    private static L2tpOptions CreateVpn(string id) =>
        new()
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

    private sealed class BlockingStartFactory : IProxyInstanceStartFactory
    {
        private readonly TaskCompletionSource _entered;
        private readonly TaskCompletionSource _release;
        private readonly bool _fail;
        private int _createCount;

        public BlockingStartFactory(
            TaskCompletionSource entered,
            TaskCompletionSource release,
            bool fail)
        {
            _entered = entered;
            _release = release;
            _fail = fail;
        }

        public int CreateCount => Volatile.Read(ref _createCount);

        public async Task<ProxyStartAttempt> CreateAsync(
            ProxyOptions options,
            ProxyRuntimeMetrics metrics,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _createCount);
            _entered.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            if (_fail)
            {
                throw new SyntheticCleanupException($"Synthetic start failure for {options.Id}");
            }

            return new ProxyStartAttempt(new NoopLease(), new BlockingProxyServer());
        }
    }

    private sealed class BlockingProxyServer : IProxyServerLifetime
    {
        public async Task RunAsync(CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }

        public Task WaitUntilListeningAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class NoopLease : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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
