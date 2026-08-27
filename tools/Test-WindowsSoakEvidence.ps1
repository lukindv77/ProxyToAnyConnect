[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$OutputDirectory,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$ExpectedExecutableSha256,

    [ValidateRange(1, 10000000)]
    [int]$MinimumSamples = 2,

    [ValidateRange(0, 604800)]
    [int]$MinimumObservedDurationSeconds = 0
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Read-JsonFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Required soak evidence file is missing: $Path"
    }

    return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
}

function Get-RequiredJsonStringProperty {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Json,
        [Parameter(Mandatory = $true)]
        [string]$PropertyName,
        [Parameter(Mandatory = $true)]
        [string]$Context
    )

    $document = $null
    try {
        $document = [System.Text.Json.JsonDocument]::Parse($Json)
        $property = $document.RootElement.GetProperty($PropertyName)
        if ($property.ValueKind -ne [System.Text.Json.JsonValueKind]::String) {
            throw "'$PropertyName' is not a JSON string."
        }

        $value = $property.GetString()
        if ([string]::IsNullOrWhiteSpace($value)) {
            throw "'$PropertyName' is empty."
        }

        return $value
    }
    catch {
        throw "Invalid $Context '$PropertyName': $($_.Exception.Message)"
    }
    finally {
        if ($null -ne $document) {
            $document.Dispose()
        }
    }
}

function ConvertFrom-RoundTripTimestamp {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Value,
        [Parameter(Mandatory = $true)]
        [string]$Context
    )

    try {
        return [DateTimeOffset]::ParseExact(
            $Value,
            'O',
            [Globalization.CultureInfo]::InvariantCulture).ToUniversalTime()
    }
    catch {
        throw "Invalid $Context timestamp '$Value': $($_.Exception.Message)"
    }
}

$expectedHash = $ExpectedExecutableSha256.Trim().ToLowerInvariant()
if ($expectedHash -notmatch '^[0-9a-f]{64}$') {
    throw 'ExpectedExecutableSha256 must be exactly 64 hexadecimal characters.'
}

$outputPath = [IO.Path]::GetFullPath($OutputDirectory)
$metadataPath = Join-Path $outputPath 'metadata.json'
$samplesPath = Join-Path $outputPath 'process-samples.jsonl'
$summaryPath = Join-Path $outputPath 'summary.json'
$resultPath = Join-Path $outputPath 'result.json'
$manifestPath = Join-Path $outputPath 'manifest.json'

