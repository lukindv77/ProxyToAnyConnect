[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$OutputDirectory,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$ExpectedExecutableSha256,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string[]]$ApplicationLogPath,

    [ValidateRange(1, 10000000)]
    [int]$MinimumSoakSamples = 1,

    [ValidateRange(0, 604800)]
    [int]$MinimumObservedDurationSeconds = 0,

    [ValidateRange(1, 10000000)]
    [int]$MinimumMemoryRecords = 1,

    [ValidateRange(0, 3600)]
    [int]$ClockSkewSeconds = 5
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Read-RequiredJson {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Required soak evidence file is missing: $Path"
    }

    return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
}

$expectedHash = $ExpectedExecutableSha256.Trim().ToLowerInvariant()
if ($expectedHash -notmatch '^[0-9a-f]{64}$') {
    throw 'ExpectedExecutableSha256 must be exactly 64 hexadecimal characters.'
}

$outputPath = [IO.Path]::GetFullPath($OutputDirectory)
$bundleValidatorPath = Join-Path $PSScriptRoot 'Test-WindowsSoakEvidence.ps1'
if (-not (Test-Path -LiteralPath $bundleValidatorPath -PathType Leaf)) {
    throw "Soak evidence validator is missing: $bundleValidatorPath"
}

# Correlation is never allowed to trust metadata/summary independently of the sealed
# bundle. Validate manifest lengths/hashes, exact executable identity and the sample
# stream first, so a modified PID/start-time cannot be made to agree with unrelated
# application logs after collection.
$bundleValidationJson = & $bundleValidatorPath `
    -OutputDirectory $outputPath `
    -ExpectedExecutableSha256 $expectedHash `
    -MinimumSamples $MinimumSoakSamples `
    -MinimumObservedDurationSeconds $MinimumObservedDurationSeconds
$bundleValidation = $bundleValidationJson | ConvertFrom-Json
if ($bundleValidation.schemaVersion -ne 1 -or -not $bundleValidation.validated) {
    throw 'Soak evidence bundle did not pass its integrity/identity validator before log correlation.'
}

$metadata = Read-RequiredJson -Path (Join-Path $outputPath 'metadata.json')
$summary = Read-RequiredJson -Path (Join-Path $outputPath 'summary.json')

$expectedProcessId = [int]$metadata.processId
$expectedProcessStart = [DateTimeOffset]::Parse([string]$metadata.processStartTimeUtc).ToUniversalTime()
$soakStart = [DateTimeOffset]::Parse([string]$metadata.startedAtUtc).ToUniversalTime()
$soakEnd = [DateTimeOffset]::Parse([string]$summary.completedAtUtc).ToUniversalTime()
$windowStart = $soakStart.AddSeconds(-$ClockSkewSeconds)
$windowEnd = $soakEnd.AddSeconds($ClockSkewSeconds)

if ($soakEnd -lt $soakStart) {
    throw 'Soak evidence completion timestamp precedes its start timestamp.'
}

$recordCount = 0
$logFileCount = 0
$linesScanned = 0L
$memoryRecordsOutsideWindow = 0L
$firstTimestamp = $null
$lastTimestamp = $null
$firstManagedHeapBytes = 0L
$lastManagedHeapBytes = 0L
$firstTotalAllocatedBytes = 0L
$lastTotalAllocatedBytes = 0L
$maxWorkingSetBytes = 0L
$maxPrivateBytes = 0L
$maxHandleCount = 0
$maxThreadCount = 0
$maxGen0Collections = 0
$maxGen1Collections = 0
$maxGen2Collections = 0

