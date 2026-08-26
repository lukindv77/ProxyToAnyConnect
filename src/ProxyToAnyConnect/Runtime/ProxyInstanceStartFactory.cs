using ProxyToAnyConnect.Configuration;
using ProxyToAnyConnect.Network;
using ProxyToAnyConnect.Proxy;

namespace ProxyToAnyConnect.Runtime;

internal interface IProxyInstanceStartFactory
{
    Task<ProxyStartAttempt> CreateAsync(
        ProxyOptions options,
        ProxyRuntimeMetrics metrics,
        CancellationToken cancellationToken);
}

internal interface IProxyServerLifetime
{
    Task RunAsync(CancellationToken cancellationToken);
    Task WaitUntilListeningAsync(CancellationToken cancellationToken);
}

internal readonly record struct ProxyStartAttempt(
    IAsyncDisposable Lease,
    IProxyServerLifetime Server);

internal sealed class ProductionProxyInstanceStartFactory : IProxyInstanceStartFactory
{
    private readonly VpnLeaseManager _vpn;

    public ProductionProxyInstanceStartFactory(VpnLeaseManager vpn)
    {
        _vpn = vpn;
    }

    public async Task<ProxyStartAttempt> CreateAsync(
        ProxyOptions options,
        ProxyRuntimeMetrics metrics,
        CancellationToken cancellationToken)
    {
        var lease = await _vpn.AcquireAsync(options.Id, cancellationToken);
        try
        {
            var dnsResolver = new L2tpDnsResolver(
                options.DnsTimeoutMilliseconds,
                lease.DnsCache);
            var socketFactory = new L2tpSocketFactory(
                lease.ConnectionManager,
                dnsResolver);
            var proxyServer = new ProxyServer(
                options,
                socketFactory,
                metrics,
                _vpn.Metrics);

            return new ProxyStartAttempt(
                lease,
                new ProxyServerLifetime(proxyServer));
        }
        catch
        {
            await lease.DisposeAsync();
            throw;
        }
    }

    private sealed class ProxyServerLifetime : IProxyServerLifetime
    {
        private readonly ProxyServer _server;

        public ProxyServerLifetime(ProxyServer server)
        {
            _server = server;
        }

        public Task RunAsync(CancellationToken cancellationToken) =>
            _server.RunAsync(cancellationToken);

        public Task WaitUntilListeningAsync(CancellationToken cancellationToken) =>
            _server.WaitUntilListeningAsync(cancellationToken);
    }
}
