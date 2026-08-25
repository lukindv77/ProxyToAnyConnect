using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace ProxyToAnyConnect.Vpn;

internal static class VpnInterfaceResolver
{
    public static VpnInterfaceInfo ResolveByAddress(IPAddress localIPv4)
    {
        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            IPInterfaceProperties properties;
            try
            {
                properties = networkInterface.GetIPProperties();
            }
            catch (NetworkInformationException)
            {
                continue;
            }

            var ownsAddress = properties.UnicastAddresses.Any(
                address => address.Address.AddressFamily == AddressFamily.InterNetwork &&
                           address.Address.Equals(localIPv4));

            if (!ownsAddress)
            {
                continue;
            }

            var ipv4Properties = properties.GetIPv4Properties()
                ?? throw new InvalidOperationException(
                    $"Network interface '{networkInterface.Name}' has no IPv4 properties.");

            var dnsServers = properties.DnsAddresses
                .Where(address => address.AddressFamily == AddressFamily.InterNetwork)
                .Distinct()
                .ToArray();

            return new VpnInterfaceInfo(
                networkInterface.Name,
                networkInterface.Description,
                ipv4Properties.Index,
                dnsServers);
        }

        throw new InvalidOperationException(
            $"Unable to map RAS IPv4 address {localIPv4} to a Windows network interface.");
    }
}

internal sealed record VpnInterfaceInfo(
    string Name,
    string Description,
    int InterfaceIndex,
    IReadOnlyList<IPAddress> DnsServers);
