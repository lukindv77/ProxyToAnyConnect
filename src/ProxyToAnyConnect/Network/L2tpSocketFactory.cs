using System.Net;
using System.Net.Sockets;
using ProxyToAnyConnect.Vpn;

namespace ProxyToAnyConnect.Network;

internal sealed class L2tpSocketFactory
{
    private readonly RasConnectionManager _connectionManager;
    private readonly L2tpDnsResolver _dnsResolver;

    public L2tpSocketFactory(RasConnectionManager connectionManager, L2tpDnsResolver dnsResolver)
    {
        _connectionManager = connectionManager;
        _dnsResolver = dnsResolver;
    }

    public async Task<L2tpConnection> ConnectAsync(
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
            throw new VpnUnavailableException("L2TP connection is not available.");
        }

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            context.LifetimeToken);

        var addresses = await _dnsResolver.ResolveIPv4Async(host, context, linkedCancellation.Token);
        Exception? lastError = null;

        foreach (var address in addresses)
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true
            };

            try
            {
                // This is the central no-DIRECT invariant: every outbound TCP socket
                // is explicitly bound to the IPv4 address assigned by this RAS/L2TP session.
                socket.Bind(new IPEndPoint(context.LocalIPv4, 0));
                await socket.ConnectAsync(new IPEndPoint(address, port), linkedCancellation.Token);

                if (!context.IsAlive)
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

internal sealed class L2tpConnection : IAsyncDisposable
{
    public L2tpConnection(Socket socket, VpnContext context)
    {
        Socket = socket;
        Context = context;
    }

    public Socket Socket { get; }
    public VpnContext Context { get; }

    public ValueTask DisposeAsync()
    {
        Socket.Dispose();
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
