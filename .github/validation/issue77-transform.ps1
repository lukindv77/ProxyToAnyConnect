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
        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            upstream.LifetimeToken);

        if (request.ContentLength == 0)
        {
            await PumpAsync(
                upstreamStream,
                clientStream,
                RecordReceived,
                requestCancellation.Token);
            return;
        }

        using var bodyCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            requestCancellation.Token);
        var bodyUpload = ForwardRequestBodyAsync(
            clientStream,
            upstreamStream,
            remainder,
            request.ContentLength,
            bodyCancellation.Token);
        var responseDownload = PumpAsync(
            upstreamStream,
            clientStream,
            RecordReceived,
            requestCancellation.Token);
'@ @'
        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            upstream.LifetimeToken);
        var responseCommit = new HttpResponseCommitState();

        if (request.ContentLength == 0)
        {
            var responseDownload = PumpHttpResponseAsync(
                upstreamStream,
                clientStream,
                RecordReceived,
                responseCommit,
                requestCancellation.Token);
            await AwaitHttpResponseDownloadAsync(
                responseDownload,
                responseCommit,
                cancellationToken);
            return;
        }

        using var bodyCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            requestCancellation.Token);
        var bodyUpload = ForwardRequestBodyAsync(
            clientStream,
            upstreamStream,
            remainder,
            request.ContentLength,
            bodyCancellation.Token);
        var responseDownload = PumpHttpResponseAsync(
            upstreamStream,
            clientStream,
            RecordReceived,
            responseCommit,
            requestCancellation.Token);
'@

Replace-Exact $proxyPath @'
            bodyCancellation.Cancel();
            await IgnoreCancellationAsync(bodyUpload);
            await responseDownload;
            return;
'@ @'
            bodyCancellation.Cancel();
            await IgnoreCancellationAsync(bodyUpload);
            await AwaitHttpResponseDownloadAsync(
                responseDownload,
                responseCommit,
                cancellationToken);
            return;
'@

Replace-Exact $proxyPath @'
        catch
        {
            requestCancellation.Cancel();
            await IgnoreCancellationAsync(responseDownload);
            throw;
        }
'@ @'
        catch (Exception ex)
        {
            requestCancellation.Cancel();
            await IgnoreCancellationAsync(responseDownload);
            RethrowIfHttpResponseCommitted(responseCommit, ex, cancellationToken);
            throw;
        }
'@

Replace-Exact $proxyPath @'
        // The proxy handles exactly one plain-HTTP request per client connection.
        // Once Content-Length bytes have been forwarded, no later client bytes are
        // read or sent upstream; only the origin response remains active.
        await responseDownload;
'@ @'
        // The proxy handles exactly one plain-HTTP request per client connection.
        // Once Content-Length bytes have been forwarded, no later client bytes are
        // read or sent upstream; only the origin response remains active.
        await AwaitHttpResponseDownloadAsync(
            responseDownload,
            responseCommit,
            cancellationToken);
'@

Replace-Exact $proxyPath @'
    private static async Task PumpAsync(
        Stream source,
        Stream destination,
        Action<int> onTransferred,
        CancellationToken cancellationToken)
'@ @'
    private static async Task PumpHttpResponseAsync(
        Stream source,
        Stream destination,
        Action<int> onTransferred,
        HttpResponseCommitState responseCommit,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(TransferBufferSize);
        try
        {
            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, TransferBufferSize), cancellationToken);
                if (read == 0)
                {
                    return;
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                // Mark only after the client write completed successfully. An origin
                // read failure before this point can still be represented by the
                // proxy's normal pre-response 5xx mapping.
                responseCommit.MarkCommitted();
                onTransferred(read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: false);
        }
    }

    private static async Task AwaitHttpResponseDownloadAsync(
        Task responseDownload,
        HttpResponseCommitState responseCommit,
        CancellationToken ownerCancellation)
    {
        try
        {
            await responseDownload;
        }
        catch (OperationCanceledException) when (ownerCancellation.IsCancellationRequested)
        {
            // Explicit proxy Pause/Shutdown remains lifecycle control flow even when
            // origin bytes were already forwarded.
            throw;
        }
        catch (Exception ex)
        {
            if (responseCommit.IsCommitted)
            {
                throw new ProxyResponseCommittedException(
                    "Plain HTTP origin response was already committed; closing the client transport.",
                    ex);
            }

            throw;
        }
    }

    private static void RethrowIfHttpResponseCommitted(
        HttpResponseCommitState responseCommit,
        Exception failure,
        CancellationToken ownerCancellation)
    {
        if (failure is OperationCanceledException && ownerCancellation.IsCancellationRequested)
        {
            return;
        }

        if (responseCommit.IsCommitted)
        {
            throw new ProxyResponseCommittedException(
                "Plain HTTP origin response was already committed before request-side failure cleanup completed.",
                failure);
        }
    }

    private sealed class HttpResponseCommitState
    {
        private int _committed;

        public bool IsCommitted => Volatile.Read(ref _committed) != 0;

        public void MarkCommitted() => Volatile.Write(ref _committed, 1);
    }

    private static async Task PumpAsync(
        Stream source,
        Stream destination,
        Action<int> onTransferred,
        CancellationToken cancellationToken)
'@

Replace-Exact $runnerPath @'
        await RunAsync(nameof(ProxyConnectPortGrammarSelfTests), ProxyConnectPortGrammarSelfTests.RunAsync);
        await RunAsync(nameof(ProxyHttpFramingSelfTests), ProxyHttpFramingSelfTests.RunAsync);
'@ @'
        await RunAsync(nameof(ProxyConnectPortGrammarSelfTests), ProxyConnectPortGrammarSelfTests.RunAsync);
        await RunAsync(nameof(ProxyHttpResponseCommitBoundarySelfTests), ProxyHttpResponseCommitBoundarySelfTests.RunAsync);
        await RunAsync(nameof(ProxyHttpFramingSelfTests), ProxyHttpFramingSelfTests.RunAsync);
'@
