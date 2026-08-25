using System.Net;
using System.Net.Sockets;

namespace ProxyToAnyConnect.Network;

internal static class WindowsSocketInterfaceBinder
{
    // Winsock IP_UNICAST_IF. System.Net.Sockets does not currently expose a named
    // SocketOptionName value for this Windows-specific IPv4 option.
    private const SocketOptionName IpUnicastInterface = (SocketOptionName)31;

    public static void BindToIPv4Interface(Socket socket, int interfaceIndex)
    {
        ArgumentNullException.ThrowIfNull(socket);

        if (interfaceIndex <= 0 || interfaceIndex > 0x00FF_FFFF)
        {
            throw new ArgumentOutOfRangeException(
                nameof(interfaceIndex),
                interfaceIndex,
                "IPv4 interface index must be a non-zero 24-bit Windows interface index.");
        }

        // IP_UNICAST_IF expects the interface index encoded as a DWORD in network byte order.
        var networkOrderInterfaceIndex = IPAddress.HostToNetworkOrder(interfaceIndex);
        socket.SetSocketOption(
            SocketOptionLevel.IP,
            IpUnicastInterface,
            networkOrderInterfaceIndex);
    }
}
