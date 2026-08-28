using System.Net;
using System.Net.Sockets;
using System.Text;
using ProxyToAnyConnect.Configuration;
using ProxyToAnyConnect.Network;
using ProxyToAnyConnect.Proxy;

namespace ProxyToAnyConnect.SelfTests;

internal static class ProxyClientHeaderTimeoutSelfTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(6);

    public static async Task<int> RunAsync()
    {
        try
        {
            DeadlineCancellationClassifiesAsRequestTimeout();
            OwnerCancellationWinsDeadlineRace();
            await PartialHeaderDeadlineReturns408BeforeOutboundAsync();

            Console.WriteLine(
                "PASS: client header deadline maps to HTTP 408 before outbound ownership and preserves proxy owner cancellation");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: client header timeout regression: {ex}");
            return 1;
        }
    }

    private static void DeadlineCancellationClassifiesAsRequestTimeout()
    {
        using var deadline = new CancellationTokenSource();
        deadline.Cancel();
        var failure = new OperationCanceledException(deadline.Token);

        try
        {
            ProxyServer.ThrowIfClientHeaderCancellationRequiresAbort(
                failure,
                CancellationToken.None,
                deadline.Token,
                timeoutSeconds: 3);
            throw new InvalidOperationException(
                "Expired client-header deadline was not classified as Request Timeout.");
        }
        catch (ProxyServer.ClientHeaderTimeoutException ex)
            when (ReferenceEquals(ex.InnerException, failure))
        {
        }
    }

    private static void OwnerCancellationWinsDeadlineRace()
    {
        using var owner = new CancellationTokenSource();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(owner.Token);
        owner.Cancel();
        var failure = new OperationCanceledException(deadline.Token);

        try
        {
            ProxyServer.ThrowIfClientHeaderCancellationRequiresAbort(
                failure,
                owner.Token,
                deadline.Token,
                timeoutSeconds: 3);
            throw new InvalidOperationException(
                "Proxy owner cancellation did not win a simultaneous header-deadline race.");
        }
        catch (OperationCanceledException ex) when (ex.CancellationToken == owner.Token)
        {
        }
    }

    private static async Task PartialHeaderDeadlineReturns408BeforeOutboundAsync()
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        var factory = new CountingRejectingFactory();
        var proxyPort = ReserveLoopbackPort();
        using var proxyCancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var proxy = new ProxyServer(
            new ProxyOptions
            {
                Id = "header-timeout-selftest",
                Name = "Header timeout self-test",
                ListenAddress = "127.0.0.1",
                ListenPort = proxyPort,
                MaxConcurrentConnections = 4,
                MaxHeaderBytes = 8192,
                ClientHeaderTimeoutSeconds = 1,
                OutboundConnectTimeoutSeconds = 5,
                DnsTimeoutMilliseconds = 1000
            },
            factory);
        var proxyTask = proxy.RunAsync(proxyCancellation.Token);

        try
        {
            using var client = new TcpClient { NoDelay = true };
            await client.ConnectAsync(IPAddress.Loopback, proxyPort, timeout.Token);
            await using var stream = client.GetStream();
            await stream.WriteAsync(
                "GET http://timeout.test/ HTTP/1.1\r\nHost: timeout.test\r\n"u8.ToArray(),
                timeout.Token);

            var responseHeader = Encoding.ASCII.GetString(await ReadHeaderAsync(stream, timeout.Token));
            if (!responseHeader.StartsWith("HTTP/1.1 408 Request Timeout\r\n", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Incomplete request header deadline did not map to HTTP 408: {responseHeader}");
            }

            if (factory.ConnectCount != 0)
            {
                throw new InvalidOperationException(
                    $"Client header timeout opened {factory.ConnectCount} outbound connection(s).");
            }
        }
        finally
        {
            proxyCancellation.Cancel();
            await proxyTask;
        }
    }

    private static async Task<byte[]> ReadHeaderAsync(Stream stream, CancellationToken cancellationToken)
    {
        var bytes = new List<byte>(256);
        var one = new byte[1];
        while (bytes.Count < 8192)
        {
            var read = await stream.ReadAsync(one, cancellationToken);
            if (read == 0)
            {
                throw new IOException("Connection closed before the proxy response header completed.");
            }

            bytes.Add(one[0]);
            var count = bytes.Count;
            if (count >= 4 &&
                bytes[count - 4] == (byte)'\r' &&
                bytes[count - 3] == (byte)'\n' &&
                bytes[count - 2] == (byte)'\r' &&
                bytes[count - 1] == (byte)'\n')
            {
                return bytes.ToArray();
            }
        }

        throw new InvalidDataException("Proxy response header exceeded the self-test limit.");
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

        public Task<IProxyOutboundConnection> ConnectAsync(
            string host,
            int port,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _connectCount);
            return Task.FromException<IProxyOutboundConnection>(
                new InvalidOperationException("Header-timeout test must not reach outbound ownership."));
        }
    }
}
