[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$OutputDirectory,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string[]]$ApplicationLogPath,

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

$outputPath = [IO.Path]::GetFullPath($OutputDirectory)
$metadata = Read-RequiredJson -Path (Join-Path $outputPath 'metadata.json')
$summary = Read-RequiredJson -Path (Join-Path $outputPath 'summary.json')

if ($metadata.schemaVersion -ne 1 -or $summary.schemaVersion -ne 1 -or -not $summary.completed) {
    throw 'Soak metadata/summary is not a completed schema-v1 evidence set.'
}

$expectedProcessId = [int]$metadata.processId
$expectedProcessStart = [DateTimeOffset]::Parse([string]$metadata.processStartTimeUtc).ToUniversalTime()
$soakStart = [DateTimeOffset]::Parse([string]$metadata.startedAtUtc).ToUniversalTime()
$soakEnd = [DateTimeOffset]::Parse([string]$summary.completedAtUtc).ToUniversalTime()
$windowStart = $soakStart.AddSeconds(-$ClockSkewSeconds)
$windowEnd = $soakEnd.AddSeconds($ClockSkewSeconds)

if ($soakEnd -lt $soakStart) {
    throw 'Soak evidence completion timestamp precedes its start timestamp.'
}

$records = [System.Collections.Generic.List[object]]::new()
foreach ($logPathInput in $ApplicationLogPath) {
    $logPath = [IO.Path]::GetFullPath($logPathInput)
    if (-not (Test-Path -LiteralPath $logPath -PathType Leaf)) {
        throw "Application JSONL log is missing: $logPath"
    }

    $lineNumber = 0
    foreach ($line in Get-Content -LiteralPath $logPath) {
        $lineNumber++
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

        $records.Add([pscustomobject]@{
            TimestampUtc = $timestamp
            Event = $eventName
            ManagedHeapBytes = [long]$entry.Data.ManagedHeapBytes
            TotalAllocatedBytes = [long]$entry.Data.TotalAllocatedBytes
            WorkingSetBytes = [long]$entry.Data.WorkingSetBytes
            PrivateBytes = [long]$entry.Data.PrivateBytes
            HandleCount = [int]$entry.Data.HandleCount
            ThreadCount = [int]$entry.Data.ThreadCount
        })
    }
}

$ordered = @($records | Sort-Object TimestampUtc)
if ($ordered.Count -lt $MinimumMemoryRecords) {
    throw "Found $($ordered.Count) process.memory record(s) for the exact soak process lifetime; at least $MinimumMemoryRecords are required."
}

$first = $ordered[0]
$last = $ordered[-1]
$result = [ordered]@{
    schemaVersion = 1
    correlated = $true
    processId = $expectedProcessId
    processStartTimeUtc = $expectedProcessStart.ToString('O')
    soakStartedAtUtc = $soakStart.ToString('O')
    soakCompletedAtUtc = $soakEnd.ToString('O')
    memoryRecordCount = $ordered.Count
    firstMemoryRecordUtc = $first.TimestampUtc.ToString('O')
    lastMemoryRecordUtc = $last.TimestampUtc.ToString('O')
    firstManagedHeapBytes = $first.ManagedHeapBytes
    lastManagedHeapBytes = $last.ManagedHeapBytes
    managedHeapDeltaBytes = $last.ManagedHeapBytes - $first.ManagedHeapBytes
    firstTotalAllocatedBytes = $first.TotalAllocatedBytes
    lastTotalAllocatedBytes = $last.TotalAllocatedBytes
    maxWorkingSetBytes = [long](($ordered | Measure-Object WorkingSetBytes -Maximum).Maximum)
    maxPrivateBytes = [long](($ordered | Measure-Object PrivateBytes -Maximum).Maximum)
    maxHandleCount = [int](($ordered | Measure-Object HandleCount -Maximum).Maximum)
    maxThreadCount = [int](($ordered | Measure-Object ThreadCount -Maximum).Maximum)
    assessment = 'External soak process identity and application managed-memory records are correlated. Leak acceptance remains workload-aware and is not inferred from one delta.'
}

ConvertTo-Json -InputObject $result -Depth 4
