using System.Net;
using ProxyToAnyConnect.Configuration;
using ProxyToAnyConnect.Runtime;
using ProxyToAnyConnect.Vpn;

namespace ProxyToAnyConnect.SelfTests;

internal static class VpnLeaseManagerLifetimeSelfTests
{
    public static async Task<int> RunAsync()
    {
        try
        {
            await DisposeCancelsPendingAcquireAsync();
            await DisposeRejectsSuccessfulConnectRacingShutdownAsync();
            await SharedLeaseDisconnectsOnlyAfterLastReleaseAsync();
            await DedicatedLeaseRejectsConcurrentConsumerAsync();
            await RepeatedReconnectFailuresRecoverAndStopAfterLastLeaseAsync();
            await RepeatedLeaseManagerCyclesBecomeCollectibleAsync();

            Console.WriteLine(
                "PASS: VPN lease manager owner cancellation, shutdown race barrier, shared last-release, dedicated exclusivity, reconnect churn and collection churn");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: VPN lease manager lifetime regression: {ex}");
            return 1;
        }
    }

    private static async Task DisposeCancelsPendingAcquireAsync()
    {
        var controller = BlockingVpnConnectionController.CreateBlocking();
        var manager = new VpnLeaseManager(CreateOptions(shared: true), controller);

        var acquireTask = manager.AcquireAsync("proxy-a", CancellationToken.None);
        await controller.ConnectStarted.WaitAsync(TimeSpan.FromSeconds(2));

        var disposeTask = manager.DisposeAsync().AsTask();

        try
        {
            _ = await acquireTask.WaitAsync(TimeSpan.FromSeconds(2));
            throw new InvalidOperationException(
                "Manager disposal did not cancel the in-flight lease acquisition.");
        }
        catch (OperationCanceledException)
        {
        }

        await disposeTask.WaitAsync(TimeSpan.FromSeconds(2));

        if (!controller.OwnerCancellationObserved)
        {
            throw new InvalidOperationException(
                "Injected VPN controller did not observe lease-manager lifetime cancellation.");
        }

        if (manager.ActiveProxyCount != 0)
        {
            throw new InvalidOperationException(
                $"Cancelled acquire retained {manager.ActiveProxyCount} active proxy consumer(s).");
        }

        if (controller.DisposeCount != 1)
        {
            throw new InvalidOperationException(
                $"VPN controller dispose count was {controller.DisposeCount}; expected exactly one.");
        }
    }

    private static async Task DisposeRejectsSuccessfulConnectRacingShutdownAsync()
    {
        var controller = new StubbornSuccessfulVpnConnectionController();
        var manager = new VpnLeaseManager(CreateOptions(shared: true), controller);

        var acquireTask = manager.AcquireAsync("proxy-race", CancellationToken.None);
        await controller.ConnectStarted.WaitAsync(TimeSpan.FromSeconds(2));

        var disposeTask = manager.DisposeAsync().AsTask();
        await controller.OwnerCancellationObserved.WaitAsync(TimeSpan.FromSeconds(2));

        controller.AllowSuccessfulReturn();

        try
        {
            _ = await acquireTask.WaitAsync(TimeSpan.FromSeconds(2));
            throw new InvalidOperationException(
                "Acquire returned a lease after owner shutdown even though its linked lifetime token was cancelled.");
        }
        catch (OperationCanceledException)
        {
        }

        await disposeTask.WaitAsync(TimeSpan.FromSeconds(2));

        if (manager.ActiveProxyCount != 0)
        {
            throw new InvalidOperationException(
                $"Shutdown-racing successful connect retained {manager.ActiveProxyCount} consumer(s).");
        }

        if (controller.DisposeCount != 1 || controller.Current is not null)
        {
            throw new InvalidOperationException(
                "Shutdown-racing controller was not deterministically disposed after the rejected acquire.");
        }
    }

    private static async Task SharedLeaseDisconnectsOnlyAfterLastReleaseAsync()
    {
        var controller = BlockingVpnConnectionController.CreateReady();
        await using var manager = new VpnLeaseManager(CreateOptions(shared: true), controller);

        var first = await manager.AcquireAsync("proxy-a", CancellationToken.None);
        var second = await manager.AcquireAsync("proxy-b", CancellationToken.None);

        if (manager.ActiveProxyCount != 2)
        {
            throw new InvalidOperationException(
                $"Shared lease count was {manager.ActiveProxyCount}; expected 2.");
        }

        await first.DisposeAsync();
        if (manager.ActiveProxyCount != 1 || controller.DisconnectCount != 0)
        {
            throw new InvalidOperationException(
                "Releasing one shared consumer disconnected VPN before the last lease was released.");
        }

        await first.DisposeAsync();
        if (controller.DisconnectCount != 0)
        {
            throw new InvalidOperationException(
                "Idempotent shared lease disposal triggered an unexpected disconnect.");
        }

        await second.DisposeAsync();
        if (manager.ActiveProxyCount != 0 || controller.DisconnectCount != 1)
        {
            throw new InvalidOperationException(
                $"Last shared release produced active={manager.ActiveProxyCount}, " +
                $"disconnects={controller.DisconnectCount}; expected 0/1.");
        }

        await second.DisposeAsync();
        if (controller.DisconnectCount != 1)
        {
            throw new InvalidOperationException(
                "Repeated last-lease disposal duplicated VPN disconnect.");
        }
    }

