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

    $updated = $text.Replace($oldNormalized, $newNormalized)
    [IO.File]::WriteAllText($Path, $updated, [Text.UTF8Encoding]::new($false))
}

$proxyPath = 'src/ProxyToAnyConnect/Proxy/ProxyServer.cs'
$runnerPath = 'tests/ProxyToAnyConnect.SelfTests/CombinedTestRunner.cs'

Replace-Exact $proxyPath @'
            catch (VpnUnavailableException ex)
'@ @'
            catch (ProxyResponseCommittedException ex)
            {
                // Once a CONNECT success response has begun, this client stream is
                // no longer an HTTP response channel. Never inject a second proxy
                // response into the raw tunnel; terminal transport close is the only
                // fail-closed action after the commitment boundary.
                System.Diagnostics.Debug.WriteLine(
                    $"Committed proxy response failed; closing transport without a second HTTP response: {ex.InnerException ?? ex}");
            }
            catch (VpnUnavailableException ex)
'@

Replace-Exact $proxyPath @'
        await clientStream.WriteAsync(ConnectionEstablished, cancellationToken);
        if (!remainder.IsEmpty)
        {
            await upstreamStream.WriteAsync(remainder, cancellationToken);
            RecordSent(remainder.Length);
        }

        using var tunnelCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            upstream.LifetimeToken);

        var clientToUpstream = PumpAsync(
            clientStream,
            upstreamStream,
            RecordSent,
            tunnelCancellation.Token);
        var upstreamToClient = PumpAsync(
            upstreamStream,
            clientStream,
            RecordReceived,
            tunnelCancellation.Token);

        try
        {
            await Task.WhenAny(clientToUpstream, upstreamToClient);
        }
        finally
        {
            tunnelCancellation.Cancel();
            await IgnoreCancellationAsync(clientToUpstream);
            await IgnoreCancellationAsync(upstreamToClient);
        }
'@ @'
        try
        {
            // Treat the success response as committed before the write begins. A
            // transport failure can occur after a partial write, so attempting a
            // fallback 5xx is never safe once these bytes are handed to the stream.
            await clientStream.WriteAsync(ConnectionEstablished, cancellationToken);
            if (!remainder.IsEmpty)
            {
                await upstreamStream.WriteAsync(remainder, cancellationToken);
                RecordSent(remainder.Length);
            }

            using var tunnelCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                upstream.LifetimeToken);

            var clientToUpstream = PumpAsync(
                clientStream,
                upstreamStream,
                RecordSent,
                tunnelCancellation.Token);
            var upstreamToClient = PumpAsync(
                upstreamStream,
                clientStream,
                RecordReceived,
                tunnelCancellation.Token);

            try
            {
                await Task.WhenAny(clientToUpstream, upstreamToClient);
            }
            finally
            {
                tunnelCancellation.Cancel();
                await IgnoreCancellationAsync(clientToUpstream);
                await IgnoreCancellationAsync(upstreamToClient);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Preserve explicit proxy Pause/Shutdown cancellation semantics.
            throw;
        }
        catch (Exception ex)
        {
            throw new ProxyResponseCommittedException(
                "CONNECT response was already committed; closing the tunnel transport.",
                ex);
        }
'@

Replace-Exact $proxyPath @'
    private readonly record struct RequestReadResult(ParsedProxyRequest Request, byte[] Remainder);
'@ @'
    private sealed class ProxyResponseCommittedException : Exception
    {
        public ProxyResponseCommittedException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

    private readonly record struct RequestReadResult(ParsedProxyRequest Request, byte[] Remainder);
'@

Replace-Exact $runnerPath @'
        Run(nameof(ProxyConnectSetupSelfTests), ProxyConnectSetupSelfTests.Run);
        await RunAsync(nameof(ProxyConnectAuthoritySelfTests), ProxyConnectAuthoritySelfTests.RunAsync);
'@ @'
        Run(nameof(ProxyConnectSetupSelfTests), ProxyConnectSetupSelfTests.Run);
        await RunAsync(nameof(ProxyConnectCommitBoundarySelfTests), ProxyConnectCommitBoundarySelfTests.RunAsync);
        await RunAsync(nameof(ProxyConnectAuthoritySelfTests), ProxyConnectAuthoritySelfTests.RunAsync);
'@
