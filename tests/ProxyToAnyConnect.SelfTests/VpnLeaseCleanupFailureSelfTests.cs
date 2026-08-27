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
            await DisposeReleasesIndependentOwnersWhenControllerFailsAsync();

            Console.WriteLine(
                "PASS: VPN lease teardown releases cache/status/token ownership even when controller cleanup fails");
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

        if (!LifetimeSourceWasDisposed(lifetime))
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

    private static CancellationTokenSource GetPrivateLifetime(VpnLeaseManager manager)
    {
        var field = typeof(VpnLeaseManager).GetField(
            "_lifetime",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(VpnLeaseManager).FullName, "_lifetime");
        return field.GetValue(manager) as CancellationTokenSource
            ?? throw new InvalidOperationException("VPN manager lifetime field had an unexpected value.");
    }

    private static bool LifetimeSourceWasDisposed(CancellationTokenSource source)
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
