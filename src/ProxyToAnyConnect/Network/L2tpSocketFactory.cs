using System.Net;
using System.Net.Sockets;
using ProxyToAnyConnect.Vpn;

namespace ProxyToAnyConnect.Network;

internal sealed class L2tpSocketFactory : IProxyOutboundConnectionFactory
{
    private readonly RasConnectionManager _connectionManager;
    private readonly L2tpDnsResolver _dnsResolver;

    public L2tpSocketFactory(RasConnectionManager connectionManager, L2tpDnsResolver dnsResolver)
    {
        _connectionManager = connectionManager;
        _dnsResolver = dnsResolver;
    }

    public async Task<IProxyOutboundConnection> ConnectAsync(
        string host,
        int port,
        CancellationToken cancellationToken)
    {
        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        var context = _connectionManager.Current;
        if (context is null || !context.IsAlive)
        {
            try
            {
                context = await _connectionManager.ConnectAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException)
            {
                throw new VpnUnavailableException("Unable to establish the configured L2TP connection.", ex);
            }
        }

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            context.LifetimeToken);

        IReadOnlyList<IPAddress> addresses;
        try
        {
            addresses = await _dnsResolver.ResolveIPv4Async(host, context, linkedCancellation.Token);
        }
        catch (OperationCanceledException ex) when (context.LifetimeToken.IsCancellationRequested)
        {
            throw new VpnUnavailableException("L2TP connection disappeared during DNS resolution.", ex);
        }
        catch (InvalidOperationException ex)
        {
            throw new IOException($"Unable to resolve '{host}' through L2TP DNS.", ex);
        }

        Exception? lastError = null;

        foreach (var address in addresses)
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true
            };

            try
            {
                // Two independent routing constraints are applied before connect():
                // 1) the socket source address is the IPv4 assigned by this RAS session;
                // 2) IP_UNICAST_IF explicitly selects the same Windows interface.
                // There is no code path that creates an unbound/direct Internet socket.
                socket.Bind(new IPEndPoint(context.LocalIPv4, 0));
                WindowsSocketInterfaceBinder.BindToIPv4Interface(socket, context.InterfaceIndex);
                await socket.ConnectAsync(new IPEndPoint(address, port), linkedCancellation.Token);

                if (!context.IsAlive || !context.TryAcquireConnectionReference())
                {
                    socket.Dispose();
                    throw new VpnUnavailableException("L2TP connection disappeared while connecting.");
                }

                return new L2tpConnection(socket, context);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                socket.Dispose();
                lastError = ex;

                if (context.LifetimeToken.IsCancellationRequested)
                {
                    throw new VpnUnavailableException("L2TP connection disappeared while connecting.", ex);
                }
            }
        }

        throw new IOException($"Unable to connect to {host}:{port} through L2TP.", lastError);
    }
}

internal sealed class L2tpConnection : IProxyOutboundConnection
{
    private int _disposed;

    public L2tpConnection(Socket socket, VpnContext context)
    {
        Socket = socket;
        Context = context;
    }

    public Socket Socket { get; }
    public VpnContext Context { get; }
    public CancellationToken LifetimeToken => Context.LifetimeToken;

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        try
        {
            Socket.Dispose();
        }
        finally
        {
            Context.ReleaseConnectionReference();
        }

        return ValueTask.CompletedTask;
    }
}

internal sealed class VpnUnavailableException : IOException
{
    public VpnUnavailableException(string message)
        : base(message)
    {
    }

    public VpnUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