$metadata = Read-JsonFile -Path $metadataPath
$metadataJson = Get-Content -LiteralPath $metadataPath -Raw
$metadataProcessStartTimeUtc = Get-RequiredJsonStringProperty `
    -Json $metadataJson `
    -PropertyName 'processStartTimeUtc' `
    -Context 'soak metadata'
$summary = Read-JsonFile -Path $summaryPath
$resultFile = Read-JsonFile -Path $resultPath
$manifest = Read-JsonFile -Path $manifestPath

if ($metadata.schemaVersion -ne 1 -or
    $summary.schemaVersion -ne 1 -or
    $resultFile.schemaVersion -ne 1 -or
    $manifest.schemaVersion -ne 1) {
    throw 'Unsupported Windows soak evidence schema version.'
}

if (-not $summary.completed -or -not $resultFile.completed) {
    throw "Soak evidence collector did not complete: $($summary.failureType): $($summary.failureMessage)"
}

if (-not ([string]$metadata.executableSha256).Equals($expectedHash, [StringComparison]::Ordinal) -or
    -not ([string]$metadata.expectedExecutableSha256).Equals($expectedHash, [StringComparison]::Ordinal) -or
    -not ([string]$summary.executableSha256).Equals($expectedHash, [StringComparison]::Ordinal) -or
    -not ([string]$resultFile.executableSha256).Equals($expectedHash, [StringComparison]::Ordinal)) {
    throw 'Soak evidence executable SHA-256 does not match the expected release binary.'
}

# Every payload emitted by the collector except the manifest itself must be covered
# by exactly one bundle-relative manifest entry.
$requiredManifestNames = @('metadata.json', 'process-samples.jsonl', 'summary.json', 'result.json')
$manifestEntries = @($manifest.files)
if ($manifestEntries.Count -ne $requiredManifestNames.Count) {
    throw "Manifest contains $($manifestEntries.Count) file entry/entries; exactly $($requiredManifestNames.Count) are required."
}

foreach ($requiredName in $requiredManifestNames) {
    $entry = @($manifestEntries | Where-Object { $_.path -eq $requiredName })
    if ($entry.Count -ne 1) {
        throw "Manifest must contain exactly one '$requiredName' entry."
    }

    if ([IO.Path]::IsPathRooted([string]$entry[0].path) -or
        -not ([string]$entry[0].path).Equals($requiredName, [StringComparison]::Ordinal)) {
        throw "Manifest path '$($entry[0].path)' is not a portable bundle-relative path."
    }

    $filePath = Join-Path $outputPath $requiredName
    $actualLength = [long](Get-Item -LiteralPath $filePath).Length
    $actualHash = (Get-FileHash -LiteralPath $filePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualLength -ne [long]$entry[0].length) {
        throw "Manifest length mismatch for '$requiredName'."
    }
    if (-not $actualHash.Equals(([string]$entry[0].sha256).ToLowerInvariant(), [StringComparison]::Ordinal)) {
        throw "Manifest SHA-256 mismatch for '$requiredName'."
    }
}

# result.json is intentionally transport-neutral. Reject reintroduction of the old
# absolute outputDirectory field so a copied bundle remains machine-independent.
if ($null -ne $resultFile.PSObject.Properties['outputDirectory']) {
    throw 'Soak result.json must not contain a host-specific outputDirectory field.'
}

if (-not (Test-Path -LiteralPath $samplesPath -PathType Leaf)) {
    throw "Required soak sample stream is missing: $samplesPath"
}

# Validate the JSONL stream in one pass. A release soak may be deliberately sampled
# more frequently than the recommended 5-minute cadence; validation must therefore
# remain O(1) in retained memory instead of materializing every line in an array.
$sampleCount = 0
$previousTimestamp = $null
$firstTimestamp = $null
$lastTimestamp = $null
foreach ($line in Get-Content -LiteralPath $samplesPath) {
    if ([string]::IsNullOrWhiteSpace($line)) {
        continue
    }

    try {
        $sample = $line | ConvertFrom-Json
    }
    catch {
        throw "Soak sample JSON parse failure at logical sample ${sampleCount}: $($_.Exception.Message)"
    }

    if ($sample.schemaVersion -ne 1 -or [int]$sample.index -ne $sampleCount) {
        throw "Soak sample index/schema mismatch at position $sampleCount."
    }

    $sampleProcessStartTimeUtc = Get-RequiredJsonStringProperty `
        -Json $line `
        -PropertyName 'processStartTimeUtc' `
        -Context "soak sample $sampleCount"
    $sampleTimestampUtc = Get-RequiredJsonStringProperty `
        -Json $line `
        -PropertyName 'timestampUtc' `
        -Context "soak sample $sampleCount"

    if ([int]$sample.processId -ne [int]$metadata.processId -or
        -not ([string]$sample.processName).Equals(([string]$metadata.processName), [StringComparison]::OrdinalIgnoreCase) -or
        -not $sampleProcessStartTimeUtc.Equals($metadataProcessStartTimeUtc, [StringComparison]::Ordinal)) {
        throw "Soak process identity drift detected at sample $sampleCount."
    }

    foreach ($propertyName in @('workingSetBytes', 'privateBytes', 'handleCount', 'threadCount')) {
        $property = $sample.PSObject.Properties[$propertyName]
        if ($null -eq $property -or [long]$property.Value -lt 0) {
            throw "Soak sample $sampleCount contains invalid '$propertyName'."
        }
    }

    $timestamp = ConvertFrom-RoundTripTimestamp `
        -Value $sampleTimestampUtc `
        -Context "soak sample $sampleCount"
    if ($null -ne $previousTimestamp -and $timestamp -lt $previousTimestamp) {
        throw "Soak sample timestamps are not monotonic at sample $sampleCount."
    }

    if ($sampleCount -eq 0) {
        $firstTimestamp = $timestamp
    }
    $lastTimestamp = $timestamp
    $previousTimestamp = $timestamp
    $sampleCount++
}

if ($sampleCount -lt $MinimumSamples) {
    throw "Soak evidence contains $sampleCount sample(s); at least $MinimumSamples are required."
}
if ($sampleCount -ne [int]$summary.sampleCount -or
    $sampleCount -ne [int]$resultFile.sampleCount) {
    throw 'Soak sample count does not match summary/result metadata.'
}

if ([double]$summary.observedDurationSeconds -lt $MinimumObservedDurationSeconds) {
    throw "Observed soak duration $($summary.observedDurationSeconds)s is below required ${MinimumObservedDurationSeconds}s."
}
if ([Math]::Abs([double]$summary.observedDurationSeconds - [double]$resultFile.observedDurationSeconds) -gt 0.001) {
    throw 'Soak result observedDurationSeconds does not match summary.json.'
}

$computedObservedDurationSeconds = if ($null -ne $firstTimestamp -and $null -ne $lastTimestamp) {
    [Math]::Max(0.0, ($lastTimestamp - $firstTimestamp).TotalSeconds)
}
else {
    0.0
}
if ([Math]::Abs($computedObservedDurationSeconds - [double]$summary.observedDurationSeconds) -gt 0.050) {
    throw 'Soak summary observedDurationSeconds does not match the validated sample timestamp span.'
}

$result = [ordered]@{
    schemaVersion = 1
    validated = $true
    processId = [int]$metadata.processId
    processName = [string]$metadata.processName
    processStartTimeUtc = $metadataProcessStartTimeUtc
    executableSha256 = [string]$metadata.executableSha256
    sampleCount = $sampleCount
    observedDurationSeconds = [double]$summary.observedDurationSeconds
    workingSetDeltaBytes = [long]$summary.workingSetDeltaBytes
    privateBytesDeltaBytes = [long]$summary.privateBytesDeltaBytes
    maxWorkingSetBytes = [long]$summary.maxWorkingSetBytes
    maxPrivateBytes = [long]$summary.maxPrivateBytes
    maxHandleCount = [int]$summary.maxHandleCount
    maxThreadCount = [int]$summary.maxThreadCount
    manifestFileCount = $manifestEntries.Count
    validationMemoryModel = 'streaming-o1'
    assessment = 'Integrity, portability and process identity validated. Memory-leak acceptance still requires workload-aware review of the series and application managed-heap logs.'
}

ConvertTo-Json -InputObject $result -Depth 4
