using ProxyToAnyConnect.Configuration;
using ProxyToAnyConnect.Runtime;

namespace ProxyToAnyConnect.SelfTests;

internal static class ProxyRejectedStartupCleanupFailureSelfTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public static async Task<int> RunAsync()
    {
        try
        {
            await ReadinessFailureSurvivesCancellationCallbackFailureAsync();
            await CallerCancellationSurvivesRunCancellationCallbackFailureAsync();

            Console.WriteLine(
                "PASS: rejected proxy starts preserve primary failure/cancellation through secondary cleanup callback faults");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: rejected-start cleanup-failure regression: {ex}");
            return 1;
        }
    }

    private static async Task ReadinessFailureSurvivesCancellationCallbackFailureAsync()
    {
        var lease = new FakeLease();
        var server = new ThrowingCancellationServer(waitForCallerCancellation: false);
        await using var runtime = new ProxyInstanceRuntime(
            CreateOptions("readiness"),
            new SingleAttemptFactory(lease, server));

        try
        {
            await runtime.StartAsync();
            throw new InvalidOperationException(
                "Synthetic readiness failure unexpectedly allowed startup.");
        }
        catch (IOException ex) when (ex.Message == ThrowingCancellationServer.ReadinessFailureMessage)
        {
            AssertCancelCleanupAttached(ex, "readiness failure");
        }

        await server.CancellationObserved.WaitAsync(Timeout);
        AssertLeaseAndState(lease, runtime, ProxyInstanceState.Error, "readiness failure");
    }

    private static async Task CallerCancellationSurvivesRunCancellationCallbackFailureAsync()
    {
        var lease = new FakeLease();
        var server = new ThrowingCancellationServer(waitForCallerCancellation: true);
        await using var runtime = new ProxyInstanceRuntime(
            CreateOptions("caller-cancel"),
            new SingleAttemptFactory(lease, server));
        using var cancellation = new CancellationTokenSource();

        var startTask = runtime.StartAsync(cancellation.Token);
        await server.RunStarted.WaitAsync(Timeout);
        cancellation.Cancel();

        try
        {
            await startTask;
            throw new InvalidOperationException(
                "Caller cancellation unexpectedly allowed startup.");
        }
        catch (OperationCanceledException ex) when (
            ex.CancellationToken == cancellation.Token && cancellation.IsCancellationRequested)
        {
            AssertCancelCleanupAttached(ex, "caller cancellation");
        }

        await server.CancellationObserved.WaitAsync(Timeout);
        AssertLeaseAndState(lease, runtime, ProxyInstanceState.Paused, "caller cancellation");
    }

    private static void AssertCancelCleanupAttached(Exception primary, string phase)
    {
        const string key = "ProxyStartCleanup:run-cancel";
        if (!primary.Data.Contains(key) ||
            primary.Data[key]?.ToString()?.Contains(
                nameof(SyntheticCancelCallbackException),
                StringComparison.Ordinal) != true)
        {
            throw new InvalidOperationException(
                $"{phase}: secondary cancellation-callback failure was not attached to the primary exception.");
        }
    }

    private static void AssertLeaseAndState(
        FakeLease lease,
        ProxyInstanceRuntime runtime,
        ProxyInstanceState expectedState,
        string phase)
    {
        if (lease.DisposeCount != 1)
        {
            throw new InvalidOperationException(
                $"{phase}: rejected start disposed its lease {lease.DisposeCount} time(s); expected 1.");
        }

        if (runtime.State != expectedState)
        {
            throw new InvalidOperationException(
                $"{phase}: runtime state is {runtime.State}; expected {expectedState}.");
        }
    }

    private static ProxyOptions CreateOptions(string suffix) =>
        new()
        {
            Id = $"rejected-cleanup-{suffix}",
            Name = $"Rejected cleanup {suffix}",
            Enabled = true,
            ListenAddress = "127.0.0.1",
            ListenPort = suffix == "readiness" ? 18191 : 18192,
            VpnConnectionId = $"rejected-cleanup-vpn-{suffix}",
            MaxConcurrentConnections = 8,
            MaxHeaderBytes = 8192,
            ClientHeaderTimeoutSeconds = 5,
            OutboundConnectTimeoutSeconds = 5,
            DnsTimeoutMilliseconds = 1000
        };

    private sealed class SingleAttemptFactory : IProxyInstanceStartFactory
    {
        private readonly ProxyStartAttempt _attempt;
        private int _taken;

        public SingleAttemptFactory(FakeLease lease, ThrowingCancellationServer server)
        {
            _attempt = new ProxyStartAttempt(lease, server);
        }

        public Task<ProxyStartAttempt> CreateAsync(
            ProxyOptions options,
            ProxyRuntimeMetrics metrics,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Exchange(ref _taken, 1) != 0)
            {
                throw new InvalidOperationException("Synthetic attempt was already consumed.");
            }

            return Task.FromResult(_attempt);
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

    private sealed class ThrowingCancellationServer : IProxyServerLifetime
    {
        public const string ReadinessFailureMessage = "Synthetic listener readiness failure with throwing cleanup callback.";

        private readonly bool _waitForCallerCancellation;
        private readonly TaskCompletionSource _runStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _cancellationObserved =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _neverReady =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ThrowingCancellationServer(bool waitForCallerCancellation)
        {
            _waitForCallerCancellation = waitForCallerCancellation;
        }

        public Task RunStarted => _runStarted.Task;
        public Task CancellationObserved => _cancellationObserved.Task;

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            using var throwingRegistration = cancellationToken.Register(
                static () => throw new SyntheticCancelCallbackException());
            _runStarted.TrySetResult();

            try
            {
                await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _cancellationObserved.TrySetResult();
                throw;
            }
        }

        public Task WaitUntilListeningAsync(CancellationToken cancellationToken) =>
            _waitForCallerCancellation
                ? _neverReady.Task.WaitAsync(cancellationToken)
                : Task.FromException(new IOException(ReadinessFailureMessage));
    }

    private sealed class SyntheticCancelCallbackException : Exception
    {
    }
}
