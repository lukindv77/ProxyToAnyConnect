Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Replace-Exact {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Old,
        [Parameter(Mandatory = $true)][string]$New,
        [int]$ExpectedCount = 1
    )

    $text = [IO.File]::ReadAllText($Path).Replace("`r`n", "`n")
    $oldNormalized = $Old.Replace("`r`n", "`n").TrimEnd("`n")
    $newNormalized = $New.Replace("`r`n", "`n").TrimEnd("`n")
    $count = ([regex]::Matches($text, [regex]::Escape($oldNormalized))).Count
    if ($count -ne $ExpectedCount) {
        throw "Expected $ExpectedCount exact match(es) in $Path, found $count."
    }

    [IO.File]::WriteAllText(
        $Path,
        $text.Replace($oldNormalized, $newNormalized),
        [Text.UTF8Encoding]::new($false))
}

$proxyPath = 'src/ProxyToAnyConnect/Proxy/ProxyServer.cs'
$runnerPath = 'tests/ProxyToAnyConnect.SelfTests/CombinedTestRunner.cs'

Replace-Exact $proxyPath @'
            catch (VpnUnavailableException ex)
            {
                await TryWriteErrorAsync(client, 503, "L2TP VPN unavailable", ex.Message, cancellationToken);
            }
'@ @'
            catch (VpnUnavailableException ex)
            {
                await TryWriteErrorAsync(client, 503, "L2TP VPN unavailable", ex.Message, cancellationToken);
            }
            catch (OutboundConnectTimeoutException ex)
            {
                await TryWriteErrorAsync(client, 504, "Gateway Timeout", ex.Message, cancellationToken);
            }
'@

Replace-Exact $proxyPath @'
        await using var upstream = await _socketFactory.ConnectAsync(host, port, cancellationToken);
'@ @'
        await using var upstream = await ConnectOutboundAsync(host, port, cancellationToken);
'@ 2

Replace-Exact $proxyPath @'
    internal static void EnsureInitialBodyRemainderFits(long contentLength, int remainderLength)
'@ @'
    private Task<IProxyOutboundConnection> ConnectOutboundAsync(
        string host,
        int port,
        CancellationToken cancellationToken) =>
        ConnectOutboundWithTimeoutAsync(
            _socketFactory,
            host,
            port,
            TimeSpan.FromSeconds(_options.OutboundConnectTimeoutSeconds),
            cancellationToken);

    internal static async Task<IProxyOutboundConnection> ConnectOutboundWithTimeoutAsync(
        IProxyOutboundConnectionFactory socketFactory,
        string host,
        int port,
        TimeSpan timeout,
        CancellationToken ownerCancellation)
    {
        ArgumentNullException.ThrowIfNull(socketFactory);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        ownerCancellation.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ownerCancellation);
        deadline.CancelAfter(timeout);

        try
        {
            return await socketFactory.ConnectAsync(host, port, deadline.Token);
        }
        catch (OperationCanceledException ex)
        {
            // Proxy Pause/Shutdown owns the session even if its cancellation races
            // the configured outbound deadline. Preserve that lifecycle control flow.
            ownerCancellation.ThrowIfCancellationRequested();

            if (deadline.IsCancellationRequested)
            {
                throw new OutboundConnectTimeoutException(
                    $"Outbound connection to {host}:{port} timed out after {timeout.TotalSeconds:F0} second(s).",
                    ex);
            }

            throw;
        }
    }

    internal static void EnsureInitialBodyRemainderFits(long contentLength, int remainderLength)
'@

Replace-Exact $proxyPath @'
    internal sealed class ProxyResponseCommittedException : Exception
'@ @'
    internal sealed class OutboundConnectTimeoutException : TimeoutException
    {
        public OutboundConnectTimeoutException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

    internal sealed class ProxyResponseCommittedException : Exception
'@

Replace-Exact $runnerPath @'
        await RunAsync(nameof(ProxyConnectCommitBoundarySelfTests), ProxyConnectCommitBoundarySelfTests.RunAsync);
        await RunAsync(nameof(ProxyConnectAuthoritySelfTests), ProxyConnectAuthoritySelfTests.RunAsync);
'@ @'
        await RunAsync(nameof(ProxyConnectCommitBoundarySelfTests), ProxyConnectCommitBoundarySelfTests.RunAsync);
        await RunAsync(nameof(ProxyOutboundTimeoutSelfTests), ProxyOutboundTimeoutSelfTests.RunAsync);
        await RunAsync(nameof(ProxyConnectAuthoritySelfTests), ProxyConnectAuthoritySelfTests.RunAsync);
'@
