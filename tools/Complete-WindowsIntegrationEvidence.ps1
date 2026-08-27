[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $OutputDirectory,

    [string[]] $ProxyEndpoint = @(),

    [string] $ExpectedVpnPublicIPv4 = '',

    [switch] $RequireExternalProbes,

    [switch] $RequireProxyHttpProbe,

    [switch] $AllowDirectPublicIPv4Match
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Read-StageBundle {
    param(
        [Parameter(Mandatory = $true)][string] $Stage
    )

    $stageName = $Stage.ToLowerInvariant()
    $stageDirectory = Join-Path $OutputDirectory $stageName
    $evidencePath = Join-Path $stageDirectory 'evidence.json'
    $summaryPath = Join-Path $stageDirectory 'summary.json'
    $manifestPath = Join-Path $stageDirectory 'manifest.json'

    foreach ($path in @($evidencePath, $summaryPath, $manifestPath)) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Required '$Stage' integration evidence file is missing: $path"
        }
    }

    $evidence = Get-Content -LiteralPath $evidencePath -Raw | ConvertFrom-Json
    $summary = Get-Content -LiteralPath $summaryPath -Raw | ConvertFrom-Json
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json

    if ($evidence.schemaVersion -ne 1 -or $evidence.stage -ne $Stage) {
        throw "Unexpected evidence schema/stage in '$evidencePath'."
    }
    if ($summary.stage -ne $Stage -or $summary.failedAssertionCount -ne 0) {
        throw "Stage '$Stage' has failed assertions or an invalid summary."
    }
    if ($manifest.schemaVersion -ne 1 -or $manifest.stage -ne $Stage -or $manifest.fileCount -lt 2) {
        throw "Stage '$Stage' has an invalid/incomplete integrity manifest."
    }

    return [pscustomobject]@{
        Evidence = $evidence
        Summary = $summary
        Manifest = $manifest
        EvidencePath = $evidencePath
        ManifestPath = $manifestPath
    }
}

function Get-SuccessfulProbeOutput {
    param(
        [Parameter(Mandatory = $true)] $Evidence,
        [Parameter(Mandatory = $true)][string] $Name
    )

    $probe = @($Evidence.probes | Where-Object { $_.name -eq $Name }) | Select-Object -First 1
    if ($null -eq $probe) {
        throw "Required external integration probe '$Name' is missing from Ready evidence."
    }
    if (-not $probe.succeeded) {
        throw "External integration probe capture '$Name' failed: $($probe.error)"
    }
    if ($null -eq $probe.value -or -not $probe.value.succeeded) {
        $errorText = if ($null -eq $probe.value) { 'no probe result' } else { $probe.value.output }
        throw "External integration probe '$Name' failed: $errorText"
    }

    return ([string]$probe.value.output).Trim()
}

$validator = Join-Path $PSScriptRoot 'Test-WindowsIntegrationEvidence.ps1'
if (-not (Test-Path -LiteralPath $validator -PathType Leaf)) {
    throw "Stage validator is missing: $validator"
}

# Re-run stage validation before aggregation so the final verdict never trusts a
# stale summary/manifest produced before evidence files changed.
foreach ($stage in @('Baseline', 'Ready', 'Final')) {
    & $validator -Stage $stage -OutputDirectory $OutputDirectory | Out-Null
}

$baseline = Read-StageBundle -Stage 'Baseline'
$ready = Read-StageBundle -Stage 'Ready'
$final = Read-StageBundle -Stage 'Final'

$routeFingerprint = [string]$baseline.Evidence.routeFingerprint
if ([string]::IsNullOrWhiteSpace($routeFingerprint) -or
    $routeFingerprint -ne [string]$ready.Evidence.routeFingerprint -or
    $routeFingerprint -ne [string]$final.Evidence.routeFingerprint) {
    throw 'IPv4 default-route fingerprint changed between Baseline, Ready and Final checkpoints.'
}

