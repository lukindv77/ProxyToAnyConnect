using System.Net;
using ProxyToAnyConnect.Configuration;
using ProxyToAnyConnect.Runtime;
using ProxyToAnyConnect.Vpn;

namespace ProxyToAnyConnect.SelfTests;

internal static class VpnSharedFailClosedSelfTests
{
    public static async Task<int> RunAsync()
    {
        try
        {
            await SharedFailureCancelsDependentsAndLastReleaseStopsReconnectAsync();
            Console.WriteLine(
                "PASS: shared VPN fail-closed cancels dependent sessions, isolates unrelated context and stops reconnect after last lease");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: shared VPN fail-closed lifecycle regression: {ex}");
            return 1;
        }
    }

    private static async Task SharedFailureCancelsDependentsAndLastReleaseStopsReconnectAsync()
    {
        var controller = new FailClosedVpnController();
        await using var manager = new VpnLeaseManager(
            new L2tpOptions
            {
                Id = "shared-failclosed-selftest",
                Name = "Shared fail-closed self-test",
                Shared = true
            },
            controller);

        var firstLease = await manager.AcquireAsync("proxy-a", CancellationToken.None);
        var secondLease = await manager.AcquireAsync("proxy-b", CancellationToken.None);
        var sharedContext = controller.Current
            ?? throw new InvalidOperationException("Shared controller did not publish the initial context.");

        if (!sharedContext.TryAcquireConnectionReference() ||
            !sharedContext.TryAcquireConnectionReference())
        {
            throw new InvalidOperationException("Unable to simulate two dependent outbound tunnels.");
        }

        var unrelated = CreateContext("unrelated", 99);
        try
        {
            controller.FailCurrentAndBlockReconnect();

            if (!sharedContext.LifetimeToken.IsCancellationRequested || sharedContext.IsAlive)
            {
                throw new InvalidOperationException(
                    "Shared fail-closed transition did not cancel the exact dependent context token.");
            }

            if (!unrelated.IsAlive || unrelated.LifetimeToken.IsCancellationRequested)
            {
                throw new InvalidOperationException(
                    "Failing the shared group cancelled an unrelated VPN context.");
            }

            await controller.ReconnectStarted.WaitAsync(TimeSpan.FromSeconds(2));

            await firstLease.DisposeAsync();
            if (manager.ActiveProxyCount != 1)
            {
                throw new InvalidOperationException(
                    $"Expected one active shared lease after first release, got {manager.ActiveProxyCount}.");
            }

            if (controller.ReconnectCancellationObserved)
            {
                throw new InvalidOperationException(
                    "Reconnect was cancelled while one shared proxy lease was still active.");
            }

            await secondLease.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

            if (manager.ActiveProxyCount != 0)
            {
                throw new InvalidOperationException(
                    $"Last shared release retained {manager.ActiveProxyCount} active lease(s).");
            }

            if (!controller.ReconnectCancellationObserved)
            {
                throw new InvalidOperationException(
                    "Last shared release did not cancel the in-progress reconnect attempt.");
            }

            if (controller.SuccessfulReconnectCount != 0)
            {
                throw new InvalidOperationException(
                    "A reconnect completed after the shared VPN had been invalidated and all leases released.");
            }

            if (!unrelated.IsAlive)
            {
                throw new InvalidOperationException(
                    "Unrelated context became invalid during shared reconnect teardown.");
            }
        }
        finally
        {
            sharedContext.ReleaseConnectionReference();
            sharedContext.ReleaseConnectionReference();
            unrelated.MarkDisconnected();
        }

        if (!sharedContext.IsDisposed || sharedContext.ReferenceCount != 0)
        {
            throw new InvalidOperationException(
                $"Released shared context retained resources: disposed={sharedContext.IsDisposed}, refs={sharedContext.ReferenceCount}.");
        }
    }

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

    private sealed class FailClosedVpnController : IVpnConnectionController
    {
        private readonly TaskCompletionSource _reconnectStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private VpnContext? _current;
        private int _blockReconnect;
        private int _reconnectCancellationObserved;
        private int _successfulReconnectCount;
        private int _disposed;

        public Task ReconnectStarted => _reconnectStarted.Task;
        public bool ReconnectCancellationObserved =>
            Volatile.Read(ref _reconnectCancellationObserved) != 0;
        public int SuccessfulReconnectCount => Volatile.Read(ref _successfulReconnectCount);
        public VpnContext? Current => Volatile.Read(ref _current);
        public VpnConnectionState State => Current is { IsAlive: true }
            ? VpnConnectionState.Ready
            : VpnConnectionState.Disconnected;

        public async Task<VpnContext> ConnectAsync(CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

            var current = Current;
            if (current is { IsAlive: true })
            {
                return current;
            }

            if (Volatile.Read(ref _blockReconnect) != 0)
            {
                _reconnectStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    Volatile.Write(ref _reconnectCancellationObserved, 1);
                    throw;
                }

                throw new InvalidOperationException("Unreachable blocked reconnect state.");
            }

            var created = CreateContext("shared", 42);
            Volatile.Write(ref _current, created);
            Interlocked.Increment(ref _successfulReconnectCount);
            return created;
        }

        public void FailCurrentAndBlockReconnect()
        {
            Volatile.Write(ref _blockReconnect, 1);
            var current = Interlocked.Exchange(ref _current, null)
                ?? throw new InvalidOperationException("No current VPN context to fail.");
            current.MarkDisconnected();
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
