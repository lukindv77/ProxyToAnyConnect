using System.Net;
using System.Net.Sockets;
using System.Text;
using ProxyToAnyConnect.Network;
using ProxyToAnyConnect.Proxy;
using ProxyToAnyConnect.Vpn;

namespace ProxyToAnyConnect.SelfTests;

internal static class Program
{
    private const ushort DnsTransactionId = 0x1234;

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
            ("DNS public address skips IP comparison", PublicDnsSkipsComparison),
            ("DNS A response returns IPv4", DnsAResponseReturnsIpv4),
            ("DNS CNAME response returns canonical name", DnsCnameResponseReturnsCanonicalName),
            ("Truncated DNS response requests TCP fallback", DnsTruncatedResponseIsDetected),
            ("CONNECT authority uses default HTTPS port", ProxyAuthorityUsesDefaultPort),
            ("CONNECT authority accepts explicit port", ProxyAuthorityAcceptsExplicitPort),
            ("IPv6 proxy authority is rejected", ProxyAuthorityRejectsIpv6),
            ("Origin request strips proxy-only headers", ProxyOriginHeaderStripsProxyHeaders)
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

    private static void DnsAResponseReturnsIpv4()
    {
        var packet = BuildDnsResponse(
            answerType: 1,
            answerData: [203, 0, 113, 7]);

        var parsed = L2tpDnsResolver.ParseResponse(packet, DnsTransactionId);
        if (parsed.Truncated ||
            parsed.Addresses.Count != 1 ||
            !parsed.Addresses[0].Equals(IPAddress.Parse("203.0.113.7")))
        {
            throw new InvalidOperationException("DNS A response was not parsed correctly.");
        }
    }

    private static void DnsCnameResponseReturnsCanonicalName()
    {
        var cnameData = EncodeDnsName("target.example.com");
        var packet = BuildDnsResponse(
            answerType: 5,
            answerData: cnameData);

        var parsed = L2tpDnsResolver.ParseResponse(packet, DnsTransactionId);
        if (parsed.Truncated ||
            !string.Equals(parsed.CanonicalName, "target.example.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("DNS CNAME response was not parsed correctly.");
        }
    }

    private static void DnsTruncatedResponseIsDetected()
    {
        var packet = new byte[12];
        packet[0] = 0x12;
        packet[1] = 0x34;
        packet[2] = 0x82; // QR + TC.
        packet[3] = 0x00;

        var parsed = L2tpDnsResolver.ParseResponse(packet, DnsTransactionId);
        if (!parsed.Truncated)
        {
            throw new InvalidOperationException("DNS TC flag was not detected.");
        }
    }

    private static void ProxyAuthorityUsesDefaultPort()
    {
        var (host, port) = ProxyServer.ParseAuthority("example.com", 443);
        if (host != "example.com" || port != 443)
        {
            throw new InvalidOperationException("Default CONNECT authority parsing failed.");
        }
    }

    private static void ProxyAuthorityAcceptsExplicitPort()
    {
        var (host, port) = ProxyServer.ParseAuthority("example.com:8443", 443);
        if (host != "example.com" || port != 8443)
        {
            throw new InvalidOperationException("Explicit CONNECT authority parsing failed.");
        }
    }

    private static void ProxyAuthorityRejectsIpv6()
    {
        AssertThrows<NotSupportedException>(() =>
            ProxyServer.ParseAuthority("[2001:db8::1]:443", 443));
    }

    private static void ProxyOriginHeaderStripsProxyHeaders()
    {
        var raw = Encoding.ASCII.GetBytes(
            "GET http://example.com/path HTTP/1.1\r\n" +
            "Host: example.com\r\n" +
            "Proxy-Authorization: Basic secret\r\n" +
            "Proxy-Connection: keep-alive\r\n" +
            "Connection: X-Remove\r\n" +
            "X-Remove: do-not-forward\r\n" +
            "X-Keep: yes\r\n\r\n");

        var request = ProxyServer.ParsedProxyRequest.Parse(raw);
        var outbound = Encoding.Latin1.GetString(request.BuildOriginHeader("/path"));

        if (outbound.Contains("Proxy-Authorization", StringComparison.OrdinalIgnoreCase) ||
            outbound.Contains("Proxy-Connection", StringComparison.OrdinalIgnoreCase) ||
            outbound.Contains("X-Remove:", StringComparison.OrdinalIgnoreCase) ||
            !outbound.Contains("X-Keep: yes", StringComparison.OrdinalIgnoreCase) ||
            !outbound.Contains("Connection: close", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Origin header filtering failed.");
        }
    }

    private static byte[] BuildDnsResponse(ushort answerType, byte[] answerData)
    {
        var packet = new List<byte>();

        AddUInt16(packet, DnsTransactionId);
        AddUInt16(packet, 0x8180); // Standard successful recursive response.
        AddUInt16(packet, 1); // QDCOUNT.
        AddUInt16(packet, 1); // ANCOUNT.
        AddUInt16(packet, 0); // NSCOUNT.
        AddUInt16(packet, 0); // ARCOUNT.

        packet.AddRange(EncodeDnsName("example.com"));
        AddUInt16(packet, 1); // QTYPE A.
        AddUInt16(packet, 1); // QCLASS IN.

        packet.Add(0xC0);
        packet.Add(0x0C); // Answer NAME points to the question name.
        AddUInt16(packet, answerType);
        AddUInt16(packet, 1); // CLASS IN.
        AddUInt32(packet, 60); // TTL.
        AddUInt16(packet, checked((ushort)answerData.Length));
        packet.AddRange(answerData);

        return packet.ToArray();
    }

    private static byte[] EncodeDnsName(string name)
    {
        var bytes = new List<byte>();
        foreach (var label in name.Split('.'))
        {
            var labelBytes = Encoding.ASCII.GetBytes(label);
            bytes.Add(checked((byte)labelBytes.Length));
            bytes.AddRange(labelBytes);
        }

        bytes.Add(0);
        return bytes.ToArray();
    }

    private static void AddUInt16(List<byte> bytes, ushort value)
    {
        bytes.Add((byte)(value >> 8));
        bytes.Add((byte)value);
    }

    private static void AddUInt32(List<byte> bytes, uint value)
    {
        bytes.Add((byte)(value >> 24));
        bytes.Add((byte)(value >> 16));
        bytes.Add((byte)(value >> 8));
        bytes.Add((byte)value);
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
