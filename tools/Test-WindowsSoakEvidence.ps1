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
    -not ([string]$summary.executableSha256).Equals($expectedHash, [StringComparison]::Ordinal) -or
    -not ([string]$resultFile.executableSha256).Equals($expectedHash, [StringComparison]::Ordinal)) {
    throw 'Soak evidence executable SHA-256 does not match the expected release binary.'
}

# Every emitted payload except the manifest itself must be covered by the manifest.
# This makes the directory portable and tamper-evident without embedding any host path.
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

$sampleLines = @(Get-Content -LiteralPath $samplesPath | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
if ($sampleLines.Count -lt $MinimumSamples) {
    throw "Soak evidence contains $($sampleLines.Count) sample(s); at least $MinimumSamples are required."
}
if ($sampleLines.Count -ne [int]$summary.sampleCount -or
    $sampleLines.Count -ne [int]$resultFile.sampleCount) {
    throw 'Soak sample count does not match summary/result metadata.'
}

$previousTimestamp = $null
for ($index = 0; $index -lt $sampleLines.Count; $index++) {
    $sample = $sampleLines[$index] | ConvertFrom-Json
    if ($sample.schemaVersion -ne 1 -or [int]$sample.index -ne $index) {
        throw "Soak sample index/schema mismatch at position $index."
    }
    if ([int]$sample.processId -ne [int]$metadata.processId -or
        -not ([string]$sample.processName).Equals(([string]$metadata.processName), [StringComparison]::OrdinalIgnoreCase) -or
        -not ([string]$sample.processStartTimeUtc).Equals(([string]$metadata.processStartTimeUtc), [StringComparison]::Ordinal)) {
        throw "Soak process identity drift detected at sample $index."
    }

    foreach ($propertyName in @('workingSetBytes', 'privateBytes', 'handleCount', 'threadCount')) {
        if ([long]$sample.$propertyName -lt 0) {
            throw "Soak sample $index contains negative '$propertyName'."
        }
    }

    $timestamp = [DateTimeOffset]::Parse([string]$sample.timestampUtc)
    if ($null -ne $previousTimestamp -and $timestamp -lt $previousTimestamp) {
        throw "Soak sample timestamps are not monotonic at sample $index."
    }
    $previousTimestamp = $timestamp
}

if ([double]$summary.observedDurationSeconds -lt $MinimumObservedDurationSeconds) {
    throw "Observed soak duration $($summary.observedDurationSeconds)s is below required ${MinimumObservedDurationSeconds}s."
}
if ([Math]::Abs([double]$summary.observedDurationSeconds - [double]$resultFile.observedDurationSeconds) -gt 0.001) {
    throw 'Soak result observedDurationSeconds does not match summary.json.'
}

$result = [ordered]@{
    schemaVersion = 1
    validated = $true
    processId = [int]$metadata.processId
    processName = [string]$metadata.processName
    executableSha256 = [string]$metadata.executableSha256
    sampleCount = $sampleLines.Count
    observedDurationSeconds = [double]$summary.observedDurationSeconds
    workingSetDeltaBytes = [long]$summary.workingSetDeltaBytes
    privateBytesDeltaBytes = [long]$summary.privateBytesDeltaBytes
    maxWorkingSetBytes = [long]$summary.maxWorkingSetBytes
    maxPrivateBytes = [long]$summary.maxPrivateBytes
    maxHandleCount = [int]$summary.maxHandleCount
    maxThreadCount = [int]$summary.maxThreadCount
    manifestFileCount = $manifestEntries.Count
    assessment = 'Integrity, portability and process identity validated. Memory-leak acceptance still requires workload-aware review of the series and application managed-heap logs.'
}

ConvertTo-Json -InputObject $result -Depth 4
