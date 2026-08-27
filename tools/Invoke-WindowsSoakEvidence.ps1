[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateRange(1, [int]::MaxValue)]
    [int]$ProcessId,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$ExpectedExecutableSha256,

    [ValidateNotNullOrEmpty()]
    [string]$ExpectedProcessName = 'ProxyToAnyConnect',

    [ValidateRange(1, 604800)]
    [int]$DurationSeconds = 43200,

    [ValidateRange(1, 3600)]
    [int]$SampleIntervalSeconds = 300,

    [ValidateNotNullOrEmpty()]
    [string]$OutputDirectory = (Join-Path $PWD 'artifacts/windows-soak-evidence')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-JsonFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [object]$Value
    )

    ConvertTo-Json -InputObject $Value -Depth 8 | Set-Content -LiteralPath $Path -Encoding utf8
}

function Get-RequiredProcess {
    param(
        [Parameter(Mandatory = $true)]
        [int]$Id,
        [Parameter(Mandatory = $true)]
        [string]$Name,
        [Parameter(Mandatory = $true)]
        [string]$StartTimeUtc
    )

    $process = Get-Process -Id $Id -ErrorAction Stop
    $process.Refresh()

    if (-not $process.ProcessName.Equals($Name, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Process $Id changed identity from '$Name' to '$($process.ProcessName)'."
    }

    $actualStartTimeUtc = ([DateTimeOffset]$process.StartTime.ToUniversalTime()).ToString('O')
    if (-not $actualStartTimeUtc.Equals($StartTimeUtc, [StringComparison]::Ordinal)) {
        throw "Process $Id start time changed during soak evidence collection; PID reuse is not accepted."
    }

    return $process
}

$expectedHash = $ExpectedExecutableSha256.Trim().ToLowerInvariant()
if ($expectedHash -notmatch '^[0-9a-f]{64}$') {
    throw 'ExpectedExecutableSha256 must be exactly 64 hexadecimal characters.'
}

$outputPath = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $outputPath) {
    $existing = @(Get-ChildItem -LiteralPath $outputPath -Force -ErrorAction Stop)
    if ($existing.Count -ne 0) {
        throw "Soak evidence output directory is not empty: $outputPath"
    }
}
else {
    New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
}

