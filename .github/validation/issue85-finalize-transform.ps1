Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Read-Normalized([string]$Path) {
    return [IO.File]::ReadAllText($Path).Replace("`r`r`n", "`r`n").Replace("`r`n", "`n")
}

function Write-PreservingNewlines([string]$Path, [string]$Original, [string]$Normalized) {
    $newline = if ($Original.Contains("`r`n")) { "`r`n" } else { "`n" }
    $output = if ($newline -eq "`r`n") { $Normalized.Replace("`n", "`r`n") } else { $Normalized }
    [IO.File]::WriteAllText($Path, $output, [Text.UTF8Encoding]::new($false))
}

function Replace-Exact([string]$Text, [string]$Old, [string]$New, [string]$Label) {
    $oldNormalized = $Old.Replace("`r`n", "`n").TrimEnd("`n")
    $newNormalized = $New.Replace("`r`n", "`n").TrimEnd("`n")
    $first = $Text.IndexOf($oldNormalized, [StringComparison]::Ordinal)
    if ($first -lt 0) { throw "issue85 finalizer could not find: $Label" }
    if ($Text.IndexOf($oldNormalized, $first + $oldNormalized.Length, [StringComparison]::Ordinal) -ge 0) {
        throw "issue85 finalizer found non-unique anchor: $Label"
    }
    return $Text.Substring(0, $first) + $newNormalized + $Text.Substring($first + $oldNormalized.Length)
}

# The primary transform is deliberately newline-preserving, but PowerShell here-strings
# carry CRLF on Windows. Normalize the two range-replaced production files before the
# whitespace gate so validation transport cannot manufacture CRCRLF content.
foreach ($path in @(
    'src/ProxyToAnyConnect/Runtime/ProxyRuntimeCoordinator.cs',
    'src/ProxyToAnyConnect/Runtime/ProxyRuntimeHost.cs')) {
    $raw = [IO.File]::ReadAllText($path)
    $normalized = $raw.Replace("`r`r`n", "`r`n")
    [IO.File]::WriteAllText($path, $normalized, [Text.UTF8Encoding]::new($false))
}

$shutdownPath = 'src/ProxyToAnyConnect/Gui/ApplicationShutdownSequence.cs'
$shutdownOriginal = [IO.File]::ReadAllText($shutdownPath)
$shutdown = Read-Normalized $shutdownPath
$shutdown = Replace-Exact $shutdown @'
        await TryPhaseAsync(
            "runtime-host",
            async () => await disposeRuntimeAsync(),
            failures);
        await TryPhaseAsync(
            "memory-monitor",
            async () => await disposeMemoryMonitorAsync(),
            failures);

        return failures;
'@ @'
        var failureCountBeforeRuntime = failures.Count;
        await TryPhaseAsync(
            "runtime-host",
            async () => await disposeRuntimeAsync(),
            failures);
        var runtimeFailed = failures.Count != failureCountBeforeRuntime;

        // Every independent first-pass owner is still attempted before retrying the
        // runtime. This keeps shutdown latency bounded by one extra exact-host cleanup
        // attempt without letting a transient RAS teardown defect skip memory cleanup.
        await TryPhaseAsync(
            "memory-monitor",
            async () => await disposeMemoryMonitorAsync(),
            failures);

        if (runtimeFailed)
        {
            await TryPhaseAsync(
                "runtime-host-retry",
                async () => await disposeRuntimeAsync(),
                failures);
        }

        return failures;
'@ 'top-level runtime retry after independent owners'
Write-PreservingNewlines $shutdownPath $shutdownOriginal $shutdown

$shutdownTestsPath = 'tests/ProxyToAnyConnect.SelfTests/ApplicationShutdownSequenceSelfTests.cs'
$shutdownTestsOriginal = [IO.File]::ReadAllText($shutdownTestsPath)
$shutdownTests = Read-Normalized $shutdownTestsPath
$shutdownTests = Replace-Exact $shutdownTests @'
        if (!phases.SequenceEqual(new[] { "configuration", "runtime", "memory" }))
        {
            throw new InvalidOperationException(
                "An earlier shutdown fault skipped or reordered an independent cleanup owner.");
        }

        if (failures.Count != 3 ||
            failures[0].Phase != "configuration-command-queue" || failures[0].Exception is not IOException ||
            failures[1].Phase != "runtime-host" || failures[1].Exception is not InvalidOperationException ||
            failures[2].Phase != "memory-monitor" || failures[2].Exception is not ApplicationException)
        {
            throw new InvalidOperationException("Shutdown sequence did not retain every phase failure in owner order.");
        }
'@ @'
        if (!phases.SequenceEqual(new[] { "configuration", "runtime", "memory", "runtime" }))
        {
            throw new InvalidOperationException(
                "An earlier shutdown fault skipped/reordered an independent owner or the bounded runtime retry.");
        }

        if (failures.Count != 4 ||
            failures[0].Phase != "configuration-command-queue" || failures[0].Exception is not IOException ||
            failures[1].Phase != "runtime-host" || failures[1].Exception is not InvalidOperationException ||
            failures[2].Phase != "memory-monitor" || failures[2].Exception is not ApplicationException ||
            failures[3].Phase != "runtime-host-retry" || failures[3].Exception is not InvalidOperationException)
        {
            throw new InvalidOperationException(
                "Shutdown sequence did not retain first-pass and residual retry failures in deterministic owner order.");
        }
'@ 'shutdown retry failure diagnostics'
Write-PreservingNewlines $shutdownTestsPath $shutdownTestsOriginal $shutdownTests
