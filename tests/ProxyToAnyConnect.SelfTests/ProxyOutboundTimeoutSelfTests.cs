using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ProxyToAnyConnect.Configuration;
using ProxyToAnyConnect.Network;
using ProxyToAnyConnect.Proxy;

namespace ProxyToAnyConnect.SelfTests;

internal static class ProxyOutboundTimeoutSelfTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(8);

    public static async Task<int> RunAsync()
    {
        try
        {
            await ConfiguredDeadlineCancelsBlockedFactoryAsync();
            await OwnerCancellationWinsDeadlineRaceAsync();
            await VpnUnavailableFailureIsNotRewrittenAsync();
            await PreCommitDeadlineMapsTo504Async();

            Console.WriteLine(
                "PASS: outbound connection deadline is bounded, owner-cancellation preserving and maps pre-commit timeout to HTTP 504");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: outbound connection timeout regression: {ex}");
            return 1;
        }
    }

    private static async Task ConfiguredDeadlineCancelsBlockedFactoryAsync()
    {
        var factory = new BlockingOutboundFactory();
        var stopwatch = Stopwatch.StartNew();
        try
        {
            _ = await ProxyServer.ConnectOutboundWithTimeoutAsync(
                factory,
                "timeout.test",
                443,
                TimeSpan.FromMilliseconds(80),
                CancellationToken.None);
            throw new InvalidOperationException("Blocked outbound factory escaped its configured deadline.");
        }
        catch (ProxyServer.OutboundConnectTimeoutException)
        {
        }

        stopwatch.Stop();
        if (!factory.CancellationObserved || factory.ConnectCount != 1)
        {
            throw new InvalidOperationException(
                "Configured outbound deadline did not cancel exactly one factory acquisition.");
        }

        if (stopwatch.Elapsed < TimeSpan.FromMilliseconds(20) ||
            stopwatch.Elapsed > TimeSpan.FromSeconds(2))
        {
            throw new InvalidOperationException(
                $"Configured outbound deadline completed outside the bounded self-test window: {stopwatch.Elapsed}.");
        }
    }

    private static async Task OwnerCancellationWinsDeadlineRaceAsync()
    {
        var factory = new BlockingOutboundFactory();
        using var ownerCancellation = new CancellationTokenSource();
        ownerCancellation.CancelAfter(TimeSpan.FromMilliseconds(60));

        try
        {
            _ = await ProxyServer.ConnectOutboundWithTimeoutAsync(
                factory,
                "owner-cancel.test",
                443,
                TimeSpan.FromSeconds(5),
                ownerCancellation.Token);
            throw new InvalidOperationException("Owner cancellation did not stop blocked outbound acquisition.");
        }
        catch (OperationCanceledException) when (ownerCancellation.IsCancellationRequested)
        {
        }

        if (!factory.CancellationObserved || factory.ConnectCount != 1)
        {
            throw new InvalidOperationException(
                "Owner cancellation did not reach the exact blocked outbound acquisition.");
        }
    }

    private static async Task VpnUnavailableFailureIsNotRewrittenAsync()
    {
        var expected = new VpnUnavailableException("Synthetic VPN disappearance before outbound connection.");
        var factory = new FailingOutboundFactory(expected);

        try
        {
            _ = await ProxyServer.ConnectOutboundWithTimeoutAsync(
                factory,
                "vpn-unavailable.test",
                443,
                TimeSpan.FromMilliseconds(100),
                CancellationToken.None);
            throw new InvalidOperationException("Synthetic VPN failure unexpectedly returned a connection.");
        }
        catch (VpnUnavailableException ex) when (ReferenceEquals(ex, expected))
        {
        }
    }

    private static async Task PreCommitDeadlineMapsTo504Async()
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        var factory = new BlockingOutboundFactory();
        var proxyPort = ReserveLoopbackPort();
        using var proxyCancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var proxy = new ProxyServer(
            new ProxyOptions
            {
                Id = "outbound-timeout-selftest",
                Name = "Outbound timeout self-test",
                ListenAddress = "127.0.0.1",
                ListenPort = proxyPort,
                MaxConcurrentConnections = 4,
                MaxHeaderBytes = 8192,
                ClientHeaderTimeoutSeconds = 5,
                OutboundConnectTimeoutSeconds = 1,
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
                "CONNECT timeout.test:443 HTTP/1.1\r\nHost: timeout.test:443\r\n\r\n"u8.ToArray(),
                timeout.Token);

            var responseHeader = Encoding.ASCII.GetString(await ReadHeaderAsync(stream, timeout.Token));
            if (!responseHeader.StartsWith("HTTP/1.1 504 Gateway Timeout\r\n", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Pre-commit outbound deadline did not map to HTTP 504: {responseHeader}");
            }

            if (factory.ConnectCount != 1 || !factory.CancellationObserved)
            {
                throw new InvalidOperationException(
                    "HTTP 504 path did not cancel exactly one blocked outbound acquisition.");
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

    private sealed class BlockingOutboundFactory : IProxyOutboundConnectionFactory
    {
        private int _connectCount;
        private int _cancellationObserved;

        public int ConnectCount => Volatile.Read(ref _connectCount);
        public bool CancellationObserved => Volatile.Read(ref _cancellationObserved) != 0;

        public async Task<IProxyOutboundConnection> ConnectAsync(
            string host,
            int port,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _connectCount);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("Infinite synthetic outbound wait unexpectedly completed.");
            }
            catch (OperationCanceledException)
            {
                Volatile.Write(ref _cancellationObserved, 1);
                throw;
            }
        }
    }

    private sealed class FailingOutboundFactory : IProxyOutboundConnectionFactory
    {
        private readonly Exception _failure;

        public FailingOutboundFactory(Exception failure)
        {
            _failure = failure;
        }

        public Task<IProxyOutboundConnection> ConnectAsync(
            string host,
            int port,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromException<IProxyOutboundConnection>(_failure);
        }
    }
}
