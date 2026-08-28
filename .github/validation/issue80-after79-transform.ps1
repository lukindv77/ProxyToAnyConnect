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
    if ($count -ne $ExpectedCount) { throw "Expected $ExpectedCount exact match(es) in $Path, found $count." }
    [IO.File]::WriteAllText($Path, $text.Replace($oldNormalized, $newNormalized), [Text.UTF8Encoding]::new($false))
}

$proxyPath = 'src/ProxyToAnyConnect/Proxy/ProxyServer.cs'
$runnerPath = 'tests/ProxyToAnyConnect.SelfTests/CombinedTestRunner.cs'

Replace-Exact $proxyPath @'
            catch (OutboundConnectTimeoutException ex)
            {
                await TryWriteErrorAsync(client, 504, "Gateway Timeout", ex.Message, cancellationToken);
            }
            catch (InvalidDataException ex)
'@ @'
            catch (OutboundConnectTimeoutException ex)
            {
                await TryWriteErrorAsync(client, 504, "Gateway Timeout", ex.Message, cancellationToken);
            }
            catch (ClientHeaderTimeoutException ex)
            {
                await TryWriteErrorAsync(client, 408, "Request Timeout", ex.Message, cancellationToken);
            }
            catch (InvalidDataException ex)
'@

Replace-Exact $proxyPath @'
        using var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        headerTimeout.CancelAfter(TimeSpan.FromSeconds(_options.ClientHeaderTimeoutSeconds));
        var readResult = await ReadRequestAsync(clientStream, _options.MaxHeaderBytes, headerTimeout.Token);
        var request = readResult.Request;
'@ @'
        using var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        headerTimeout.CancelAfter(TimeSpan.FromSeconds(_options.ClientHeaderTimeoutSeconds));
        RequestReadResult readResult;
        try
        {
            readResult = await ReadRequestAsync(
                clientStream,
                _options.MaxHeaderBytes,
                headerTimeout.Token);
        }
        catch (OperationCanceledException ex)
        {
            ThrowIfClientHeaderCancellationRequiresAbort(
                ex,
                cancellationToken,
                headerTimeout.Token,
                _options.ClientHeaderTimeoutSeconds);
            throw;
        }

        var request = readResult.Request;
'@

Replace-Exact $proxyPath @'
    private static async Task<RequestReadResult> ReadRequestAsync(
'@ @'
    internal static void ThrowIfClientHeaderCancellationRequiresAbort(
        OperationCanceledException failure,
        CancellationToken ownerCancellation,
        CancellationToken headerDeadline,
        int timeoutSeconds)
    {
        ArgumentNullException.ThrowIfNull(failure);
        if (timeoutSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(timeoutSeconds));
        }

        ownerCancellation.ThrowIfCancellationRequested();
        if (headerDeadline.IsCancellationRequested)
        {
            throw new ClientHeaderTimeoutException(
                $"Client request header timed out after {timeoutSeconds} second(s).",
                failure);
        }
    }

    private static async Task<RequestReadResult> ReadRequestAsync(
'@

Replace-Exact $proxyPath @'
    internal sealed class ProxyResponseCommittedException : Exception
'@ @'
    internal sealed class ClientHeaderTimeoutException : TimeoutException
    {
        public ClientHeaderTimeoutException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

    internal sealed class ProxyResponseCommittedException : Exception
'@

Replace-Exact $runnerPath @'
        await RunAsync(nameof(ProxyShutdownDrainSelfTests), ProxyShutdownDrainSelfTests.RunAsync);
        await RunAsync(nameof(AcceptedClientTransportSelfTests), AcceptedClientTransportSelfTests.RunAsync);
'@ @'
        await RunAsync(nameof(ProxyShutdownDrainSelfTests), ProxyShutdownDrainSelfTests.RunAsync);
        await RunAsync(nameof(ProxyClientHeaderTimeoutSelfTests), ProxyClientHeaderTimeoutSelfTests.RunAsync);
        await RunAsync(nameof(AcceptedClientTransportSelfTests), AcceptedClientTransportSelfTests.RunAsync);
'@
