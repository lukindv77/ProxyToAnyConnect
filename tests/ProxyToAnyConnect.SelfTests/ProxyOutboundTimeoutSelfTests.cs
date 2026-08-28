using System.Net;
using System.Net.Sockets;
using System.Text;
using ProxyToAnyConnect.Configuration;
using ProxyToAnyConnect.Network;
using ProxyToAnyConnect.Proxy;
using ProxyToAnyConnect.Vpn;

namespace ProxyToAnyConnect.SelfTests;

internal static class ProxyOutboundTimeoutSelfTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(6);

    public static async Task<int> RunAsync()
    {
        try
        {
            await ProductionFactoryDeadlineCancelsVpnAcquisitionAsync();
            await OwnerCancellationWinsConfiguredDeadlineAsync();
            VpnLossWinsConcurrentDeadline();
            OwnerCancellationWinsVpnLossAndDeadline();
            await ConfiguredTimeoutReachesConnectAndHttpMappingsAsync();

            Console.WriteLine(
                "PASS: outbound deadline preserves owner/VPN precedence, bounds L2TP acquisition and maps pre-commit timeout to HTTP 504");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: outbound connection timeout regression: {ex}");
            return 1;
        }
    }

    private static async Task ProductionFactoryDeadlineCancelsVpnAcquisitionAsync()
    {
        var controller = new BlockingController();
        var factory = new L2tpSocketFactory(controller, new L2tpDnsResolver(1000));

        try
        {
            _ = await factory.ConnectAsync(
                "timeout.test",
                443,
                TimeSpan.FromMilliseconds(80),
                CancellationToken.None);
            throw new InvalidOperationException(
                "Production L2TP outbound factory escaped its configured deadline.");
        }
        catch (OutboundConnectTimeoutException)
        {
        }

        if (controller.ConnectCount != 1 || !controller.CancellationObserved)
        {
            throw new InvalidOperationException(
                "Configured deadline did not cancel the exact blocked L2TP acquisition.");
        }
    }

    private static async Task OwnerCancellationWinsConfiguredDeadlineAsync()
    {
        var controller = new BlockingController();
        var factory = new L2tpSocketFactory(controller, new L2tpDnsResolver(1000));
        using var owner = new CancellationTokenSource();
        owner.CancelAfter(TimeSpan.FromMilliseconds(60));

        try
        {
            _ = await factory.ConnectAsync(
                "owner-cancel.test",
                443,
                TimeSpan.FromSeconds(5),
                owner.Token);
            throw new InvalidOperationException(
                "Owner cancellation did not stop the blocked L2TP acquisition.");
        }
        catch (OperationCanceledException ex) when (ex.CancellationToken == owner.Token)
        {
        }

        if (controller.ConnectCount != 1 || !controller.CancellationObserved)
        {
            throw new InvalidOperationException(
                "Owner cancellation did not reach the exact blocked L2TP acquisition.");
        }
    }

    private static void VpnLossWinsConcurrentDeadline()
    {
        using var context = CreateContext();
        using var deadline = new CancellationTokenSource();
        deadline.Cancel();
        context.MarkDisconnected();
        var failure = new OperationCanceledException(deadline.Token);

        try
        {
            L2tpSocketFactory.ThrowIfConnectCancellationRequiresAbort(
                failure,
                CancellationToken.None,
                context,
                deadline.Token);
            throw new InvalidOperationException(
                "Concurrent VPN loss/deadline did not fail closed as VPN unavailable.");
        }
        catch (VpnUnavailableException ex) when (ReferenceEquals(ex.InnerException, failure))
        {
        }
    }

    private static void OwnerCancellationWinsVpnLossAndDeadline()
    {
        using var context = CreateContext();
        using var owner = new CancellationTokenSource();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(owner.Token);
        owner.Cancel();
        context.MarkDisconnected();
        var failure = new OperationCanceledException(deadline.Token);

        try
        {
            L2tpSocketFactory.ThrowIfConnectCancellationRequiresAbort(
                failure,
                owner.Token,
                context,
                deadline.Token);
            throw new InvalidOperationException(
                "Owner cancellation did not win the owner/VPN/deadline race.");
        }
        catch (OperationCanceledException ex) when (ex.CancellationToken == owner.Token)
        {
        }
    }

    private static async Task ConfiguredTimeoutReachesConnectAndHttpMappingsAsync()
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        var factory = new DeadlineAwareTimeoutFactory();
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
                OutboundConnectTimeoutSeconds = 17,
                DnsTimeoutMilliseconds = 1000
            },
            factory);
        var proxyTask = proxy.RunAsync(proxyCancellation.Token);

        try
        {
            await AssertRequestMaps504Async(
                proxyPort,
                "CONNECT timeout.test:443 HTTP/1.1\r\nHost: timeout.test:443\r\n\r\n",
                timeout.Token);
            await AssertRequestMaps504Async(
                proxyPort,
                "GET http://timeout.test/ HTTP/1.1\r\nHost: timeout.test\r\n\r\n",
                timeout.Token);

            if (factory.TimedConnectCount != 2 || factory.LegacyConnectCount != 0)
            {
                throw new InvalidOperationException(
                    $"Proxy did not use the timeout-aware outbound overload exactly twice (timed={factory.TimedConnectCount}, legacy={factory.LegacyConnectCount}).");
            }

            if (factory.LastTimeout != TimeSpan.FromSeconds(17))
            {
                throw new InvalidOperationException(
                    $"Configured outbound timeout was not forwarded exactly: {factory.LastTimeout}.");
            }
        }
        finally
        {
            proxyCancellation.Cancel();
            await proxyTask;
        }
    }

    private static async Task AssertRequestMaps504Async(
        int proxyPort,
        string request,
        CancellationToken cancellationToken)
    {
        using var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync(IPAddress.Loopback, proxyPort, cancellationToken);
        await using var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request), cancellationToken);

        var responseHeader = Encoding.ASCII.GetString(await ReadHeaderAsync(stream, cancellationToken));
        if (!responseHeader.StartsWith("HTTP/1.1 504 Gateway Timeout\r\n", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Pre-commit outbound deadline did not map to HTTP 504: {responseHeader}");
        }
    }

    private static VpnContext CreateContext() =>
        new(
            "timeout-test",
            IPAddress.Loopback,
            new VpnInterfaceInfo(
                "loopback",
                "loopback",
                1,
                Array.Empty<IPAddress>()));

    private static async Task<byte[]> ReadHeaderAsync(Stream stream, CancellationToken cancellationToken)
    {
        var bytes = new List<byte>(256);
        var one = new byte[1];
        while (bytes.Count < 8192)
        {
            var read = await stream.ReadAsync(one, cancellationToken);
            if (read == 0)
            {
                throw new IOException("Connection closed before proxy response header completed.");
            }
            bytes.Add(one[0]);
            var count = bytes.Count;
            if (count >= 4 &&
                bytes[count - 4] == (byte)'\r' && bytes[count - 3] == (byte)'\n' &&
                bytes[count - 2] == (byte)'\r' && bytes[count - 1] == (byte)'\n')
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

    private sealed class BlockingController : IVpnConnectionController
    {
        private int _connectCount;
        private int _cancellationObserved;

        public VpnContext? Current => null;
        public VpnConnectionState State => VpnConnectionState.Disconnected;
        public int ConnectCount => Volatile.Read(ref _connectCount);
        public bool CancellationObserved => Volatile.Read(ref _cancellationObserved) != 0;

        public async Task<VpnContext> ConnectAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _connectCount);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("Synthetic blocked VPN acquisition unexpectedly completed.");
            }
            catch (OperationCanceledException)
            {
                Volatile.Write(ref _cancellationObserved, 1);
                throw;
            }
        }

        public Task DisconnectAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class DeadlineAwareTimeoutFactory : IProxyOutboundConnectionFactory
    {
        private int _legacyConnectCount;
        private int _timedConnectCount;
        private long _lastTimeoutTicks;

        public int LegacyConnectCount => Volatile.Read(ref _legacyConnectCount);
        public int TimedConnectCount => Volatile.Read(ref _timedConnectCount);
        public TimeSpan LastTimeout => TimeSpan.FromTicks(Volatile.Read(ref _lastTimeoutTicks));

        public Task<IProxyOutboundConnection> ConnectAsync(
            string host,
            int port,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _legacyConnectCount);
            return Task.FromException<IProxyOutboundConnection>(
                new InvalidOperationException("Proxy used legacy outbound overload instead of the configured deadline overload."));
        }

        public Task<IProxyOutboundConnection> ConnectAsync(
            string host,
            int port,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _timedConnectCount);
            Volatile.Write(ref _lastTimeoutTicks, timeout.Ticks);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromException<IProxyOutboundConnection>(
                new OutboundConnectTimeoutException("Synthetic configured outbound deadline."));
        }
    }
}
