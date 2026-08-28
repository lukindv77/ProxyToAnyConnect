using System.Net;
using ProxyToAnyConnect.Network;
using ProxyToAnyConnect.Vpn;

namespace ProxyToAnyConnect.SelfTests;

internal static class DnsCacheSelfTests
{
    public static int Run()
    {
        var failed = 0;
        failed += RunCase("DNS cache honors TTL expiry", HonorsTtlExpiry);
        failed += RunCase("DNS cache ignores wall-clock rollback for TTL", WallClockRollbackDoesNotExtendTtl);
        failed += RunCase("DNS cache enforces hard capacity", EnforcesCapacity);
        failed += RunCase("DNS cache resets on VPN context change", ResetsOnContextChange);
        return failed;
    }

    private static int RunCase(string name, Action test)
    {
        try
        {
            test();
            Console.WriteLine($"PASS: {name}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: {name}: {ex}");
            return 1;
        }
    }

    private static void HonorsTtlExpiry()
    {
        using var context = CreateContext("10.20.30.25", 42);
        var clock = new ManualTimeProvider();
        var cache = new L2tpDnsCache(maxEntries: 4, timeProvider: clock);
        cache.Set(
            "example.com",
            context,
            [IPAddress.Parse("203.0.113.10")],
            TimeSpan.FromSeconds(30));

        clock.Advance(TimeSpan.FromSeconds(29));
        if (!cache.TryGet("example.com", context, out var beforeExpiry) ||
            beforeExpiry.Count != 1)
        {
            throw new InvalidOperationException("DNS cache entry expired too early.");
        }

        clock.Advance(TimeSpan.FromSeconds(1));
        if (cache.TryGet("example.com", context, out _) || cache.Count != 0)
        {
            throw new InvalidOperationException("DNS cache entry survived its TTL boundary.");
        }
    }

    private static void WallClockRollbackDoesNotExtendTtl()
    {
        using var context = CreateContext("10.20.30.25", 42);
        var clock = new ManualTimeProvider();
        var cache = new L2tpDnsCache(maxEntries: 4, timeProvider: clock);
        cache.Set(
            "rollback.example",
            context,
            [IPAddress.Parse("198.51.100.17")],
            TimeSpan.FromSeconds(30));

        clock.Advance(TimeSpan.FromSeconds(20));
        if (!cache.TryGet("rollback.example", context, out _))
        {
            throw new InvalidOperationException("DNS cache entry expired before elapsed TTL during setup.");
        }

        clock.ShiftUtc(TimeSpan.FromDays(-7));
        clock.Advance(TimeSpan.FromSeconds(10));
        if (cache.TryGet("rollback.example", context, out _))
        {
            throw new InvalidOperationException(
                "Seven-day wall-clock rollback extended a DNS entry beyond 30 seconds of monotonic elapsed time.");
        }
    }

    private static void EnforcesCapacity()
    {
        using var context = CreateContext("10.20.30.25", 42);
        var clock = new ManualTimeProvider();
        var cache = new L2tpDnsCache(maxEntries: 2, timeProvider: clock);

        cache.Set("one.example", context, [IPAddress.Parse("203.0.113.1")], TimeSpan.FromMinutes(1));
        cache.Set("two.example", context, [IPAddress.Parse("203.0.113.2")], TimeSpan.FromMinutes(1));
        clock.Advance(TimeSpan.FromSeconds(1));
        _ = cache.TryGet("two.example", context, out _);
        clock.Advance(TimeSpan.FromSeconds(1));
        cache.Set("three.example", context, [IPAddress.Parse("203.0.113.3")], TimeSpan.FromMinutes(1));

        if (cache.Count != 2)
        {
            throw new InvalidOperationException($"Expected 2 DNS entries, got {cache.Count}.");
        }

        clock.Advance(TimeSpan.FromSeconds(1));
        if (cache.TryGet("one.example", context, out _))
        {
            throw new InvalidOperationException("Least-recently-used DNS entry was not evicted.");
        }

        if (!cache.TryGet("two.example", context, out _) ||
            !cache.TryGet("three.example", context, out _))
        {
            throw new InvalidOperationException("DNS cache evicted a newer entry instead of the LRU entry.");
        }
    }

    private static void ResetsOnContextChange()
    {
        using var first = CreateContext("10.20.30.25", 42);
        using var second = CreateContext("10.20.30.26", 43);
        var clock = new ManualTimeProvider();
        var cache = new L2tpDnsCache(maxEntries: 4, timeProvider: clock);

        cache.Set("example.com", first, [IPAddress.Parse("203.0.113.10")], TimeSpan.FromMinutes(5));
        if (!cache.TryGet("example.com", first, out _))
        {
            throw new InvalidOperationException("Initial VPN-context cache entry was not stored.");
        }

        if (cache.TryGet("example.com", second, out _) || cache.Count != 0)
        {
            throw new InvalidOperationException("DNS cache was not cleared for a new VPN context.");
        }
    }

    private static VpnContext CreateContext(string localIpv4, int interfaceIndex) =>
        new(
            "SelfTest",
            IPAddress.Parse(localIpv4),
            new VpnInterfaceInfo(
                "SelfTest",
                "SelfTest",
                interfaceIndex,
                [IPAddress.Parse("10.0.0.53")]));

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;
        private DateTimeOffset _utcNow = new(2026, 8, 28, 0, 0, 0, TimeSpan.Zero);

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan elapsed)
        {
            Interlocked.Add(ref _timestamp, elapsed.Ticks);
            _utcNow += elapsed;
        }

        public void ShiftUtc(TimeSpan delta) => _utcNow += delta;
    }
}