# Scan JSONL one line at a time and retain only scalar aggregates. Multi-day log files
# can be large; validation itself must not create an unbounded in-memory history.
foreach ($logPathInput in $ApplicationLogPath) {
    $logPath = [IO.Path]::GetFullPath($logPathInput)
    if (-not (Test-Path -LiteralPath $logPath -PathType Leaf)) {
        throw "Application JSONL log is missing: $logPath"
    }

    $logFileCount++
    $lineNumber = 0
    foreach ($line in Get-Content -LiteralPath $logPath) {
        $lineNumber++
        $linesScanned++
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }

        try {
            $entry = $line | ConvertFrom-Json
        }
        catch {
            throw "Application JSONL parse failure at ${logPath}:${lineNumber}: $($_.Exception.Message)"
        }

        $eventName = [string]$entry.Event
        if ($eventName -notin @('process.memory.startup', 'process.memory.periodic')) {
            continue
        }

        $timestamp = [DateTimeOffset]::Parse([string]$entry.TimestampUtc).ToUniversalTime()
        if ($timestamp -lt $windowStart -or $timestamp -gt $windowEnd) {
            $memoryRecordsOutsideWindow++
            continue
        }

        if ($null -eq $entry.Data) {
            throw "Memory-health log entry at ${logPath}:${lineNumber} has no Data payload."
        }

        $processIdProperty = $entry.Data.PSObject.Properties['ProcessId']
        $processStartProperty = $entry.Data.PSObject.Properties['ProcessStartTimeUtc']
        if ($null -eq $processIdProperty -or $null -eq $processStartProperty) {
            throw "Memory-health log entry at ${logPath}:${lineNumber} lacks ProcessId/ProcessStartTimeUtc identity fields."
        }

        $processId = [int]$processIdProperty.Value
        $processStart = [DateTimeOffset]::Parse([string]$processStartProperty.Value).ToUniversalTime()
        if ($processId -ne $expectedProcessId -or $processStart -ne $expectedProcessStart) {
            throw "Memory-health log process identity mismatch at ${logPath}:${lineNumber}: " +
                  "pid=$processId/$expectedProcessId start=$($processStart.ToString('O'))/$($expectedProcessStart.ToString('O'))."
        }

        foreach ($requiredMetric in @(
            'ManagedHeapBytes',
            'TotalAllocatedBytes',
            'WorkingSetBytes',
            'PrivateBytes',
            'Gen0Collections',
            'Gen1Collections',
            'Gen2Collections',
            'HandleCount',
            'ThreadCount')) {
            $property = $entry.Data.PSObject.Properties[$requiredMetric]
            if ($null -eq $property -or [long]$property.Value -lt 0) {
                throw "Memory-health log entry at ${logPath}:${lineNumber} has invalid '$requiredMetric'."
            }
        }

        $managedHeapBytes = [long]$entry.Data.ManagedHeapBytes
        $totalAllocatedBytes = [long]$entry.Data.TotalAllocatedBytes
        $workingSetBytes = [long]$entry.Data.WorkingSetBytes
        $privateBytes = [long]$entry.Data.PrivateBytes
        $handleCount = [int]$entry.Data.HandleCount
        $threadCount = [int]$entry.Data.ThreadCount
        $gen0Collections = [int]$entry.Data.Gen0Collections
        $gen1Collections = [int]$entry.Data.Gen1Collections
        $gen2Collections = [int]$entry.Data.Gen2Collections

        if ($null -eq $firstTimestamp -or $timestamp -lt $firstTimestamp) {
            $firstTimestamp = $timestamp
            $firstManagedHeapBytes = $managedHeapBytes
            $firstTotalAllocatedBytes = $totalAllocatedBytes
        }
        if ($null -eq $lastTimestamp -or $timestamp -gt $lastTimestamp) {
            $lastTimestamp = $timestamp
            $lastManagedHeapBytes = $managedHeapBytes
            $lastTotalAllocatedBytes = $totalAllocatedBytes
        }

        $maxWorkingSetBytes = [Math]::Max($maxWorkingSetBytes, $workingSetBytes)
        $maxPrivateBytes = [Math]::Max($maxPrivateBytes, $privateBytes)
        $maxHandleCount = [Math]::Max($maxHandleCount, $handleCount)
        $maxThreadCount = [Math]::Max($maxThreadCount, $threadCount)
        $maxGen0Collections = [Math]::Max($maxGen0Collections, $gen0Collections)
        $maxGen1Collections = [Math]::Max($maxGen1Collections, $gen1Collections)
        $maxGen2Collections = [Math]::Max($maxGen2Collections, $gen2Collections)
        $recordCount++
    }
}

if ($recordCount -lt $MinimumMemoryRecords) {
    throw "Found $recordCount process.memory record(s) for the exact soak process lifetime; at least $MinimumMemoryRecords are required."
}

$result = [ordered]@{
    schemaVersion = 1
    correlated = $true
    bundleValidated = $true
    executableSha256 = $expectedHash
    processId = $expectedProcessId
    processStartTimeUtc = $expectedProcessStart.ToString('O')
    soakStartedAtUtc = $soakStart.ToString('O')
    soakCompletedAtUtc = $soakEnd.ToString('O')
    soakSampleCount = [int]$bundleValidation.sampleCount
    soakObservedDurationSeconds = [double]$bundleValidation.observedDurationSeconds
    applicationLogFileCount = $logFileCount
    applicationLogLinesScanned = $linesScanned
    memoryRecordsOutsideWindow = $memoryRecordsOutsideWindow
    memoryRecordCount = $recordCount
    firstMemoryRecordUtc = $firstTimestamp.ToString('O')
    lastMemoryRecordUtc = $lastTimestamp.ToString('O')
    firstManagedHeapBytes = $firstManagedHeapBytes
    lastManagedHeapBytes = $lastManagedHeapBytes
    managedHeapDeltaBytes = $lastManagedHeapBytes - $firstManagedHeapBytes
    firstTotalAllocatedBytes = $firstTotalAllocatedBytes
    lastTotalAllocatedBytes = $lastTotalAllocatedBytes
    maxWorkingSetBytes = $maxWorkingSetBytes
    maxPrivateBytes = $maxPrivateBytes
    maxHandleCount = $maxHandleCount
    maxThreadCount = $maxThreadCount
    maxGen0Collections = $maxGen0Collections
    maxGen1Collections = $maxGen1Collections
    maxGen2Collections = $maxGen2Collections
    validationMemoryModel = 'streaming-o1'
    assessment = 'Sealed external soak evidence and application managed-memory records are correlated to one exact executable/process lifetime. Leak acceptance remains workload-aware and is not inferred from one delta.'
}

ConvertTo-Json -InputObject $result -Depth 4
