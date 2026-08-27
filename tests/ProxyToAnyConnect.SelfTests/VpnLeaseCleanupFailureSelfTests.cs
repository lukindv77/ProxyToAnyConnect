using System.Net;
using System.Reflection;
using ProxyToAnyConnect.Configuration;
using ProxyToAnyConnect.Diagnostics;
using ProxyToAnyConnect.Runtime;
using ProxyToAnyConnect.Vpn;

namespace ProxyToAnyConnect.SelfTests;

internal static class VpnLeaseCleanupFailureSelfTests
{
    public static async Task<int> RunAsync()
    {
        try
        {
            await LastReleaseClearsCacheWhenDisconnectFailsAsync();
            await LastReleaseDrainsMaintenanceWhenCancellationCallbackThrowsAsync();
            await DisposeReleasesIndependentOwnersWhenControllerFailsAsync();
            await DisposeContinuesWhenLifetimeCancellationCallbackThrowsAsync();

            Console.WriteLine(
                "PASS: VPN lease teardown releases maintenance/cache/status/token ownership through cleanup and cancellation callback faults");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: VPN lease cleanup-failure regression: {ex}");
            return 1;
        }
    }

    private static async Task LastReleaseClearsCacheWhenDisconnectFailsAsync()
    {
        var controller = new CleanupFailureController
        {
            DisconnectFailure = new SyntheticCleanupException("disconnect failed")
        };
        await using var manager = new VpnLeaseManager(
            CreateOptions("cleanup-release"),
            controller,
            TimeSpan.FromSeconds(30));

        var lease = await manager.AcquireAsync("proxy-a", CancellationToken.None);
        SeedDnsCache(manager, controller.Current!);

        try
        {
            await lease.DisposeAsync();
            throw new InvalidOperationException(
                "Synthetic disconnect failure was not propagated from last-lease release.");
        }
        catch (SyntheticCleanupException ex) when (ex.Message == "disconnect failed")
        {
        }

        if (manager.ActiveProxyCount != 0)
        {
            throw new InvalidOperationException(
                $"Failed last-release teardown retained {manager.ActiveProxyCount} active lease(s).");
        }

        if (manager.DnsCache.Count != 0)
        {
            throw new InvalidOperationException(
                $"Failed last-release teardown retained {manager.DnsCache.Count} DNS cache entry(s).");
        }

        if (controller.DisconnectCount != 1)
        {
            throw new InvalidOperationException(
                $"Expected one disconnect attempt, got {controller.DisconnectCount}.");
        }
    }

    private static async Task LastReleaseDrainsMaintenanceWhenCancellationCallbackThrowsAsync()
    {
        var controller = new CleanupFailureController();
        var manager = new VpnLeaseManager(
            CreateOptions("cleanup-maintenance-cancel"),
            controller,
            TimeSpan.FromMinutes(5));
        var lease = await manager.AcquireAsync("proxy-a", CancellationToken.None);
        var maintenanceCancellation = GetPrivateField<CancellationTokenSource>(
            manager,
            "_maintenanceCancellation");
        var maintenanceTask = GetPrivateField<Task>(manager, "_maintenanceTask");
        _ = maintenanceCancellation.Token.Register(
            static () => throw new SyntheticCleanupException("maintenance cancellation callback failed"));
        SeedDnsCache(manager, controller.Current!);

        try
        {
            await lease.DisposeAsync();
            throw new InvalidOperationException(
                "Throwing maintenance cancellation callback was not surfaced from last-lease release.");
        }
        catch (AggregateException ex) when (
            ex.InnerExceptions.Any(inner =>
                inner is SyntheticCleanupException synthetic &&
                synthetic.Message == "maintenance cancellation callback failed"))
        {
        }

        if (!maintenanceTask.IsCompleted || !CancellationSourceWasDisposed(maintenanceCancellation))
        {
            throw new InvalidOperationException(
                "Throwing maintenance cancellation callback prevented exact task drain or CTS disposal.");
        }

        if (GetPrivateField<CancellationTokenSource?>(manager, "_maintenanceCancellation") is not null ||
            GetPrivateField<Task?>(manager, "_maintenanceTask") is not null)
        {
            throw new InvalidOperationException(
                "Throwing maintenance cancellation callback retained published maintenance ownership.");
        }

        if (manager.ActiveProxyCount != 0 || manager.DnsCache.Count != 0 || controller.DisconnectCount != 1)
        {
            throw new InvalidOperationException(
                "Throwing maintenance cancellation callback prevented last-lease disconnect/cache cleanup.");
        }

        await manager.DisposeAsync();
    }

