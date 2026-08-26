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
            await SharedLeaseDisconnectsOnlyAfterLastReleaseAsync();
            await DedicatedLeaseRejectsConcurrentConsumerAsync();

            Console.WriteLine(
                "PASS: VPN lease manager owner cancellation, shared last-release and dedicated exclusivity");
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

    private static L2tpOptions CreateOptions(bool shared) =>
        new()
        {
            Id = shared ? "vpn-shared-selftest" : "vpn-dedicated-selftest",
            Name = shared ? "Shared self-test VPN" : "Dedicated self-test VPN",
            Shared = shared
        };

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

            var created = new VpnContext(
                "self-test",
                IPAddress.Loopback,
                new VpnInterfaceInfo(
                    "self-test",
                    "self-test",
                    1,
                    Array.Empty<IPAddress>()));
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
}