    private static async Task DedicatedLeaseRejectsConcurrentConsumerAsync()
    {
        var controller = BlockingVpnConnectionController.CreateReady();
        await using var manager = new VpnLeaseManager(CreateOptions(shared: false), controller);

        var first = await manager.AcquireAsync("proxy-a", CancellationToken.None);
        try
        {
            _ = await manager.AcquireAsync("proxy-b", CancellationToken.None);
            throw new InvalidOperationException(
                "Dedicated VPN accepted a second concurrent proxy consumer.");
        }
        catch (InvalidOperationException ex) when (
            ex.Message.Contains("already leased", StringComparison.OrdinalIgnoreCase))
        {
        }

        await first.DisposeAsync();
        var second = await manager.AcquireAsync("proxy-b", CancellationToken.None);
        await second.DisposeAsync();

        if (controller.DisconnectCount != 2)
        {
            throw new InvalidOperationException(
                $"Dedicated sequential leases produced {controller.DisconnectCount} disconnects; expected 2.");
        }
    }

    private static async Task RepeatedReconnectFailuresRecoverAndStopAfterLastLeaseAsync()
    {
        const int reconnectCycles = 24;
        var contextReferences = await ExecuteReconnectChurnAsync(reconnectCycles);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var alive = contextReferences.Count(reference => reference.IsAlive);
        if (alive != 0)
        {
            throw new InvalidOperationException(
                $"Reconnect churn retained {alive} of {contextReferences.Length} replaced VPN contexts.");
        }
    }

    private static async Task<WeakReference[]> ExecuteReconnectChurnAsync(int reconnectCycles)
    {
        var controller = new CyclingReconnectVpnConnectionController();
        var manager = new VpnLeaseManager(
            new L2tpOptions
            {
                Id = "vpn-reconnect-churn",
                Name = "VPN reconnect churn",
                Shared = true
            },
            controller,
            TimeSpan.FromMilliseconds(10));

        try
        {
            var lease = await manager.AcquireAsync("proxy-reconnect", CancellationToken.None);
            var references = new WeakReference[reconnectCycles + 1];

            for (var cycle = 0; cycle < reconnectCycles; cycle++)
            {
                var current = controller.Current
                    ?? throw new InvalidOperationException($"Reconnect cycle {cycle} had no Ready context.");
                references[cycle] = new WeakReference(current);

                var nextGeneration = controller.Generation + 1;
                controller.FailCurrent(transientFailures: cycle % 3 + 1);
                await controller.WaitForGenerationAsync(nextGeneration, TimeSpan.FromSeconds(2));

                if (manager.ActiveProxyCount != 1 ||
                    controller.Current is not { IsAlive: true } ||
                    controller.State != VpnConnectionState.Ready)
                {
                    throw new InvalidOperationException(
                        $"Reconnect cycle {cycle} did not restore a Ready shared VPN while its lease remained active.");
                }
            }

            var finalContext = controller.Current
                ?? throw new InvalidOperationException("Reconnect churn lost its final Ready context.");
            references[^1] = new WeakReference(finalContext);

            var attemptsBeforeRelease = controller.ConnectAttemptCount;
            await lease.DisposeAsync();

            if (manager.ActiveProxyCount != 0 || controller.DisconnectCount != 1)
            {
                throw new InvalidOperationException(
                    $"Last release after reconnect churn produced active={manager.ActiveProxyCount}, " +
                    $"disconnects={controller.DisconnectCount}; expected 0/1.");
            }

            var attemptsAtRelease = controller.ConnectAttemptCount;
            await Task.Delay(TimeSpan.FromMilliseconds(80));
            if (controller.ConnectAttemptCount != attemptsAtRelease || attemptsAtRelease < attemptsBeforeRelease)
            {
                throw new InvalidOperationException(
                    "VPN maintenance continued reconnect attempts after the last lease was released.");
            }

            await manager.DisposeAsync();
            return references;
        }
        catch
        {
            await manager.DisposeAsync();
            throw;
        }
    }