    private static async Task DisposeReleasesIndependentOwnersWhenControllerFailsAsync()
    {
        var vpnId = $"cleanup-dispose-{Guid.NewGuid():N}";
        var controller = new CleanupFailureController
        {
            DisposeFailure = new SyntheticCleanupException("controller dispose failed")
        };
        var manager = new VpnLeaseManager(
            CreateOptions(vpnId),
            controller,
            TimeSpan.FromSeconds(30));

        var lease = await manager.AcquireAsync("proxy-a", CancellationToken.None);
        SeedDnsCache(manager, controller.Current!);
        VpnLatestStatusRegistry.UpdateKeepaliveSuccess(
            vpnId,
            "10.42.0.1",
            TimeSpan.FromMilliseconds(12));
        if (VpnLatestStatusRegistry.Get(vpnId) is null)
        {
            throw new InvalidOperationException("Self-test could not seed latest-status ownership.");
        }

        var lifetime = GetPrivateLifetime(manager);
        try
        {
            await manager.DisposeAsync();
            throw new InvalidOperationException(
                "Synthetic controller disposal failure was not propagated.");
        }
        catch (SyntheticCleanupException ex) when (ex.Message == "controller dispose failed")
        {
        }

        if (manager.ActiveProxyCount != 0)
        {
            throw new InvalidOperationException(
                $"Failed manager disposal retained {manager.ActiveProxyCount} active lease(s).");
        }

        if (manager.DnsCache.Count != 0)
        {
            throw new InvalidOperationException(
                $"Failed manager disposal retained {manager.DnsCache.Count} DNS cache entry(s).");
        }

        if (VpnLatestStatusRegistry.Get(vpnId) is not null)
        {
            throw new InvalidOperationException(
                "Failed manager disposal retained its latest-status registry entry.");
        }

        if (!CancellationSourceWasDisposed(lifetime))
        {
            throw new InvalidOperationException(
                "Failed manager disposal retained its lifetime CancellationTokenSource.");
        }

        if (controller.DisposeCount != 1)
        {
            throw new InvalidOperationException(
                $"Expected one controller dispose attempt, got {controller.DisposeCount}.");
        }

        await lease.DisposeAsync();
    }

    private static async Task DisposeContinuesWhenLifetimeCancellationCallbackThrowsAsync()
    {
        var vpnId = $"cleanup-lifetime-cancel-{Guid.NewGuid():N}";
        var controller = new CleanupFailureController
        {
            DisposeFailure = new SyntheticCleanupException("secondary controller dispose failed")
        };
        var manager = new VpnLeaseManager(
            CreateOptions(vpnId),
            controller,
            TimeSpan.FromMinutes(5));
        var lease = await manager.AcquireAsync("proxy-a", CancellationToken.None);
        SeedDnsCache(manager, controller.Current!);
        VpnLatestStatusRegistry.UpdateKeepaliveSuccess(
            vpnId,
            "10.42.0.1",
            TimeSpan.FromMilliseconds(8));

        var lifetime = GetPrivateLifetime(manager);
        _ = lifetime.Token.Register(
            static () => throw new SyntheticCleanupException("lifetime cancellation callback failed"));

        try
        {
            await manager.DisposeAsync();
            throw new InvalidOperationException(
                "Throwing lifetime cancellation callback was not propagated from manager disposal.");
        }
        catch (AggregateException ex) when (
            ex.InnerExceptions.Any(inner =>
                inner is SyntheticCleanupException synthetic &&
                synthetic.Message == "lifetime cancellation callback failed"))
        {
            if (!ex.Data.Contains("VpnCleanup:connection-dispose"))
            {
                throw new InvalidOperationException(
                    "Secondary controller cleanup failure was not attached to the primary lifetime cancellation defect.");
            }
        }

        if (manager.ActiveProxyCount != 0 ||
            manager.DnsCache.Count != 0 ||
            VpnLatestStatusRegistry.Get(vpnId) is not null ||
            controller.DisposeCount != 1 ||
            !CancellationSourceWasDisposed(lifetime))
        {
            throw new InvalidOperationException(
                "Throwing manager lifetime cancellation callback prevented independent VPN ownership cleanup.");
        }

        await lease.DisposeAsync();
    }

    private static void SeedDnsCache(VpnLeaseManager manager, VpnContext context)
    {
        manager.DnsCache.Set(
            "cleanup.example",
            context,
            [IPAddress.Parse("203.0.113.20")],
            TimeSpan.FromMinutes(1));
        if (manager.DnsCache.Count != 1)
        {
            throw new InvalidOperationException("Self-test could not seed VPN DNS cache ownership.");
        }
    }

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

    private static CancellationTokenSource GetPrivateLifetime(VpnLeaseManager manager) =>
        GetPrivateField<CancellationTokenSource>(manager, "_lifetime");

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

    private static L2tpOptions CreateOptions(string id) =>
        new()
        {
            Id = id,
            Name = id,
            Shared = true
        };

    private static VpnContext CreateContext(int interfaceIndex) =>
        new(
            $"cleanup-{interfaceIndex}",
            IPAddress.Parse("10.42.0.2"),
            new VpnInterfaceInfo(
                $"if-{interfaceIndex}",
                $"if-{interfaceIndex}",
                interfaceIndex,
                [IPAddress.Parse("10.42.0.1")]),
            IPAddress.Parse("10.42.0.254"));

    private sealed class CleanupFailureController : IVpnConnectionController
    {
        private VpnContext? _current;
        private int _nextInterfaceIndex = 100;

        public Exception? DisconnectFailure { get; init; }
        public Exception? DisposeFailure { get; init; }
        public int DisconnectCount { get; private set; }
        public int DisposeCount { get; private set; }
        public VpnContext? Current => Volatile.Read(ref _current);
        public VpnConnectionState State => Current is { IsAlive: true }
            ? VpnConnectionState.Ready
            : VpnConnectionState.Disconnected;

        public Task<VpnContext> ConnectAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = Current;
            if (current is { IsAlive: true })
            {
                return Task.FromResult(current);
            }

            var created = CreateContext(Interlocked.Increment(ref _nextInterfaceIndex));
            Volatile.Write(ref _current, created);
            return Task.FromResult(created);
        }

        public Task DisconnectAsync()
        {
            DisconnectCount++;
            var current = Interlocked.Exchange(ref _current, null);
            current?.MarkDisconnected();
            return DisconnectFailure is null
                ? Task.CompletedTask
                : Task.FromException(DisconnectFailure);
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            var current = Interlocked.Exchange(ref _current, null);
            current?.MarkDisconnected();
            return DisposeFailure is null
                ? ValueTask.CompletedTask
                : ValueTask.FromException(DisposeFailure);
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
