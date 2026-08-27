using System.Net;
using System.Net.Sockets;
using System.Text;
using ProxyToAnyConnect.Configuration;
using ProxyToAnyConnect.Network;
using ProxyToAnyConnect.Proxy;

namespace ProxyToAnyConnect.SelfTests;

internal static class ProxyHttpHeaderValueSelfTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public static async Task<int> RunAsync()
    {
        try
        {
            ParserRejectsEveryForbiddenFieldValueControl();
            ParserAcceptsTabAndLatin1ObsText();
            await MalformedFieldValueIsRejectedBeforeOutboundConnectAsync();
            Console.WriteLine("PASS: HTTP field-value controls are rejected before outbound routing");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: HTTP field-value control regression: {ex}");
            return 1;
        }
    }

    private static void ParserRejectsEveryForbiddenFieldValueControl()
    {
        for (var octet = 0; octet < 0x20; octet++)
        {
            if (octet == 0x09)
            {
                continue;
            }

            AssertRejected(BuildRequest((byte)octet, connect: false), $"HTTP CTL 0x{octet:X2}");
            AssertRejected(BuildRequest((byte)octet, connect: true), $"CONNECT CTL 0x{octet:X2}");
        }

        AssertRejected(BuildRequest(0x7F, connect: false), "HTTP DEL 0x7F");
        AssertRejected(BuildRequest(0x7F, connect: true), "CONNECT DEL 0x7F");
    }

    private static void ParserAcceptsTabAndLatin1ObsText()
    {
        var prefix = Encoding.ASCII.GetBytes(
            "GET http://example.test/ HTTP/1.1\r\nHost: example.test\r\nX-Test: A");
        var suffix = Encoding.ASCII.GetBytes(" C\r\n\r\n");
        var request = new byte[prefix.Length + 4 + suffix.Length];
        prefix.CopyTo(request, 0);
        request[prefix.Length] = 0x09;
        request[prefix.Length + 1] = (byte)'B';
        request[prefix.Length + 2] = 0x80;
        request[prefix.Length + 3] = 0xFF;
        suffix.CopyTo(request, prefix.Length + 4);

        var parsed = ProxyServer.ParsedProxyRequest.Parse(request);
        var forwarded = parsed.BuildOriginHeader("/", "example.test");
        var expectedValue = new byte[] { (byte)'A', 0x09, (byte)'B', 0x80, 0xFF, (byte)' ', (byte)'C' };
        if (!ContainsHeaderValue(forwarded, "X-Test", expectedValue))
        {
            throw new InvalidOperationException("HTAB/Latin-1 obs-text value was not accepted and preserved.");
        }

        var connect = Encoding.Latin1.GetBytes(
            "CONNECT example.test:443 HTTP/1.1\r\nX-Test: A\tB\u0080\u00FF C\r\n\r\n");
        _ = ProxyServer.ParsedProxyRequest.Parse(connect);
    }

    private static async Task MalformedFieldValueIsRejectedBeforeOutboundConnectAsync()
    {
        using var timeout = new CancellationTokenSource(Timeout);
        var factory = new CountingRejectingFactory();
        var port = ReserveLoopbackPort();
        using var proxyCancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var proxy = new ProxyServer(
            new ProxyOptions
            {
                ListenAddress = "127.0.0.1",
                ListenPort = port,
                MaxConcurrentConnections = 4,
                MaxHeaderBytes = 65536,
                ClientHeaderTimeoutSeconds = 5
            },
            factory);
        var proxyTask = proxy.RunAsync(proxyCancellation.Token);
        await proxy.WaitUntilListeningAsync(timeout.Token);

        try
        {
            using var client = new TcpClient { NoDelay = true };
            await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
            await using var stream = client.GetStream();
            await stream.WriteAsync(BuildRequest(0x00, connect: false), timeout.Token);

            var response = Encoding.Latin1.GetString(await ReadToEndAsync(stream, timeout.Token));
            if (!response.StartsWith("HTTP/1.1 400 Bad Request\r\n", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Malformed field value was not rejected as 400:\n{response}");
            }

            if (factory.ConnectCount != 0)
            {
                throw new InvalidOperationException(
                    $"Malformed field value opened {factory.ConnectCount} outbound connection(s).");
            }
        }
        finally
        {
            proxyCancellation.Cancel();
            await proxyTask;
        }
    }

    private static byte[] BuildRequest(byte valueOctet, bool connect)
    {
        var start = Encoding.ASCII.GetBytes(
            connect
                ? "CONNECT example.test:443 HTTP/1.1\r\nX-Test: before"
                : "GET http://example.test/ HTTP/1.1\r\nHost: example.test\r\nX-Test: before");
        var end = Encoding.ASCII.GetBytes("after\r\n\r\n");
        var result = new byte[start.Length + 1 + end.Length];
        start.CopyTo(result, 0);
        result[start.Length] = valueOctet;
        end.CopyTo(result, start.Length + 1);
        return result;
    }

    private static void AssertRejected(byte[] request, string name)
    {
        try
        {
            _ = ProxyServer.ParsedProxyRequest.Parse(request);
        }
        catch (InvalidDataException)
        {
            return;
        }

        throw new InvalidOperationException($"{name} was accepted inside a header field value.");
    }

    private static bool ContainsHeaderValue(byte[] header, string name, byte[] expectedValue)
    {
        var prefix = Encoding.ASCII.GetBytes(name + ": ");
        for (var i = 0; i <= header.Length - prefix.Length - expectedValue.Length - 2; i++)
        {
            if (!header.AsSpan(i, prefix.Length).SequenceEqual(prefix))
            {
                continue;
            }

            var valueStart = i + prefix.Length;
            return header.AsSpan(valueStart, expectedValue.Length).SequenceEqual(expectedValue) &&
                   header[valueStart + expectedValue.Length] == (byte)'\r' &&
                   header[valueStart + expectedValue.Length + 1] == (byte)'\n';
        }

        return false;
    }

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

    private static async Task<byte[]> ReadToEndAsync(Stream stream, CancellationToken cancellationToken)
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

    private sealed class CountingRejectingFactory : IProxyOutboundConnectionFactory
    {
        private int _connectCount;
        public int ConnectCount => Volatile.Read(ref _connectCount);

        public Task<IProxyOutboundConnection> ConnectAsync(
            string host,
            int port,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _connectCount);
            throw new InvalidOperationException("Malformed headers must be rejected before outbound connect.");
        }
    }
}
