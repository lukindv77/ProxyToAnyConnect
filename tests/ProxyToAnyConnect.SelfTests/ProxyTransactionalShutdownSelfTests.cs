using ProxyToAnyConnect.Configuration;
using ProxyToAnyConnect.Runtime;

namespace ProxyToAnyConnect.SelfTests;

internal static class ProxyTransactionalShutdownSelfTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public static async Task<int> RunAsync()
    {
        try
        {
            await CallerCancellationCannotReleaseLeaseBeforeExactRunDrainAsync();
            await DisposeCannotReleaseLeaseBeforeExactRunDrainAsync();

            Console.WriteLine(
                "PASS: proxy pause/dispose shutdown drains exact run ownership before releasing the VPN lease");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: proxy transactional shutdown regression: {ex}");
            return 1;
        }
    }

    private static async Task CallerCancellationCannotReleaseLeaseBeforeExactRunDrainAsync()
    {
        var lease = new FakeLease();
        var server = new BlockingDrainServer();
        var runtime = new ProxyInstanceRuntime(
            CreateOptions("pause"),
            new SingleAttemptFactory(lease, server));

        try
        {
            await runtime.StartAsync();
            using var cancellation = new CancellationTokenSource();
            var pauseTask = runtime.PauseAsync(cancellation.Token);

            await server.CancellationObserved.WaitAsync(Timeout);
            cancellation.Cancel();

            var prematureRelease = await Task.WhenAny(
                lease.Disposed,
                Task.Delay(TimeSpan.FromMilliseconds(150)));
            if (ReferenceEquals(prematureRelease, lease.Disposed))
            {
                throw new InvalidOperationException(
                    "Caller cancellation released the VPN lease before the exact proxy run task drained.");
            }

            if (pauseTask.IsCompleted)
            {
                throw new InvalidOperationException(
                    "Pause completed before the exact proxy run task drained.");
            }

            server.ReleaseDrain();
            await ExpectCallerCancellationAsync(pauseTask, cancellation.Token);
            await lease.Disposed.WaitAsync(Timeout);

            if (lease.DisposeCount != 1 || runtime.State != ProxyInstanceState.Paused)
            {
                throw new InvalidOperationException(
                    $"Cancelled pause did not finish cleanly: leaseDispose={lease.DisposeCount}, state={runtime.State}.");
            }
        }
        finally
        {
            server.ReleaseDrain();
            try
            {
                await runtime.DisposeAsync();
            }
            catch
            {
            }
        }
    }

    private static async Task DisposeCannotReleaseLeaseBeforeExactRunDrainAsync()
    {
        var lease = new FakeLease();
        var server = new BlockingDrainServer();
        var runtime = new ProxyInstanceRuntime(
            CreateOptions("dispose"),
            new SingleAttemptFactory(lease, server));

        await runtime.StartAsync();
        var disposeTask = runtime.DisposeAsync().AsTask();
        await server.CancellationObserved.WaitAsync(Timeout);

        var prematureRelease = await Task.WhenAny(
            lease.Disposed,
            Task.Delay(TimeSpan.FromMilliseconds(150)));
        if (ReferenceEquals(prematureRelease, lease.Disposed))
        {
            throw new InvalidOperationException(
                "Runtime disposal released the VPN lease before the exact proxy run task drained.");
        }

        if (disposeTask.IsCompleted)
        {
            throw new InvalidOperationException(
                "Runtime disposal completed before the exact proxy run task drained.");
        }

        server.ReleaseDrain();
        await disposeTask;
        await lease.Disposed.WaitAsync(Timeout);

        if (lease.DisposeCount != 1 || runtime.State != ProxyInstanceState.Paused)
        {
            throw new InvalidOperationException(
                $"Runtime disposal did not finish cleanly: leaseDispose={lease.DisposeCount}, state={runtime.State}.");
        }

        await runtime.DisposeAsync();
        if (lease.DisposeCount != 1)
        {
            throw new InvalidOperationException(
                "Repeated runtime disposal duplicated VPN lease release.");
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
        catch (OperationCanceledException ex) when (
            cancellationToken.IsCancellationRequested &&
            ex.CancellationToken == cancellationToken)
        {
            return;
        }

        throw new InvalidOperationException(
            "Pause did not preserve the caller cancellation token after transactional drain.");
    }

    private static ProxyOptions CreateOptions(string suffix) =>
        new()
        {
            Id = $"transactional-stop-{suffix}",
            Name = $"Transactional stop {suffix}",
            Enabled = true,
            ListenAddress = "127.0.0.1",
            ListenPort = suffix == "pause" ? 18171 : 18172,
            VpnConnectionId = $"transactional-stop-vpn-{suffix}",
            MaxConcurrentConnections = 8,
            MaxHeaderBytes = 8192,
            ClientHeaderTimeoutSeconds = 5,
            OutboundConnectTimeoutSeconds = 5,
            DnsTimeoutMilliseconds = 1000
        };

    private sealed class SingleAttemptFactory : IProxyInstanceStartFactory
    {
        private ProxyStartAttempt? _attempt;

        public SingleAttemptFactory(FakeLease lease, BlockingDrainServer server)
        {
            _attempt = new ProxyStartAttempt(lease, server);
        }

        public Task<ProxyStartAttempt> CreateAsync(
            ProxyOptions options,
            ProxyRuntimeMetrics metrics,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var attempt = Interlocked.Exchange(ref _attempt, null)
                ?? throw new InvalidOperationException("Synthetic start attempt was already consumed.");
            return Task.FromResult(attempt);
        }
    }

    private sealed class FakeLease : IAsyncDisposable
    {
        private readonly TaskCompletionSource _disposed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _disposeCount;

        public int DisposeCount => Volatile.Read(ref _disposeCount);
        public Task Disposed => _disposed.Task;

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            _disposed.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class BlockingDrainServer : IProxyServerLifetime
    {
        private readonly TaskCompletionSource _cancellationObserved =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _allowDrain =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task CancellationObserved => _cancellationObserved.Task;

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _cancellationObserved.TrySetResult();
                await _allowDrain.Task;
                throw;
            }
        }

        public Task WaitUntilListeningAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public void ReleaseDrain() => _allowDrain.TrySetResult();
    }
}