$initialProcess = Get-Process -Id $ProcessId -ErrorAction Stop
$initialProcess.Refresh()
if (-not $initialProcess.ProcessName.Equals($ExpectedProcessName, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Expected process '$ExpectedProcessName', found '$($initialProcess.ProcessName)' for PID $ProcessId."
}

$processStartTimeUtc = ([DateTimeOffset]$initialProcess.StartTime.ToUniversalTime()).ToString('O')
$executablePath = $initialProcess.Path
if ([string]::IsNullOrWhiteSpace($executablePath)) {
    throw "Unable to resolve executable path for process $ProcessId."
}

$actualHash = (Get-FileHash -LiteralPath $executablePath -Algorithm SHA256).Hash.ToLowerInvariant()
if (-not $actualHash.Equals($expectedHash, [StringComparison]::Ordinal)) {
    throw "Process executable SHA-256 '$actualHash' does not match expected '$expectedHash'."
}

$metadataPath = Join-Path $outputPath 'metadata.json'
$samplesPath = Join-Path $outputPath 'process-samples.jsonl'
$summaryPath = Join-Path $outputPath 'summary.json'
$resultPath = Join-Path $outputPath 'result.json'
$manifestPath = Join-Path $outputPath 'manifest.json'
$startedAtUtc = [DateTimeOffset]::UtcNow

$metadata = [ordered]@{
    schemaVersion = 1
    startedAtUtc = $startedAtUtc.ToString('O')
    processId = $ProcessId
    processName = $initialProcess.ProcessName
    processStartTimeUtc = $processStartTimeUtc
    executableSha256 = $actualHash
    expectedExecutableSha256 = $expectedHash
    durationSeconds = $DurationSeconds
    sampleIntervalSeconds = $SampleIntervalSeconds
    managedHeapEvidence = 'Use process.memory.startup/process.memory.periodic records from the application JSONL log for the same time window.'
}
Write-JsonFile -Path $metadataPath -Value $metadata

$sampleCount = 0
$firstTimestampUtc = $null
$lastTimestampUtc = $null
$firstWorkingSetBytes = 0L
$lastWorkingSetBytes = 0L
$firstPrivateBytes = 0L
$lastPrivateBytes = 0L
$minWorkingSetBytes = [long]::MaxValue
$maxWorkingSetBytes = 0L
$minPrivateBytes = [long]::MaxValue
$maxPrivateBytes = 0L
$minHandleCount = [int]::MaxValue
$maxHandleCount = 0
$minThreadCount = [int]::MaxValue
$maxThreadCount = 0
$failure = $null

try {
    $deadlineUtc = $startedAtUtc.AddSeconds($DurationSeconds)
    while ($true) {
        $process = Get-RequiredProcess `
            -Id $ProcessId `
            -Name $initialProcess.ProcessName `
            -StartTimeUtc $processStartTimeUtc

        $timestampUtc = [DateTimeOffset]::UtcNow
        $workingSetBytes = [long]$process.WorkingSet64
        $privateBytes = [long]$process.PrivateMemorySize64
        $handleCount = [int]$process.HandleCount
        $threadCount = [int]$process.Threads.Count

        $sample = [ordered]@{
            schemaVersion = 1
            index = $sampleCount
            timestampUtc = $timestampUtc.ToString('O')
            processId = $ProcessId
            processName = $process.ProcessName
            processStartTimeUtc = $processStartTimeUtc
            workingSetBytes = $workingSetBytes
            privateBytes = $privateBytes
            handleCount = $handleCount
            threadCount = $threadCount
        }
        $line = ConvertTo-Json -InputObject $sample -Depth 4 -Compress
        Add-Content -LiteralPath $samplesPath -Value $line -Encoding utf8

        if ($sampleCount -eq 0) {
            $firstTimestampUtc = $timestampUtc
            $firstWorkingSetBytes = $workingSetBytes
            $firstPrivateBytes = $privateBytes
        }

        $lastTimestampUtc = $timestampUtc
        $lastWorkingSetBytes = $workingSetBytes
        $lastPrivateBytes = $privateBytes
        $minWorkingSetBytes = [Math]::Min($minWorkingSetBytes, $workingSetBytes)
        $maxWorkingSetBytes = [Math]::Max($maxWorkingSetBytes, $workingSetBytes)
        $minPrivateBytes = [Math]::Min($minPrivateBytes, $privateBytes)
        $maxPrivateBytes = [Math]::Max($maxPrivateBytes, $privateBytes)
        $minHandleCount = [Math]::Min($minHandleCount, $handleCount)
        $maxHandleCount = [Math]::Max($maxHandleCount, $handleCount)
        $minThreadCount = [Math]::Min($minThreadCount, $threadCount)
        $maxThreadCount = [Math]::Max($maxThreadCount, $threadCount)
        $sampleCount++

        if ($timestampUtc -ge $deadlineUtc) {
            break
        }

        $remainingMilliseconds = [Math]::Max(0.0, ($deadlineUtc - [DateTimeOffset]::UtcNow).TotalMilliseconds)
        if ($remainingMilliseconds -le 0) {
            continue
        }

        $sleepMilliseconds = [Math]::Min(
            [double]($SampleIntervalSeconds * 1000),
            $remainingMilliseconds)
        Start-Sleep -Milliseconds ([int][Math]::Max(1, [Math]::Ceiling($sleepMilliseconds)))
    }
}
catch {
    $failure = $_.Exception
}
finally {
    $completedAtUtc = [DateTimeOffset]::UtcNow
    $observedDurationSeconds = if ($null -ne $firstTimestampUtc -and $null -ne $lastTimestampUtc) {
        [Math]::Max(0.0, ($lastTimestampUtc - $firstTimestampUtc).TotalSeconds)
    }
    else {
        0.0
    }

    $summary = [ordered]@{
        schemaVersion = 1
        completed = ($null -eq $failure)
        completedAtUtc = $completedAtUtc.ToString('O')
        processId = $ProcessId
        processName = $initialProcess.ProcessName
        executableSha256 = $actualHash
        sampleCount = $sampleCount
        observedDurationSeconds = $observedDurationSeconds
        firstWorkingSetBytes = $firstWorkingSetBytes
        lastWorkingSetBytes = $lastWorkingSetBytes
        workingSetDeltaBytes = ($lastWorkingSetBytes - $firstWorkingSetBytes)
        minWorkingSetBytes = if ($sampleCount -gt 0) { $minWorkingSetBytes } else { 0 }
        maxWorkingSetBytes = $maxWorkingSetBytes
        firstPrivateBytes = $firstPrivateBytes
        lastPrivateBytes = $lastPrivateBytes
        privateBytesDeltaBytes = ($lastPrivateBytes - $firstPrivateBytes)
        minPrivateBytes = if ($sampleCount -gt 0) { $minPrivateBytes } else { 0 }
        maxPrivateBytes = $maxPrivateBytes
        minHandleCount = if ($sampleCount -gt 0) { $minHandleCount } else { 0 }
        maxHandleCount = $maxHandleCount
        minThreadCount = if ($sampleCount -gt 0) { $minThreadCount } else { 0 }
        maxThreadCount = $maxThreadCount
        failureType = if ($null -ne $failure) { $failure.GetType().FullName } else { $null }
        failureMessage = if ($null -ne $failure) { $failure.Message } else { $null }
        trendAssessment = 'Evidence only. No leak threshold is inferred automatically; review this series with workload/reconnect events and application managed-heap logs.'
    }
    Write-JsonFile -Path $summaryPath -Value $summary

    if ($null -eq $failure) {
        # Keep the result portable: it deliberately contains no absolute output path.
        # The bundle directory itself is the transport boundary and manifest paths are
        # relative to that root.
        Write-JsonFile -Path $resultPath -Value ([ordered]@{
            schemaVersion = 1
            completed = $true
            sampleCount = $sampleCount
            observedDurationSeconds = $observedDurationSeconds
            executableSha256 = $actualHash
        })
    }

    $manifestFiles = @()
    foreach ($fileName in @('metadata.json', 'process-samples.jsonl', 'summary.json', 'result.json')) {
        $filePath = Join-Path $outputPath $fileName
        if (Test-Path -LiteralPath $filePath -PathType Leaf) {
            $item = Get-Item -LiteralPath $filePath
            $manifestFiles += [ordered]@{
                path = $fileName
                length = [long]$item.Length
                sha256 = (Get-FileHash -LiteralPath $filePath -Algorithm SHA256).Hash.ToLowerInvariant()
            }
        }
    }

    $manifest = [ordered]@{
        schemaVersion = 1
        createdAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        files = $manifestFiles
    }
    Write-JsonFile -Path $manifestPath -Value $manifest
}

if ($null -ne $failure) {
    throw $failure
}

Get-Content -LiteralPath $summaryPath -Raw
