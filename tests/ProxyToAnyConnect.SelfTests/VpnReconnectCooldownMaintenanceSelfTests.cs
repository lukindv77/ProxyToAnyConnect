using System.Net;
using ProxyToAnyConnect.Configuration;
using ProxyToAnyConnect.Runtime;
using ProxyToAnyConnect.Vpn;

namespace ProxyToAnyConnect.SelfTests;

internal static class VpnReconnectCooldownMaintenanceSelfTests
{
    public static async Task<int> RunAsync()
    {
        try
        {
            await MaintenanceWaitsUntilCooldownExpiresAsync();
            await LastLeaseReleaseCancelsCooldownWaitAsync();

            Console.WriteLine(
                "PASS: VPN lease maintenance waits out reconnect cooldown and cancels the wait on last release");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: reconnect cooldown maintenance regression: {ex}");
            return 1;
        }
    }

    private static async Task MaintenanceWaitsUntilCooldownExpiresAsync()
    {
        var controller = new CooldownVpnController(TimeSpan.FromMilliseconds(180));
        await using var manager = new VpnLeaseManager(
            CreateOptions("cooldown-expiry"),
            controller,
            TimeSpan.FromMilliseconds(10));

        var lease = await manager.AcquireAsync("proxy-a", CancellationToken.None);
        try
        {
            if (controller.ConnectCallCount != 1)
            {
                throw new InvalidOperationException(
                    $"Initial lease expected exactly one ConnectAsync call, got {controller.ConnectCallCount}.");
            }

            controller.FailCurrentAndArmCooldown();
            await controller.CooldownObserved.WaitAsync(TimeSpan.FromSeconds(1));

            await Task.Delay(TimeSpan.FromMilliseconds(50));
            if (controller.ConnectCallCount != 1)
            {
                throw new InvalidOperationException(
                    "Lease maintenance called ConnectAsync before the reconnect cooldown expired.");
            }

            await controller.ReconnectStarted.WaitAsync(TimeSpan.FromSeconds(1));
            if (controller.ConnectCallCount != 2)
            {
                throw new InvalidOperationException(
                    $"Expected one reconnect after cooldown eligibility, got {controller.ConnectCallCount - 1}.");
            }
        }
        finally
        {
            await lease.DisposeAsync();
        }
    }

    private static async Task LastLeaseReleaseCancelsCooldownWaitAsync()
    {
        var controller = new CooldownVpnController(TimeSpan.FromSeconds(5));
        await using var manager = new VpnLeaseManager(
            CreateOptions("cooldown-release"),
            controller,
            TimeSpan.FromMilliseconds(10));

        var lease = await manager.AcquireAsync("proxy-a", CancellationToken.None);
        controller.FailCurrentAndArmCooldown();
        await controller.CooldownObserved.WaitAsync(TimeSpan.FromSeconds(1));

        await lease.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1));

        if (controller.ConnectCallCount != 1)
        {
            throw new InvalidOperationException(
                "Last-lease release allowed a reconnect while cancelling the active cooldown wait.");
        }

        if (manager.ActiveProxyCount != 0)
        {
            throw new InvalidOperationException(
                $"Last-lease release retained {manager.ActiveProxyCount} active proxy lease(s).");
        }
    }

    private static L2tpOptions CreateOptions(string id) =>
        new()
        {
            Id = id,
            Name = id,
            Shared = true
        };

    private static VpnContext CreateContext(string entryName, int interfaceIndex) =>
        new(
            entryName,
            IPAddress.Parse("10.42.0.2"),
            new VpnInterfaceInfo(
                $"if-{interfaceIndex}",
                $"if-{interfaceIndex}",
                interfaceIndex,
                [IPAddress.Parse("10.42.0.1")]),
            IPAddress.Parse("10.42.0.254"));

    private sealed class CooldownVpnController : IVpnConnectionController
    {
        private readonly long _cooldownMilliseconds;
        private readonly TaskCompletionSource _cooldownObserved = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _reconnectStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        private VpnContext? _current;
        private long _retryNotBefore;
        private int _connectCallCount;
        private int _nextInterfaceIndex = 60;
        private int _disposed;

        public CooldownVpnController(TimeSpan cooldown)
        {
            _cooldownMilliseconds = checked((long)Math.Ceiling(cooldown.TotalMilliseconds));
        }

        public Task CooldownObserved => _cooldownObserved.Task;
        public Task ReconnectStarted => _reconnectStarted.Task;
        public int ConnectCallCount => Volatile.Read(ref _connectCallCount);
        public VpnContext? Current => Volatile.Read(ref _current);
        public VpnConnectionState State => Current is { IsAlive: true }
            ? VpnConnectionState.Ready
            : VpnConnectionState.Disconnected;

        public long ReconnectCooldownRemainingMilliseconds
        {
            get
            {
                var remaining = Volatile.Read(ref _retryNotBefore) - Environment.TickCount64;
                if (remaining > 0)
                {
                    _cooldownObserved.TrySetResult();
                    return remaining;
                }

                return 0;
            }
        }

        public Task<VpnContext> ConnectAsync(CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            cancellationToken.ThrowIfCancellationRequested();

            var existing = Current;
            if (existing is { IsAlive: true })
            {
                return Task.FromResult(existing);
            }

            var call = Interlocked.Increment(ref _connectCallCount);
            if (call > 1)
            {
                _reconnectStarted.TrySetResult();
            }

            Volatile.Write(ref _retryNotBefore, 0);
            var created = CreateContext(
                $"cooldown-{call}",
                Interlocked.Increment(ref _nextInterfaceIndex));
            Volatile.Write(ref _current, created);
            return Task.FromResult(created);
        }

        public void FailCurrentAndArmCooldown()
        {
            var current = Interlocked.Exchange(ref _current, null)
                ?? throw new InvalidOperationException("No active VPN context to invalidate.");
            current.MarkDisconnected();
            Volatile.Write(
                ref _retryNotBefore,
                checked(Environment.TickCount64 + _cooldownMilliseconds));
        }

        public Task DisconnectAsync()
        {
            var current = Interlocked.Exchange(ref _current, null);
            current?.MarkDisconnected();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return ValueTask.CompletedTask;
            }

            var current = Interlocked.Exchange(ref _current, null);
            current?.MarkDisconnected();
            return ValueTask.CompletedTask;
        }
    }
}
