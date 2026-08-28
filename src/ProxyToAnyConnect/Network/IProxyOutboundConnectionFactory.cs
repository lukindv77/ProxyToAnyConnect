using System.Net.Sockets;

namespace ProxyToAnyConnect.Network;

internal interface IProxyOutboundConnectionFactory
{
    Task<IProxyOutboundConnection> ConnectAsync(
        string host,
        int port,
        CancellationToken cancellationToken);

    // Existing test/alternate factories remain source-compatible. Production
    // L2tpSocketFactory overrides this overload so it can preserve owner, VPN
    // lifetime and configured-deadline cancellation as three distinct signals.
    Task<IProxyOutboundConnection> ConnectAsync(
        string host,
        int port,
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        ConnectAsync(host, port, cancellationToken);
}

internal interface IProxyOutboundConnection : IAsyncDisposable
{
    Socket Socket { get; }
    CancellationToken LifetimeToken { get; }
}
