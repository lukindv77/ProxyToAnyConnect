using System.Net;
using System.Net.Sockets;
using ProxyToAnyConnect.Network;
using ProxyToAnyConnect.Vpn;

namespace ProxyToAnyConnect.SelfTests;

internal static class Program
{
    public static int Main()
    {
        var tests = new (string Name, Action Test)[]
        {
            ("L2TP split-tunnel profile is accepted", AcceptsSplitTunnelL2tp),
            ("Full-tunnel profile is rejected", RejectsFullTunnel),
            ("Non-L2TP profile is rejected", RejectsNonL2tp),
            ("Unchanged default routes are accepted", AcceptsUnchangedDefaultRoutes),
            ("Changed default routes are rejected", RejectsChangedDefaultRoutes),
            ("Zero interface index is rejected", RejectsZeroInterfaceIndex),
            ("IPv4 public address enables IP comparison", PublicIpv4EnablesComparison),
            ("DNS public address skips IP comparison", PublicDnsSkipsComparison)
        };

        var failed = 0;
        foreach (var (name, test) in tests)
        {
            try
            {
                test();
                Console.WriteLine($"PASS: {name}");
            }
            catch (Exception ex)
            {
                failed++;
                Console.Error.WriteLine($"FAIL: {name}: {ex}");
            }
        }

        Console.WriteLine($"Self-tests complete. Passed: {tests.Length - failed}; Failed: {failed}.");
        return failed == 0 ? 0 : 1;
    }

    private static void AcceptsSplitTunnelL2tp()
    {
        WindowsVpnProfileInspector.ValidateForProxy(
            new VpnProfileInfo("Test", "L2tp", SplitTunneling: true, AllUserConnection: false));
    }

    private static void RejectsFullTunnel()
    {
        AssertThrows<InvalidOperationException>(() =>
            WindowsVpnProfileInspector.ValidateForProxy(
                new VpnProfileInfo("Test", "L2tp", SplitTunneling: false, AllUserConnection: false)));
    }

    private static void RejectsNonL2tp()
    {
        AssertThrows<InvalidOperationException>(() =>
            WindowsVpnProfileInspector.ValidateForProxy(
                new VpnProfileInfo("Test", "Sstp", SplitTunneling: true, AllUserConnection: false)));
    }

    private static void AcceptsUnchangedDefaultRoutes()
    {
        var routes = new DefaultRouteSnapshot(
            [new DefaultRouteEntry(7, "192.168.1.1", 0, "ActiveStore")]);

        WindowsDefaultRouteInspector.EnsureUnchanged(routes, routes);
    }

    private static void RejectsChangedDefaultRoutes()
    {
        var before = new DefaultRouteSnapshot(
            [new DefaultRouteEntry(7, "192.168.1.1", 0, "ActiveStore")]);
        var after = new DefaultRouteSnapshot(
            [
                new DefaultRouteEntry(7, "192.168.1.1", 0, "ActiveStore"),
                new DefaultRouteEntry(42, "0.0.0.0", 1, "ActiveStore")
            ]);

        AssertThrows<InvalidOperationException>(() =>
            WindowsDefaultRouteInspector.EnsureUnchanged(before, after));
    }

    private static void RejectsZeroInterfaceIndex()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        AssertThrows<ArgumentOutOfRangeException>(() =>
            WindowsSocketInterfaceBinder.BindToIPv4Interface(socket, 0));
    }

    private static void PublicIpv4EnablesComparison()
    {
        var address = VpnConnectivityVerifier.TryGetExpectedPublicIPv4("198.51.100.25");
        if (!IPAddress.Parse("198.51.100.25").Equals(address))
        {
            throw new InvalidOperationException("Expected public IPv4 was not recognized.");
        }
    }

    private static void PublicDnsSkipsComparison()
    {
        var address = VpnConnectivityVerifier.TryGetExpectedPublicIPv4("vpn.example.com");
        if (address is not null)
        {
            throw new InvalidOperationException("DNS public address must not enable IPv4 equality checking.");
        }
    }

    private static void AssertThrows<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Expected exception {typeof(TException).Name} was not thrown.");
    }
}
