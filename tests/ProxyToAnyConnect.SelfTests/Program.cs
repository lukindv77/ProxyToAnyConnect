using System.Net;
using System.Net.Sockets;
using System.Text;
using ProxyToAnyConnect.Configuration;
using ProxyToAnyConnect.Network;
using ProxyToAnyConnect.Proxy;
using ProxyToAnyConnect.Vpn;

namespace ProxyToAnyConnect.SelfTests;

internal static class Program
{
    private const ushort DnsTransactionId = 0x1234;
    private static readonly TimeSpan IntegrationTimeout = TimeSpan.FromSeconds(10);

    public static async Task<int> Main()
    {
        var tests = new (string Name, Func<Task> Test)[]
        {
            ("L2TP split-tunnel profile is accepted", AsAsync(AcceptsSplitTunnelL2tp)),
            ("Full-tunnel profile is rejected", AsAsync(RejectsFullTunnel)),
            ("Non-L2TP profile is rejected", AsAsync(RejectsNonL2tp)),
            ("Unchanged default routes are accepted", AsAsync(AcceptsUnchangedDefaultRoutes)),
            ("Changed default routes are rejected", AsAsync(RejectsChangedDefaultRoutes)),
            ("Zero interface index is rejected", AsAsync(RejectsZeroInterfaceIndex)),
            ("IPv4 public address enables IP comparison", AsAsync(PublicIpv4EnablesComparison)),
            ("DNS public address skips IP comparison", AsAsync(PublicDnsSkipsComparison)),
            ("DNS A response returns IPv4", AsAsync(DnsAResponseReturnsIpv4)),
            ("DNS CNAME response returns canonical name", AsAsync(DnsCnameResponseReturnsCanonicalName)),
            ("Truncated DNS response requests TCP fallback", AsAsync(DnsTruncatedResponseIsDetected)),
            ("CONNECT authority uses default HTTPS port", AsAsync(ProxyAuthorityUsesDefaultPort)),
            ("CONNECT authority accepts explicit port", AsAsync(ProxyAuthorityAcceptsExplicitPort)),
            ("IPv6 proxy authority is rejected", AsAsync(ProxyAuthorityRejectsIpv6)),
            ("Origin request strips proxy-only headers", AsAsync(ProxyOriginHeaderStripsProxyHeaders)),
            ("Plain HTTP is forwarded end-to-end through proxy", PlainHttpIsForwardedEndToEndAsync),
            ("CONNECT creates a bidirectional TCP tunnel", ConnectCreatesBidirectionalTunnelAsync)
        };

        var failed = 0;
        foreach (var (name, test) in tests)
        {
            try
            {
                await test();
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

    private static Func<Task> AsAsync(Action action) => () =>
    {
        action();
        return Task.CompletedTask;
    };

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

        var parsed = L2tpDnsResolver.ParseResponse(packet, DnsTransactionId, "example.com");
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

        var parsed = L2tpDnsResolver.ParseResponse(packet, DnsTransactionId, "example.com");
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

        var parsed = L2tpDnsResolver.ParseResponse(packet, DnsTransactionId, "example.com");
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

    private static async Task PlainHttpIsForwardedEndToEndAsync()
    {
        using var timeout = new CancellationTokenSource(IntegrationTimeout);
        using var originListener = new TcpListener(IPAddress.Loopback, 0);
        originListener.Start();
        var originPort = ((IPEndPoint)originListener.LocalEndpoint).Port;

        var factory = new LoopbackOutboundFactory(originPort);
        var proxyPort = ReserveLoopbackPort();
        using var proxyCancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var proxy = CreateTestProxy(proxyPort, factory);
        var proxyTask = proxy.RunAsync(proxyCancellation.Token);

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, proxyPort, timeout.Token);
            await using var clientStream = client.GetStream();

            var request = Encoding.ASCII.GetBytes(
                "GET http://example.com/path?q=1 HTTP/1.1\r\n" +
                "Host: example.com\r\n" +
                "Proxy-Authorization: Basic secret\r\n" +
                "Connection: X-Remove\r\n" +
                "X-Remove: no\r\n" +
                "X-Keep: yes\r\n\r\n");
            await clientStream.WriteAsync(request, timeout.Token);

            using var originClient = await originListener.AcceptTcpClientAsync(timeout.Token);
            await using var originStream = originClient.GetStream();
            var originHeader = Encoding.Latin1.GetString(
                await ReadHeaderAsync(originStream, timeout.Token));

            if (!originHeader.StartsWith("GET /path?q=1 HTTP/1.1\r\n", StringComparison.Ordinal) ||
                originHeader.Contains("Proxy-Authorization", StringComparison.OrdinalIgnoreCase) ||
                originHeader.Contains("X-Remove:", StringComparison.OrdinalIgnoreCase) ||
                !originHeader.Contains("X-Keep: yes", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Unexpected forwarded HTTP header:\n{originHeader}");
            }

            var response = Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nOK");
            await originStream.WriteAsync(response, timeout.Token);
            originClient.Client.Shutdown(SocketShutdown.Send);

            var clientResponse = Encoding.Latin1.GetString(
                await ReadToEndAsync(clientStream, timeout.Token));
            if (!clientResponse.Contains("200 OK", StringComparison.Ordinal) ||
                !clientResponse.EndsWith("OK", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Unexpected proxy response:\n{clientResponse}");
            }

            factory.AssertLastTarget("example.com", 80);
        }
        finally
        {
            proxyCancellation.Cancel();
            await proxyTask;
        }
    }

    private static async Task ConnectCreatesBidirectionalTunnelAsync()
    {
        using var timeout = new CancellationTokenSource(IntegrationTimeout);
        using var originListener = new TcpListener(IPAddress.Loopback, 0);
        originListener.Start();
        var originPort = ((IPEndPoint)originListener.LocalEndpoint).Port;

        var factory = new LoopbackOutboundFactory(originPort);
        var proxyPort = ReserveLoopbackPort();
        using var proxyCancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var proxy = CreateTestProxy(proxyPort, factory);
        var proxyTask = proxy.RunAsync(proxyCancellation.Token);

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, proxyPort, timeout.Token);
            await using var clientStream = client.GetStream();

            var connectRequest = Encoding.ASCII.GetBytes(
                "CONNECT example.com:443 HTTP/1.1\r\nHost: example.com:443\r\n\r\n");
            await clientStream.WriteAsync(connectRequest, timeout.Token);

            using var originClient = await originListener.AcceptTcpClientAsync(timeout.Token);
            await using var originStream = originClient.GetStream();

            var connectResponse = Encoding.Latin1.GetString(
                await ReadHeaderAsync(clientStream, timeout.Token));
            if (!connectResponse.StartsWith("HTTP/1.1 200 Connection Established", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Unexpected CONNECT response: {connectResponse}");
            }

            var clientPayload = Encoding.ASCII.GetBytes("client-through-tunnel");
            await clientStream.WriteAsync(clientPayload, timeout.Token);
            var receivedAtOrigin = new byte[clientPayload.Length];
            await ReadExactlyAsync(originStream, receivedAtOrigin, timeout.Token);
            if (!receivedAtOrigin.AsSpan().SequenceEqual(clientPayload))
            {
                throw new InvalidOperationException("CONNECT tunnel corrupted client-to-origin bytes.");
            }

            var originPayload = Encoding.ASCII.GetBytes("origin-through-tunnel");
            await originStream.WriteAsync(originPayload, timeout.Token);
            var receivedAtClient = new byte[originPayload.Length];
            await ReadExactlyAsync(clientStream, receivedAtClient, timeout.Token);
            if (!receivedAtClient.AsSpan().SequenceEqual(originPayload))
            {
                throw new InvalidOperationException("CONNECT tunnel corrupted origin-to-client bytes.");
            }

            factory.AssertLastTarget("example.com", 443);
        }
        finally
        {
            proxyCancellation.Cancel();
            await proxyTask;
        }
    }

    private static ProxyServer CreateTestProxy(
        int proxyPort,
        IProxyOutboundConnectionFactory factory) =>
        new(
            new ProxyOptions
            {
                ListenAddress = "127.0.0.1",
                ListenPort = proxyPort,
                MaxHeaderBytes = 65536
            },
            factory);

    private static int ReserveLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task<byte[]> ReadHeaderAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var oneByte = new byte[1];

        while (buffer.Length < 65536)
        {
            var read = await stream.ReadAsync(oneByte, cancellationToken);
            if (read == 0)
            {
                throw new IOException("Stream ended before HTTP header was complete.");
            }

            buffer.WriteByte(oneByte[0]);
            if (buffer.Length >= 4)
            {
                var data = buffer.GetBuffer();
                var length = checked((int)buffer.Length);
                if (data[length - 4] == (byte)'\r' &&
                    data[length - 3] == (byte)'\n' &&
                    data[length - 2] == (byte)'\r' &&
                    data[length - 1] == (byte)'\n')
                {
                    return buffer.ToArray();
                }
            }
        }

        throw new IOException("HTTP header exceeded test limit.");
    }

    private static async Task<byte[]> ReadToEndAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken);
            if (read == 0)
            {
                return buffer.ToArray();
            }

