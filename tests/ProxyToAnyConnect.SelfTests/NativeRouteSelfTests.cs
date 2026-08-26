using System.Net;
using ProxyToAnyConnect.Vpn;

namespace ProxyToAnyConnect.SelfTests;

internal static class NativeRouteSelfTests
{
    public static async Task<int> RunAsync()
    {
        try
        {
            NetworkOrderAddressConversionWorks();
            await CapturesWindowsDefaultRoutesAsync();
            Console.WriteLine("PASS: native Windows IPv4 route-table inspection");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: native Windows IPv4 route-table inspection: {ex}");
            return 1;
        }
    }

    private static void NetworkOrderAddressConversionWorks()
    {
        var raw = BitConverter.ToUInt32(new byte[] { 192, 0, 2, 17 });
        var address = WindowsDefaultRouteInspector.NetworkUInt32ToIPv4(raw);
        if (!address.Equals(IPAddress.Parse("192.0.2.17")))
        {
            throw new InvalidOperationException(
                $"Network-order IPv4 conversion returned {address} instead of 192.0.2.17.");
        }
    }

    private static async Task CapturesWindowsDefaultRoutesAsync()
    {
        var inspector = new WindowsDefaultRouteInspector();
        var snapshot = await inspector.CaptureIPv4Async(CancellationToken.None);

        if (snapshot.Routes.Count == 0)
        {
            throw new InvalidOperationException("Windows runner has no IPv4 default route in GetIpForwardTable output.");
        }

        foreach (var route in snapshot.Routes)
        {
            if (route.InterfaceIndex == 0)
            {
                throw new InvalidOperationException("A captured default route has interface index 0.");
            }

            if (!IPAddress.TryParse(route.NextHop, out var nextHop) ||
                nextHop.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            {
                throw new InvalidOperationException($"Invalid default-route next hop '{route.NextHop}'.");
            }

            if (!string.Equals(route.Source, "GetIpForwardTable", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Unexpected route snapshot source '{route.Source}'.");
            }
        }
    }
}
