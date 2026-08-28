Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Replace-Exact {
    param([string]$Path,[string]$Old,[string]$New,[int]$ExpectedCount = 1)
    $text = [IO.File]::ReadAllText($Path).Replace("`r`n", "`n")
    $oldNormalized = $Old.Replace("`r`n", "`n").TrimEnd("`n")
    $newNormalized = $New.Replace("`r`n", "`n").TrimEnd("`n")
    $count = ([regex]::Matches($text, [regex]::Escape($oldNormalized))).Count
    if ($count -ne $ExpectedCount) { throw "Expected $ExpectedCount exact match(es) in $Path, found $count." }
    [IO.File]::WriteAllText($Path, $text.Replace($oldNormalized, $newNormalized), [Text.UTF8Encoding]::new($false))
}

$interfacePath = 'src/ProxyToAnyConnect/Network/IProxyOutboundConnectionFactory.cs'
$factoryPath = 'src/ProxyToAnyConnect/Network/L2tpSocketFactory.cs'
$proxyPath = 'src/ProxyToAnyConnect/Proxy/ProxyServer.cs'
$runnerPath = 'tests/ProxyToAnyConnect.SelfTests/CombinedTestRunner.cs'

Replace-Exact $interfacePath @'
    Task<IProxyOutboundConnection> ConnectAsync(
        string host,
        int port,
        CancellationToken cancellationToken);
'@ @'
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
'@

Replace-Exact $factoryPath @'
    public async Task<IProxyOutboundConnection> ConnectAsync(
        string host,
        int port,
        CancellationToken cancellationToken)
    {
        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        // Pause/shutdown cancellation is an ownership boundary, not an ordinary
        // outbound failure. Observe it before touching VPN or socket state so a
        // cancelled proxy session never starts new network work.
        cancellationToken.ThrowIfCancellationRequested();

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
        catch (OperationCanceledException ex)
        {
            // If both the proxy/session owner and VPN context were cancelled at
            // nearly the same time, caller ownership wins so Pause/Shutdown stays
            // cancellation rather than being rewritten to an HTTP 502 path.
            cancellationToken.ThrowIfCancellationRequested();

            if (context.LifetimeToken.IsCancellationRequested)
            {
                throw new VpnUnavailableException("L2TP connection disappeared during DNS resolution.", ex);
            }

            throw;
        }
        catch (InvalidOperationException ex)
        {
            throw new IOException($"Unable to resolve '{host}' through L2TP DNS.", ex);
        }

        Exception? lastError = null;

        foreach (var address in addresses)
        {
            cancellationToken.ThrowIfCancellationRequested();

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
                ThrowIfConnectCancellationRequiresAbort(ex, cancellationToken, context);
                lastError = ex;
            }
        }

        throw new IOException($"Unable to connect to {host}:{port} through L2TP.", lastError);
    }

    internal static void ThrowIfConnectCancellationRequiresAbort(
        Exception connectFailure,
        CancellationToken callerCancellation,
        VpnContext context)
    {
        ArgumentNullException.ThrowIfNull(connectFailure);
        ArgumentNullException.ThrowIfNull(context);

        // Caller cancellation is checked first intentionally. Proxy Pause/Shutdown
        // owns the session and must retain cancellation semantics even when the VPN
        // lifetime is invalidated in the same race window.
        callerCancellation.ThrowIfCancellationRequested();

        if (context.LifetimeToken.IsCancellationRequested)
        {
            throw new VpnUnavailableException(
                "L2TP connection disappeared while connecting.",
                connectFailure);
        }
    }
'@ @'
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
'@

Replace-Exact $factoryPath @'
internal sealed class L2tpConnection : IProxyOutboundConnection
'@ @'
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
'@

Replace-Exact $proxyPath @'
            catch (VpnUnavailableException ex)
            {
                await TryWriteErrorAsync(client, 503, "L2TP VPN unavailable", ex.Message, cancellationToken);
            }
            catch (InvalidDataException ex)
'@ @'
            catch (VpnUnavailableException ex)
            {
                await TryWriteErrorAsync(client, 503, "L2TP VPN unavailable", ex.Message, cancellationToken);
            }
            catch (OutboundConnectTimeoutException ex)
            {
                await TryWriteErrorAsync(client, 504, "Gateway Timeout", ex.Message, cancellationToken);
            }
            catch (InvalidDataException ex)
'@

Replace-Exact $proxyPath @'
        await using var upstream = await _socketFactory.ConnectAsync(host, port, cancellationToken);
'@ @'
        await using var upstream = await _socketFactory.ConnectAsync(
            host,
            port,
            TimeSpan.FromSeconds(_options.OutboundConnectTimeoutSeconds),
            cancellationToken);
'@ 2

Replace-Exact $runnerPath @'
        await RunAsync(nameof(ProxyConnectCommitBoundarySelfTests), ProxyConnectCommitBoundarySelfTests.RunAsync);
        await RunAsync(nameof(ProxyConnectAuthoritySelfTests), ProxyConnectAuthoritySelfTests.RunAsync);
'@ @'
        await RunAsync(nameof(ProxyConnectCommitBoundarySelfTests), ProxyConnectCommitBoundarySelfTests.RunAsync);
        await RunAsync(nameof(ProxyOutboundTimeoutSelfTests), ProxyOutboundTimeoutSelfTests.RunAsync);
        await RunAsync(nameof(ProxyConnectAuthoritySelfTests), ProxyConnectAuthoritySelfTests.RunAsync);
'@