            buffer.Write(chunk, 0, read);
        }
    }

    private static async Task ReadExactlyAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], cancellationToken);
            if (read == 0)
            {
                throw new IOException("Stream ended before expected payload was received.");
            }

            offset += read;
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

    private sealed class LoopbackOutboundFactory : IProxyOutboundConnectionFactory
    {
        private readonly int _originPort;
        private string? _lastHost;
        private int _lastPort;

        public LoopbackOutboundFactory(int originPort)
        {
            _originPort = originPort;
        }

        public async Task<IProxyOutboundConnection> ConnectAsync(
            string host,
            int port,
            CancellationToken cancellationToken)
        {
            _lastHost = host;
            _lastPort = port;

            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true
            };

            try
            {
                await socket.ConnectAsync(
                    new IPEndPoint(IPAddress.Loopback, _originPort),
                    cancellationToken);
                return new TestOutboundConnection(socket);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        public void AssertLastTarget(string expectedHost, int expectedPort)
        {
            if (!string.Equals(_lastHost, expectedHost, StringComparison.OrdinalIgnoreCase) ||
                _lastPort != expectedPort)
            {
                throw new InvalidOperationException(
                    $"Unexpected outbound target. Expected {expectedHost}:{expectedPort}, got {_lastHost}:{_lastPort}.");
            }
        }
    }

    private sealed class TestOutboundConnection : IProxyOutboundConnection
    {
        public TestOutboundConnection(Socket socket)
        {
            Socket = socket;
        }

        public Socket Socket { get; }
        public CancellationToken LifetimeToken => CancellationToken.None;

        public ValueTask DisposeAsync()
        {
            Socket.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