    private static async Task RepeatedLeaseManagerCyclesBecomeCollectibleAsync()
    {
        const int cycleCount = 256;
        var references = await CreateAndDisposeLeaseManagersAsync(cycleCount);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var managerAlive = references.ManagerReferences.Count(reference => reference.IsAlive);
        var controllerAlive = references.ControllerReferences.Count(reference => reference.IsAlive);
        var contextAlive = references.ContextReferences.Count(reference => reference.IsAlive);

        if (managerAlive != 0 || controllerAlive != 0 || contextAlive != 0)
        {
            throw new InvalidOperationException(
                $"Released lease churn retained manager/controller/context objects: " +
                $"{managerAlive}/{controllerAlive}/{contextAlive} of {cycleCount}.");
        }
    }

    private static async Task<LeaseManagerWeakReferences> CreateAndDisposeLeaseManagersAsync(int count)
    {
        var managers = new WeakReference[count];
        var controllers = new WeakReference[count];
        var contexts = new WeakReference[count];

        for (var index = 0; index < count; index++)
        {
            var controller = BlockingVpnConnectionController.CreateReady();
            var manager = new VpnLeaseManager(
                new L2tpOptions
                {
                    Id = $"vpn-churn-{index}",
                    Name = $"VPN churn {index}",
                    Shared = true
                },
                controller);

            var lease = await manager.AcquireAsync($"proxy-{index}", CancellationToken.None);
            var context = controller.Current
                ?? throw new InvalidOperationException($"Cycle {index} did not create a VPN context.");

            managers[index] = new WeakReference(manager);
            controllers[index] = new WeakReference(controller);
            contexts[index] = new WeakReference(context);

            await lease.DisposeAsync();
            await manager.DisposeAsync();

            if (manager.ActiveProxyCount != 0 || !context.IsDisposed)
            {
                throw new InvalidOperationException(
                    $"Cycle {index} did not deterministically release lease/context resources.");
            }
        }

        return new LeaseManagerWeakReferences(managers, controllers, contexts);
    }

    private static L2tpOptions CreateOptions(bool shared) =>
        new()
        {
            Id = shared ? "vpn-shared-selftest" : "vpn-dedicated-selftest",
            Name = shared ? "Shared self-test VPN" : "Dedicated self-test VPN",
            Shared = shared
        };

    private readonly record struct LeaseManagerWeakReferences(
        WeakReference[] ManagerReferences,
        WeakReference[] ControllerReferences,
        WeakReference[] ContextReferences);

    private sealed class BlockingVpnConnectionController : IVpnConnectionController
    {
        private readonly bool _blockConnect;
        private readonly TaskCompletionSource _connectStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private VpnContext? _current;
        private int _disconnectCount;
        private int _disposeCount;
        private int _ownerCancellationObserved;

        private BlockingVpnConnectionController(bool blockConnect)
        {
            _blockConnect = blockConnect;
        }

        public static BlockingVpnConnectionController CreateBlocking() => new(blockConnect: true);
        public static BlockingVpnConnectionController CreateReady() => new(blockConnect: false);

        public Task ConnectStarted => _connectStarted.Task;
        public bool OwnerCancellationObserved => Volatile.Read(ref _ownerCancellationObserved) != 0;
        public int DisconnectCount => Volatile.Read(ref _disconnectCount);
        public int DisposeCount => Volatile.Read(ref _disposeCount);
        public VpnContext? Current => Volatile.Read(ref _current);
        public VpnConnectionState State => Current is { IsAlive: true }
            ? VpnConnectionState.Ready
            : VpnConnectionState.Disconnected;

        public async Task<VpnContext> ConnectAsync(CancellationToken cancellationToken)
        {
            _connectStarted.TrySetResult();

            if (_blockConnect)
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    Volatile.Write(ref _ownerCancellationObserved, 1);
                    throw;
                }

                throw new InvalidOperationException("Unreachable blocking VPN controller state.");
            }

            var current = Current;
            if (current is { IsAlive: true })
            {
                return current;
            }

