[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $OutputDirectory,

    [Parameter(Mandatory = $true)]
    [ValidateSet('Baseline', 'Ready', 'Final')]
    [string] $Stage
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$stageName = $Stage.ToLowerInvariant()
$stageDirectory = Join-Path $OutputDirectory $stageName
$evidencePath = Join-Path $stageDirectory 'evidence.json'
$summaryPath = Join-Path $stageDirectory 'summary.json'
$manifestPath = Join-Path $stageDirectory 'manifest.json'

foreach ($requiredPath in @($evidencePath, $summaryPath)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Required integration evidence file is missing: $requiredPath"
    }
}

$evidence = Get-Content -LiteralPath $evidencePath -Raw | ConvertFrom-Json
$summary = Get-Content -LiteralPath $summaryPath -Raw | ConvertFrom-Json

if ($evidence.schemaVersion -ne 1) {
    throw "Unsupported evidence schemaVersion '$($evidence.schemaVersion)'."
}
if ($evidence.stage -ne $Stage -or $summary.stage -ne $Stage) {
    throw "Evidence/summary stage does not match requested stage '$Stage'."
}
if ($summary.failedAssertionCount -ne 0) {
    $failed = @($summary.failedAssertions) -join ', '
    throw "Integration evidence contains failed assertions: $failed"
}

$requiredCaptures = @(
    [pscustomobject]@{ Name = 'routes'; Value = $evidence.routes },
    [pscustomobject]@{ Name = 'vpnProfiles'; Value = $evidence.vpnProfiles },
    [pscustomobject]@{ Name = 'interfaces'; Value = $evidence.interfaces },
    [pscustomobject]@{ Name = 'process'; Value = $evidence.process }
)
foreach ($capture in $requiredCaptures) {
    if ($null -eq $capture.Value) {
        throw "Required integration capture '$($capture.Name)' is absent."
    }
    if (-not $capture.Value.succeeded) {
        throw "Required integration capture '$($capture.Name)' failed: $($capture.Value.error)"
    }
}

if ([string]::IsNullOrWhiteSpace([string]$evidence.routeFingerprint)) {
    throw 'Default-route fingerprint is missing.'
}
if ([string]::IsNullOrWhiteSpace([string]$evidence.profileFingerprint)) {
    throw 'VPN-profile fingerprint is missing.'
}

if ($Stage -ne 'Baseline') {
    $baselineDirectory = Join-Path $OutputDirectory 'baseline'
    foreach ($baselineFile in @('evidence.json', 'summary.json', 'default-routes.sha256', 'vpn-profiles.sha256')) {
        $path = Join-Path $baselineDirectory $baselineFile
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Baseline evidence required for '$Stage' is missing: $path"
        }
    }
}

$files = @(
    Get-ChildItem -LiteralPath $stageDirectory -Recurse -File |
        Where-Object { $_.FullName -ne $manifestPath } |
        Sort-Object FullName
)
if ($files.Count -lt 2) {
    throw 'Integration evidence bundle does not contain the expected files.'
}

$entries = @(
    foreach ($file in $files) {
        $hash = Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256
        [ordered]@{
            relativePath = [IO.Path]::GetRelativePath($stageDirectory, $file.FullName)
            length = $file.Length
            sha256 = $hash.Hash.ToLowerInvariant()
        }
    }
)

$manifest = [ordered]@{
    schemaVersion = 1
    stage = $Stage
    validatedAtUtc = [DateTimeOffset]::UtcNow
    fileCount = $entries.Count
    files = $entries
}
$manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifestPath -Encoding utf8

[ordered]@{
    stage = $Stage
    evidencePath = $evidencePath
    manifestPath = $manifestPath
    validatedFileCount = $entries.Count
    routeFingerprint = $evidence.routeFingerprint
    profileFingerprint = $evidence.profileFingerprint
} | ConvertTo-Json -Depth 4
