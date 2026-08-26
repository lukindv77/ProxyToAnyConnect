using System.Reflection;
using System.Runtime.CompilerServices;
using ProxyToAnyConnect.Configuration;
using ProxyToAnyConnect.Runtime;

namespace ProxyToAnyConnect.SelfTests;

internal static class ProxyTransactionalStartupSelfTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public static async Task<int> RunAsync()
    {
        try
        {
            await CallerCancellationDrainsBeforeLeaseReleaseAndRetriesAsync();
            await ReadinessFailureDrainsBeforeLeaseReleaseAndRetriesAsync();
            await RejectedStartOwnershipIsCollectibleAcrossCyclesAsync();

            Console.WriteLine(
                "PASS: proxy startup ownership is transactional, drain-safe, retryable and collectible across rejected starts");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: proxy transactional startup regression: {ex}");
            return 1;
        }
    }

    private static async Task CallerCancellationDrainsBeforeLeaseReleaseAndRetriesAsync()
    {
        var factory = new FakeStartFactory();
        await using var runtime = new ProxyInstanceRuntime(CreateOptions(), factory);

        var rejectedLease = new FakeLease();
        var rejectedServer = new FakeServer(ReadinessMode.WaitForCancellation, blockDrain: true);
        factory.Enqueue(rejectedLease, rejectedServer);

        using var cancellation = new CancellationTokenSource();
        var startTask = runtime.StartAsync(cancellation.Token);
        await rejectedServer.RunStarted.WaitAsync(Timeout);

        cancellation.Cancel();
        await rejectedServer.CancellationObserved.WaitAsync(Timeout);

        if (startTask.IsCompleted)
        {
            throw new InvalidOperationException(
                "Cancelled startup completed before its exact run task was allowed to drain.");
        }

        if (rejectedLease.DisposeCount != 0)
        {
            throw new InvalidOperationException(
                "Cancelled startup released the VPN lease before the exact run task drained.");
        }

        rejectedServer.ReleaseDrain();
        await ExpectCallerCancellationAsync(startTask, cancellation.Token);

        AssertRejectedAttemptClean(runtime, ProxyInstanceState.Paused, expectedError: null);
        AssertDisposeCount(rejectedLease, 1, "cancelled startup");

        var runningLease = new FakeLease();
        var runningServer = new FakeServer(ReadinessMode.Success, blockDrain: false);
        factory.Enqueue(runningLease, runningServer);

        await runtime.StartAsync();
        if (runtime.State != ProxyInstanceState.Running)
        {
            throw new InvalidOperationException(
                $"Retry after caller cancellation did not reach Running: {runtime.State}.");
        }

        await runtime.PauseAsync();
        await runningServer.CancellationObserved.WaitAsync(Timeout);
        AssertDisposeCount(runningLease, 1, "successful retry pause");

        await runtime.PauseAsync();
        AssertDisposeCount(runningLease, 1, "idempotent second pause");
    }

    private static async Task ReadinessFailureDrainsBeforeLeaseReleaseAndRetriesAsync()
    {
        var factory = new FakeStartFactory();
        await using var runtime = new ProxyInstanceRuntime(CreateOptions(), factory);

        var rejectedLease = new FakeLease();
        var rejectedServer = new FakeServer(ReadinessMode.Fail, blockDrain: true);
        factory.Enqueue(rejectedLease, rejectedServer);

        var startTask = runtime.StartAsync();
        await rejectedServer.RunStarted.WaitAsync(Timeout);
        await rejectedServer.CancellationObserved.WaitAsync(Timeout);

        if (startTask.IsCompleted)
        {
            throw new InvalidOperationException(
                "Failed readiness completed before its exact run task drain was released.");
        }

        if (rejectedLease.DisposeCount != 0)
        {
            throw new InvalidOperationException(
                "Failed readiness released the VPN lease before listener/session drain completed.");
        }

        rejectedServer.ReleaseDrain();
        await ExpectIOExceptionAsync(startTask);

        AssertRejectedAttemptClean(
            runtime,
            ProxyInstanceState.Error,
            expectedError: "Synthetic listener readiness failure.");
        AssertDisposeCount(rejectedLease, 1, "failed readiness");

        var retryLease = new FakeLease();
        var retryServer = new FakeServer(ReadinessMode.Success, blockDrain: false);
        factory.Enqueue(retryLease, retryServer);

        await runtime.StartAsync();
        if (runtime.State != ProxyInstanceState.Running)
        {
            throw new InvalidOperationException(
                $"Retry after readiness failure did not reach Running: {runtime.State}.");
        }

        await runtime.PauseAsync();
        AssertDisposeCount(retryLease, 1, "readiness-failure retry pause");
    }

    private static async Task RejectedStartOwnershipIsCollectibleAcrossCyclesAsync()
    {
        var factory = new FakeStartFactory();
        await using var runtime = new ProxyInstanceRuntime(CreateOptions(), factory);
        var references = new List<(WeakReference Lease, WeakReference Server)>();

        for (var i = 0; i < 32; i++)
        {
            references.Add(await RunRejectedAttemptForCollectionAsync(runtime, factory));
        }

        await Task.Yield();
        ForceFullCollectionForTest();

        var retained = references.Count(pair => pair.Lease.IsAlive || pair.Server.IsAlive);
        if (retained > 1)
        {
            throw new InvalidOperationException(
                $"Rejected startup cycles retained {retained} lease/server ownership pair(s); expected at most one final async/JIT root.");
        }

        AssertRejectedAttemptClean(runtime, ProxyInstanceState.Paused, expectedError: null);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(WeakReference Lease, WeakReference Server)> RunRejectedAttemptForCollectionAsync(
        ProxyInstanceRuntime runtime,
        FakeStartFactory factory)
    {
        var lease = new FakeLease();
        var server = new FakeServer(ReadinessMode.WaitForCancellation, blockDrain: false);
        factory.Enqueue(lease, server);

        using var cancellation = new CancellationTokenSource();
        var startTask = runtime.StartAsync(cancellation.Token);
        await server.RunStarted.WaitAsync(Timeout);
        cancellation.Cancel();
        await ExpectCallerCancellationAsync(startTask, cancellation.Token);

        AssertDisposeCount(lease, 1, "collectability-cycle cancellation");
        return (new WeakReference(lease), new WeakReference(server));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ForceFullCollectionForTest()
    {
        for (var i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }

    private static void AssertRejectedAttemptClean(
        ProxyInstanceRuntime runtime,
        ProxyInstanceState expectedState,
        string? expectedError)
    {
        if (runtime.State != expectedState)
        {
            throw new InvalidOperationException(
                $"Rejected startup left state {runtime.State}; expected {expectedState}.");
        }

        if (!string.Equals(runtime.LastError, expectedError, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Rejected startup error was '{runtime.LastError ?? "<null>"}'; expected '{expectedError ?? "<null>"}'.");
        }

        foreach (var fieldName in new[] { "_lease", "_runCancellation", "_runTask", "_observerTask" })
        {
            var field = typeof(ProxyInstanceRuntime).GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingFieldException(typeof(ProxyInstanceRuntime).FullName, fieldName);
            if (field.GetValue(runtime) is not null)
            {
                throw new InvalidOperationException(
                    $"Rejected startup retained stale ownership in {fieldName}.");
            }
        }
    }

    private static void AssertDisposeCount(FakeLease lease, int expected, string phase)
    {
        if (lease.DisposeCount != expected)
        {
            throw new InvalidOperationException(
                $"{phase} disposed its lease {lease.DisposeCount} time(s); expected {expected}.");
        }
    }

    private static async Task ExpectCallerCancellationAsync(
        Task operation,
        CancellationToken cancellationToken)
    {
        try
        {
            await operation;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        throw new InvalidOperationException("Caller cancellation was not preserved by StartAsync.");
    }

    private static async Task ExpectIOExceptionAsync(Task operation)
    {
        try
        {
            await operation;
        }
        catch (IOException ex) when (ex.Message == "Synthetic listener readiness failure.")
        {
            return;
        }

        throw new InvalidOperationException("Listener readiness failure was not preserved by StartAsync.");
    }

    private static ProxyOptions CreateOptions() =>
        new()
        {
            Id = "transactional-start-proxy",
            Name = "Transactional startup self-test",
            Enabled = true,
            ListenAddress = "127.0.0.1",
            ListenPort = 18141,
            VpnConnectionId = "transactional-start-vpn",
            MaxConcurrentConnections = 8,
            MaxHeaderBytes = 8192,
            ClientHeaderTimeoutSeconds = 5,
            OutboundConnectTimeoutSeconds = 5,
            DnsTimeoutMilliseconds = 1000
        };

    private enum ReadinessMode
    {
        Success,
        WaitForCancellation,
        Fail
    }

    private sealed class FakeStartFactory : IProxyInstanceStartFactory
    {
        private readonly Queue<ProxyStartAttempt> _attempts = new();

        public void Enqueue(FakeLease lease, FakeServer server) =>
            _attempts.Enqueue(new ProxyStartAttempt(lease, server));

        public Task<ProxyStartAttempt> CreateAsync(
            ProxyOptions options,
            ProxyRuntimeMetrics metrics,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_attempts.Count == 0)
            {
                throw new InvalidOperationException("No synthetic startup attempt is queued.");
            }

            return Task.FromResult(_attempts.Dequeue());
        }
    }

    private sealed class FakeLease : IAsyncDisposable
    {
        private int _disposeCount;

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeServer : IProxyServerLifetime
    {
        private readonly ReadinessMode _readinessMode;
        private readonly bool _blockDrain;
        private readonly TaskCompletionSource _runStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _cancellationObserved =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _allowDrain =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _neverReady =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public FakeServer(ReadinessMode readinessMode, bool blockDrain)
        {
            _readinessMode = readinessMode;
            _blockDrain = blockDrain;
            if (!blockDrain)
            {
                _allowDrain.TrySetResult();
            }
        }

        public Task RunStarted => _runStarted.Task;
        public Task CancellationObserved => _cancellationObserved.Task;

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            _runStarted.TrySetResult();
            try
            {
                await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _cancellationObserved.TrySetResult();
                await _allowDrain.Task;
                throw;
            }
        }

        public Task WaitUntilListeningAsync(CancellationToken cancellationToken) =>
            _readinessMode switch
            {
                ReadinessMode.Success => Task.CompletedTask,
                ReadinessMode.WaitForCancellation => _neverReady.Task.WaitAsync(cancellationToken),
                ReadinessMode.Fail => Task.FromException(
                    new IOException("Synthetic listener readiness failure.")),
                _ => throw new InvalidOperationException("Unsupported synthetic readiness mode.")
            };

        public void ReleaseDrain() => _allowDrain.TrySetResult();
    }
}
