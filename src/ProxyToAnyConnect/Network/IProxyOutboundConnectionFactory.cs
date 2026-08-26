using System.Net.Sockets;

namespace ProxyToAnyConnect.Network;

internal interface IProxyOutboundConnectionFactory
{
    Task<IProxyOutboundConnection> ConnectAsync(
        string host,
        int port,
        CancellationToken cancellationToken);
}

internal interface IProxyOutboundConnection : IAsyncDisposable
{
    Socket Socket { get; }
    CancellationToken LifetimeToken { get; }
}
