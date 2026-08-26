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
        var cache = new L2tpDnsCache(maxEntries: 4);
        var now = new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);
        cache.Set(
            "example.com",
            context,
            [IPAddress.Parse("203.0.113.10")],
            TimeSpan.FromSeconds(30),
            now);

        if (!cache.TryGet("example.com", context, out var beforeExpiry, now.AddSeconds(29)) ||
            beforeExpiry.Count != 1)
        {
            throw new InvalidOperationException("DNS cache entry expired too early.");
        }

        if (cache.TryGet("example.com", context, out _, now.AddSeconds(30)) || cache.Count != 0)
        {
            throw new InvalidOperationException("DNS cache entry survived its TTL.");
        }
    }

    private static void EnforcesCapacity()
    {
        using var context = CreateContext("10.20.30.25", 42);
        var cache = new L2tpDnsCache(maxEntries: 2);
        var now = DateTimeOffset.UtcNow;

        cache.Set("one.example", context, [IPAddress.Parse("203.0.113.1")], TimeSpan.FromMinutes(1), now);
        cache.Set("two.example", context, [IPAddress.Parse("203.0.113.2")], TimeSpan.FromMinutes(1), now);
        _ = cache.TryGet("two.example", context, out _, now.AddSeconds(1));
        cache.Set("three.example", context, [IPAddress.Parse("203.0.113.3")], TimeSpan.FromMinutes(1), now.AddSeconds(2));

        if (cache.Count != 2)
        {
            throw new InvalidOperationException($"Expected 2 DNS entries, got {cache.Count}.");
        }

        if (cache.TryGet("one.example", context, out _, now.AddSeconds(3)))
        {
            throw new InvalidOperationException("Least-recently-used DNS entry was not evicted.");
        }

        if (!cache.TryGet("two.example", context, out _, now.AddSeconds(3)) ||
            !cache.TryGet("three.example", context, out _, now.AddSeconds(3)))
        {
            throw new InvalidOperationException("DNS cache evicted a newer entry instead of the LRU entry.");
        }
    }

    private static void ResetsOnContextChange()
    {
        using var first = CreateContext("10.20.30.25", 42);
        using var second = CreateContext("10.20.30.26", 43);
        var cache = new L2tpDnsCache(maxEntries: 4);
        var now = DateTimeOffset.UtcNow;

        cache.Set("example.com", first, [IPAddress.Parse("203.0.113.10")], TimeSpan.FromMinutes(5), now);
        if (!cache.TryGet("example.com", first, out _, now))
        {
            throw new InvalidOperationException("Initial VPN-context cache entry was not stored.");
        }

        if (cache.TryGet("example.com", second, out _, now) || cache.Count != 0)
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
}