$profileFingerprint = [string]$baseline.Evidence.profileFingerprint
if ([string]::IsNullOrWhiteSpace($profileFingerprint) -or
    $profileFingerprint -ne [string]$final.Evidence.profileFingerprint) {
    throw 'Windows VPN profile fingerprint changed between Baseline and Final checkpoints.'
}

$directPublicIPv4 = $null
$proxyPublicIPv4 = [ordered]@{}
$proxyHttpValidated = [ordered]@{}

if ($RequireExternalProbes) {
    if ($ProxyEndpoint.Count -eq 0) {
        throw '-RequireExternalProbes requires at least one -ProxyEndpoint.'
    }

    $directPublicIPv4 = Get-SuccessfulProbeOutput -Evidence $ready.Evidence -Name 'directHttps'
    foreach ($endpoint in $ProxyEndpoint) {
        if ([string]::IsNullOrWhiteSpace($endpoint)) {
            throw 'ProxyEndpoint values must not be empty.'
        }

        $proxyOutput = Get-SuccessfulProbeOutput -Evidence $ready.Evidence -Name "proxyHttps:$endpoint"
        $proxyPublicIPv4[$endpoint] = $proxyOutput

        if (-not [string]::IsNullOrWhiteSpace($ExpectedVpnPublicIPv4) -and
            $proxyOutput -ne $ExpectedVpnPublicIPv4) {
            throw "Proxy '$endpoint' egress '$proxyOutput' does not match expected VPN public IPv4 '$ExpectedVpnPublicIPv4'."
        }

        if (-not $AllowDirectPublicIPv4Match -and $directPublicIPv4 -eq $proxyOutput) {
            throw "Direct host egress and proxy '$endpoint' egress are both '$proxyOutput'. Use -AllowDirectPublicIPv4Match only when this is intentionally expected."
        }

        if ($RequireProxyHttpProbe) {
            $null = Get-SuccessfulProbeOutput -Evidence $ready.Evidence -Name "proxyHttp:$endpoint"
            $proxyHttpValidated[$endpoint] = $true
        }
    }
}
elseif ($RequireProxyHttpProbe) {
    throw '-RequireProxyHttpProbe also requires -RequireExternalProbes.'
}

$acceptancePath = Join-Path $OutputDirectory 'acceptance-summary.json'
$acceptance = [ordered]@{
    schemaVersion = 1
    passed = $true
    completedAtUtc = [DateTimeOffset]::UtcNow
    routeFingerprint = $routeFingerprint
    baselineProfileFingerprint = $profileFingerprint
    finalProfileFingerprint = [string]$final.Evidence.profileFingerprint
    externalProbesRequired = [bool]$RequireExternalProbes
    proxyHttpProbeRequired = [bool]$RequireProxyHttpProbe
    allowDirectPublicIPv4Match = [bool]$AllowDirectPublicIPv4Match
    directPublicIPv4 = $directPublicIPv4
    expectedVpnPublicIPv4 = if ([string]::IsNullOrWhiteSpace($ExpectedVpnPublicIPv4)) { $null } else { $ExpectedVpnPublicIPv4 }
    proxyPublicIPv4 = $proxyPublicIPv4
    proxyHttpValidated = $proxyHttpValidated
    stages = [ordered]@{
        Baseline = [ordered]@{
            evidencePath = $baseline.EvidencePath
            manifestPath = $baseline.ManifestPath
            validatedFileCount = $baseline.Manifest.fileCount
        }
        Ready = [ordered]@{
            evidencePath = $ready.EvidencePath
            manifestPath = $ready.ManifestPath
            validatedFileCount = $ready.Manifest.fileCount
        }
        Final = [ordered]@{
            evidencePath = $final.EvidencePath
            manifestPath = $final.ManifestPath
            validatedFileCount = $final.Manifest.fileCount
        }
    }
}

$acceptance | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $acceptancePath -Encoding utf8
$acceptance | ConvertTo-Json -Depth 8
