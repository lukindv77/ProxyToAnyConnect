Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$path = 'src/ProxyToAnyConnect/Proxy/ProxyServer.cs'
$text = [IO.File]::ReadAllText($path).Replace("`r`n", "`n")
$old = @'
                var read = await source.ReadAsync(buffer.AsMemory(0, TransferBufferSize), cancellationToken);
                if (read == 0)
                {
                    return;
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                // Mark only after the client write completed successfully. An origin
'@.Replace("`r`n", "`n").TrimEnd("`n")
$new = @'
                var read = await source.ReadAsync(buffer.AsMemory(0, TransferBufferSize), cancellationToken);
                if (read == 0)
                {
                    if (!responseCommit.IsCommitted)
                    {
                        throw new IOException("Origin closed before beginning an HTTP response.");
                    }

                    return;
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                // Mark only after the client write completed successfully. An origin
'@.Replace("`r`n", "`n").TrimEnd("`n")
$count = ([regex]::Matches($text, [regex]::Escape($old))).Count
if ($count -ne 1) {
    throw "Expected exactly one issue77 HTTP response-pump EOF anchor, found $count."
}
[IO.File]::WriteAllText($path, $text.Replace($old, $new), [Text.UTF8Encoding]::new($false))
