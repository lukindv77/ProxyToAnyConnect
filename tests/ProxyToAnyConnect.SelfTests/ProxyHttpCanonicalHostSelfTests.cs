using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ProxyToAnyConnect.Configuration;
using ProxyToAnyConnect.Network;
using ProxyToAnyConnect.Proxy;

namespace ProxyToAnyConnect.SelfTests;

internal static class ProxyHttpCanonicalHostSelfTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public static async Task<int> RunAsync()
    {
        try
        {
            CanonicalTargetMatrix();
            await MalformedHostsDoNotRouteAsync();
            await CanonicalHostDrivesRoutingAndGeneratedHostAsync();
            Console.WriteLine("PASS: plain-HTTP absolute hosts are canonicalized before routing and Host regeneration");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: plain-HTTP canonical host regression: {ex}");
            return 1;
        }
    }

    private static void CanonicalTargetMatrix()
    {
        AssertTarget("http://EXAMPLE.TEST/path", "example.test", 80, "example.test", "/path");
        AssertTarget("http://example.test./path?q=1", "example.test", 80, "example.test", "/path?q=1");
        AssertTarget("http://münich.example./idn", "xn--mnich-kva.example", 80, "xn--mnich-kva.example", "/idn");
        AssertTarget("http://127.0.0.1:8080/ip", "127.0.0.1", 8080, "127.0.0.1:8080", "/ip");

        var longLabel = new string('a', 64) + ".example";
        foreach (var target in new[]
        {
            "http://bad_host.test/",
            "http://-bad.test/",
            "http://bad-.test/",
            "http://example..test/",
            "http://example.test../",
            $"http://{longLabel}/"
        })
        {
            AssertTargetRejected(target);
        }
    }

    private static async Task MalformedHostsDoNotRouteAsync()
    {
        using var timeout = new CancellationTokenSource(Timeout);
        var factory = new CountingRejectingFactory();
        var proxyPort = ReserveLoopbackPort();
        using var proxyCancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var proxy = CreateProxy(proxyPort, factory);
        var proxyTask = proxy.RunAsync(proxyCancellation.Token);
        await proxy.WaitUntilListeningAsync(timeout.Token);

        try
        {
            foreach (var target in new[]
            {
                "http://bad_host.test/",
                "http://-bad.test/",
                "http://bad-.test/",
                "http://example.test../"
            })
            {
                var response = await SendProxyRequestAsync(proxyPort, target, timeout.Token);
                if (!response.StartsWith("HTTP/1.1 400 Bad Request\r\n", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"Malformed absolute host '{target}' was not rejected as 400: {response}");
                }
            }

            if (factory.ConnectCount != 0)
            {
                throw new InvalidOperationException(
                    $"Malformed absolute hosts opened {factory.ConnectCount} outbound connection(s).");
            }
        }
        finally
        {
            proxyCancellation.Cancel();
            await proxyTask;
        }
    }

    private static async Task CanonicalHostDrivesRoutingAndGeneratedHostAsync()
    {
        using var timeout = new CancellationTokenSource(Timeout);
        var origin = new TcpListener(IPAddress.Loopback, 0);
        origin.Start();
        var originPort = ((IPEndPoint)origin.LocalEndpoint).Port;
        var factory = new RedirectingCaptureFactory(originPort);
        var proxyPort = ReserveLoopbackPort();
        using var proxyCancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var proxy = CreateProxy(proxyPort, factory);
        var proxyTask = proxy.RunAsync(proxyCancellation.Token);
        await proxy.WaitUntilListeningAsync(timeout.Token);

        var cases = new[]
        {
            new RouteCase("http://EXAMPLE.TEST.:8080/a?q=1", "example.test", 8080, "Host: example.test:8080\r\n"),
            new RouteCase("http://münich.example./idn", "xn--mnich-kva.example", 80, "Host: xn--mnich-kva.example\r\n"),
            new RouteCase("http://127.0.0.1:8123/ip", "127.0.0.1", 8123, "Host: 127.0.0.1:8123\r\n")
        };

        try
        {
            foreach (var testCase in cases)
            {
                var originTask = AcceptSingleOriginRequestAsync(origin, timeout.Token);
                var response = await SendProxyRequestAsync(proxyPort, testCase.Target, timeout.Token);
                var originRequest = await originTask;
                if (!response.StartsWith("HTTP/1.1 200 OK\r\n", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"Canonical route '{testCase.Target}' did not complete through loopback origin: {response}");
                }

                if (!originRequest.Contains(testCase.ExpectedHostHeader, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Origin Host for '{testCase.Target}' was not generated from the canonical routing host:\n{originRequest}");
                }
            }

            var captures = factory.Captures.ToArray();
            if (captures.Length != cases.Length)
            {
                throw new InvalidOperationException($"Expected {cases.Length} outbound captures, observed {captures.Length}.");
            }

            for (var i = 0; i < cases.Length; i++)
            {
                if (!captures[i].Host.Equals(cases[i].ExpectedHost, StringComparison.Ordinal) ||
                    captures[i].Port != cases[i].ExpectedPort)
                {
                    throw new InvalidOperationException(
                        $"Target '{cases[i].Target}' routed as {captures[i].Host}:{captures[i].Port}, expected {cases[i].ExpectedHost}:{cases[i].ExpectedPort}.");
                }
            }
        }
        finally
        {
            proxyCancellation.Cancel();
            await proxyTask;
            origin.Stop();
        }
    }

    private static ProxyServer CreateProxy(int port, IProxyOutboundConnectionFactory factory) =>
        new(
            new ProxyOptions
            {
                ListenAddress = "127.0.0.1",
                ListenPort = port,
                MaxConcurrentConnections = 8,
                MaxHeaderBytes = 65536,
                ClientHeaderTimeoutSeconds = 5
            },
            factory);

    private static void AssertTarget(
        string target,
        string expectedHost,
        int expectedPort,
        string expectedAuthority,
        string expectedPath)
    {
        var parsed = ProxyServer.ParseHttpTarget(target);
        if (!parsed.Host.Equals(expectedHost, StringComparison.Ordinal) ||
            parsed.Port != expectedPort ||
            !parsed.Authority.Equals(expectedAuthority, StringComparison.Ordinal) ||
            !parsed.PathAndQuery.Equals(expectedPath, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"HTTP target '{target}' canonicalized to {parsed.Host}:{parsed.Port} / '{parsed.Authority}' / '{parsed.PathAndQuery}'.");
        }
    }

    private static void AssertTargetRejected(string target)
    {
        try
        {
            _ = ProxyServer.ParseHttpTarget(target);
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
        {
            return;
        }

        throw new InvalidOperationException($"Malformed absolute HTTP target host was accepted: '{target}'.");
    }

    private static async Task<string> SendProxyRequestAsync(
        int proxyPort,
        string target,
        CancellationToken cancellationToken)
    {
        using var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync(IPAddress.Loopback, proxyPort, cancellationToken);
        await using var stream = client.GetStream();
        var request = Encoding.Latin1.GetBytes(
            $"GET {target} HTTP/1.1\r\n" +
            "Host: attacker.invalid\r\n" +
            "Connection: close\r\n\r\n");
        await stream.WriteAsync(request, cancellationToken);
        return Encoding.Latin1.GetString(await ReadToEndAsync(stream, cancellationToken));
    }

    private static async Task<string> AcceptSingleOriginRequestAsync(
        TcpListener listener,
        CancellationToken cancellationToken)
    {
        using var client = await listener.AcceptTcpClientAsync(cancellationToken);
        await using var stream = client.GetStream();
        var requestBytes = await ReadHeaderAsync(stream, cancellationToken);
        var response = "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nOK"u8.ToArray();
        await stream.WriteAsync(response, cancellationToken);
        return Encoding.Latin1.GetString(requestBytes);
    }

    private static async Task<byte[]> ReadHeaderAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var one = new byte[1];
        while (buffer.Length < 65536)
        {
            var read = await stream.ReadAsync(one, cancellationToken);
            if (read == 0)
            {
                throw new InvalidDataException("Origin connection closed before receiving a complete request header.");
            }

            buffer.WriteByte(one[0]);
            if (buffer.Length >= 4)
            {
                var data = buffer.GetBuffer();
                var length = (int)buffer.Length;
                if (data[length - 4] == '\r' && data[length - 3] == '\n' &&
                    data[length - 2] == '\r' && data[length - 1] == '\n')
                {
                    return buffer.ToArray();
                }
            }
        }

        throw new InvalidDataException("Origin request header exceeded the test bound.");
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

    private sealed class CountingRejectingFactory : IProxyOutboundConnectionFactory
    {
        private int _connectCount;
        public int ConnectCount => Volatile.Read(ref _connectCount);

        public Task<IProxyOutboundConnection> ConnectAsync(string host, int port, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _connectCount);
            throw new InvalidOperationException("Malformed HTTP target host must be rejected before outbound routing.");
        }
    }

    private sealed class RedirectingCaptureFactory(int originPort) : IProxyOutboundConnectionFactory
    {
        public ConcurrentQueue<(string Host, int Port)> Captures { get; } = new();

        public async Task<IProxyOutboundConnection> ConnectAsync(
            string host,
            int port,
            CancellationToken cancellationToken)
        {
            Captures.Enqueue((host, port));
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(IPAddress.Loopback, originPort, cancellationToken);
                return new TestOutboundConnection(socket);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
    }

    private sealed class TestOutboundConnection(Socket socket) : IProxyOutboundConnection
    {
        public Socket Socket { get; } = socket;
        public CancellationToken LifetimeToken => CancellationToken.None;

        public ValueTask DisposeAsync()
        {
            Socket.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private readonly record struct RouteCase(
        string Target,
        string ExpectedHost,
        int ExpectedPort,
        string ExpectedHostHeader);
}