            var created = CreateContext("self-test", 1);
            Volatile.Write(ref _current, created);
            return created;
        }

        public Task DisconnectAsync()
        {
            Interlocked.Increment(ref _disconnectCount);
            var current = Interlocked.Exchange(ref _current, null);
            current?.MarkDisconnected();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Increment(ref _disposeCount) != 1)
            {
                throw new InvalidOperationException("VPN controller was disposed more than once.");
            }

            var current = Interlocked.Exchange(ref _current, null);
            current?.MarkDisconnected();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class StubbornSuccessfulVpnConnectionController : IVpnConnectionController
    {
        private readonly TaskCompletionSource _connectStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _ownerCancellationObserved = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _allowSuccessfulReturn = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private VpnContext? _current;
        private int _disposeCount;

        public Task ConnectStarted => _connectStarted.Task;
        public Task OwnerCancellationObserved => _ownerCancellationObserved.Task;
        public int DisposeCount => Volatile.Read(ref _disposeCount);
        public VpnContext? Current => Volatile.Read(ref _current);
        public VpnConnectionState State => Current is { IsAlive: true }
            ? VpnConnectionState.Ready
            : VpnConnectionState.Disconnected;

        public async Task<VpnContext> ConnectAsync(CancellationToken cancellationToken)
        {
            _connectStarted.TrySetResult();
            using var registration = cancellationToken.Register(
                () => _ownerCancellationObserved.TrySetResult());

            // Deliberately ignore cancellation after observing it. This models a
            // lower layer completing concurrently with owner shutdown and proves
            // the lease layer has its own post-connect lifetime barrier.
            await _allowSuccessfulReturn.Task;

            var created = CreateContext("stubborn-success", 2);
            Volatile.Write(ref _current, created);
            return created;
        }

        public void AllowSuccessfulReturn() => _allowSuccessfulReturn.TrySetResult();

        public Task DisconnectAsync()
        {
            var current = Interlocked.Exchange(ref _current, null);
            current?.MarkDisconnected();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            var current = Interlocked.Exchange(ref _current, null);
            current?.MarkDisconnected();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CyclingReconnectVpnConnectionController : IVpnConnectionController
    {
        private readonly object _gate = new();
        private TaskCompletionSource _generationChanged = NewSignal();
        private VpnContext? _current;
        private int _generation;
        private int _transientFailuresRemaining;
        private int _connectAttemptCount;
        private int _disconnectCount;
        private int _disposed;

        public VpnContext? Current => Volatile.Read(ref _current);
        public VpnConnectionState State => Current is { IsAlive: true }
            ? VpnConnectionState.Ready
            : VpnConnectionState.Disconnected;
        public int Generation => Volatile.Read(ref _generation);
        public int ConnectAttemptCount => Volatile.Read(ref _connectAttemptCount);
        public int DisconnectCount => Volatile.Read(ref _disconnectCount);

        public Task<VpnContext> ConnectAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

            TaskCompletionSource? signal = null;
            VpnContext context;
            lock (_gate)
            {
                if (_current is { IsAlive: true } current)
                {
                    return Task.FromResult(current);
                }

                Interlocked.Increment(ref _connectAttemptCount);
                if (_transientFailuresRemaining > 0)
                {
                    _transientFailuresRemaining--;
                    throw new InvalidOperationException("Injected transient reconnect failure.");
                }

                var generation = ++_generation;
                context = CreateContext($"reconnect-{generation}", 100 + generation);
                Volatile.Write(ref _current, context);
                signal = _generationChanged;
                _generationChanged = NewSignal();
            }

            signal.TrySetResult();
            return Task.FromResult(context);
        }

        public void FailCurrent(int transientFailures)
        {
            if (transientFailures < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(transientFailures));
            }

            VpnContext? current;
            lock (_gate)
            {
                _transientFailuresRemaining = transientFailures;
                current = Interlocked.Exchange(ref _current, null);
            }

            current?.MarkDisconnected();
        }

        public async Task WaitForGenerationAsync(int expectedGeneration, TimeSpan timeout)
        {
            using var timeoutCancellation = new CancellationTokenSource(timeout);
            while (true)
            {
                Task signal;
                lock (_gate)
                {
                    if (_generation >= expectedGeneration)
                    {
                        return;
                    }

                    signal = _generationChanged.Task;
                }

                await signal.WaitAsync(timeoutCancellation.Token);
            }
        }

        public Task DisconnectAsync()
        {
            Interlocked.Increment(ref _disconnectCount);
            VpnContext? current;
            lock (_gate)
            {
                current = Interlocked.Exchange(ref _current, null);
            }
            current?.MarkDisconnected();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return ValueTask.CompletedTask;
            }

            VpnContext? current;
            lock (_gate)
            {
                current = Interlocked.Exchange(ref _current, null);
            }
            current?.MarkDisconnected();
            return ValueTask.CompletedTask;
        }

        private static TaskCompletionSource NewSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static VpnContext CreateContext(string entryName, int interfaceIndex) =>
        new(
            entryName,
            IPAddress.Loopback,
            new VpnInterfaceInfo(
                entryName,
                entryName,
                interfaceIndex,
                Array.Empty<IPAddress>()));
}
