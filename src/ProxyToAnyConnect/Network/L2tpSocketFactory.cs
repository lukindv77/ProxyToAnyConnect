using System.Net;
using System.Net.Sockets;
using ProxyToAnyConnect.Vpn;

namespace ProxyToAnyConnect.Network;

internal sealed class L2tpSocketFactory : IProxyOutboundConnectionFactory
{
    private readonly IVpnConnectionController _connectionManager;
    private readonly L2tpDnsResolver _dnsResolver;

    public L2tpSocketFactory(IVpnConnectionController connectionManager, L2tpDnsResolver dnsResolver)
    {
        _connectionManager = connectionManager;
        _dnsResolver = dnsResolver;
    }

    public Task<IProxyOutboundConnection> ConnectAsync(
        string host,
        int port,
        CancellationToken cancellationToken) =>
        ConnectCoreAsync(host, port, outboundTimeout: null, cancellationToken);

    public Task<IProxyOutboundConnection> ConnectAsync(
        string host,
        int port,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        return ConnectCoreAsync(host, port, timeout, cancellationToken);
    }

    private async Task<IProxyOutboundConnection> ConnectCoreAsync(
        string host,
        int port,
        TimeSpan? outboundTimeout,
        CancellationToken ownerCancellation)
    {
        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        ownerCancellation.ThrowIfCancellationRequested();

        using var deadline = outboundTimeout is null
            ? null
            : CancellationTokenSource.CreateLinkedTokenSource(ownerCancellation);
        if (deadline is not null)
        {
            deadline.CancelAfter(outboundTimeout!.Value);
        }
        var deadlineToken = deadline?.Token ?? default;
        var operationCancellation = deadline?.Token ?? ownerCancellation;

        var context = _connectionManager.Current;
        if (context is null || !context.IsAlive)
        {
            try
            {
                context = await _connectionManager.ConnectAsync(operationCancellation);
            }
            catch (OperationCanceledException ex)
            {
                ownerCancellation.ThrowIfCancellationRequested();
                if (deadlineToken.IsCancellationRequested)
                {
                    throw new OutboundConnectTimeoutException(
                        "Configured outbound connection deadline expired while acquiring L2TP.", ex);
                }
                throw;
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException)
            {
                throw new VpnUnavailableException("Unable to establish the configured L2TP connection.", ex);
            }
        }

        ThrowIfOutboundSetupCancellationRequiresAbort(
            ownerCancellation,
            context,
            deadlineToken);

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            operationCancellation,
            context.LifetimeToken);

        IReadOnlyList<IPAddress> addresses;
        try
        {
            addresses = await _dnsResolver.ResolveIPv4Async(host, context, linkedCancellation.Token);
        }
        catch (OperationCanceledException ex)
        {
            ThrowIfConnectCancellationRequiresAbort(
                ex,
                ownerCancellation,
                context,
                deadlineToken);
            throw;
        }
        catch (InvalidOperationException ex)
        {
            throw new IOException($"Unable to resolve '{host}' through L2TP DNS.", ex);
        }

        Exception? lastError = null;
        foreach (var address in addresses)
        {
            ThrowIfOutboundSetupCancellationRequiresAbort(
                ownerCancellation,
                context,
                deadlineToken);

            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true
            };

            try
            {
                socket.Bind(new IPEndPoint(context.LocalIPv4, 0));
                WindowsSocketInterfaceBinder.BindToIPv4Interface(socket, context.InterfaceIndex);
                await socket.ConnectAsync(new IPEndPoint(address, port), linkedCancellation.Token);

                ThrowIfOutboundSetupCancellationRequiresAbort(
                    ownerCancellation,
                    context,
                    deadlineToken);

                if (!context.TryAcquireConnectionReference())
                {
                    socket.Dispose();
                    throw new VpnUnavailableException("L2TP connection disappeared while connecting.");
                }

                return new L2tpConnection(socket, context);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                socket.Dispose();
                ThrowIfConnectCancellationRequiresAbort(
                    ex,
                    ownerCancellation,
                    context,
                    deadlineToken);
                lastError = ex;
            }
        }

        throw new IOException($"Unable to connect to {host}:{port} through L2TP.", lastError);
    }

    private static void ThrowIfOutboundSetupCancellationRequiresAbort(
        CancellationToken ownerCancellation,
        VpnContext context,
        CancellationToken deadlineCancellation)
    {
        ownerCancellation.ThrowIfCancellationRequested();
        if (!context.IsAlive || context.LifetimeToken.IsCancellationRequested)
        {
            throw new VpnUnavailableException("L2TP connection disappeared while connecting.");
        }
        if (deadlineCancellation.IsCancellationRequested)
        {
            throw new OutboundConnectTimeoutException("Configured outbound connection deadline expired.");
        }
    }

    internal static void ThrowIfConnectCancellationRequiresAbort(
        Exception connectFailure,
        CancellationToken callerCancellation,
        VpnContext context,
        CancellationToken deadlineCancellation = default)
    {
        ArgumentNullException.ThrowIfNull(connectFailure);
        ArgumentNullException.ThrowIfNull(context);

        callerCancellation.ThrowIfCancellationRequested();
        if (context.LifetimeToken.IsCancellationRequested)
        {
            throw new VpnUnavailableException(
                "L2TP connection disappeared while connecting.",
                connectFailure);
        }
        if (deadlineCancellation.IsCancellationRequested)
        {
            throw new OutboundConnectTimeoutException(
                "Configured outbound connection deadline expired.",
                connectFailure);
        }
    }
}

internal sealed class OutboundConnectTimeoutException : TimeoutException
{
    public OutboundConnectTimeoutException(string message)
        : base(message)
    {
    }

    public OutboundConnectTimeoutException(string message, Exception innerException)
        : base(message, innerException)
    {
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
