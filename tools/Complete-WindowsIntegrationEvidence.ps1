[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $OutputDirectory,

    [string[]] $ProxyEndpoint = @(),

    # Backward-compatible default expected public IPv4 for every proxy endpoint.
    [string] $ExpectedVpnPublicIPv4 = '',

    # Optional per-endpoint override for heterogeneous shared/dedicated groups.
    [hashtable] $ExpectedProxyPublicIPv4 = @{},

    # Optional expected public IPv4 for the ordinary, non-proxy host path.
    [string] $ExpectedDirectPublicIPv4 = '',

    [string] $ExpectedExecutableSha256 = '',

    [switch] $RequireExternalProbes,

    [switch] $RequireProxyHttpProbe,

    [switch] $RequireProcessLifecycle,

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
        SummaryPath = $summaryPath
        ManifestPath = $manifestPath
    }
}

function Get-OptionalPropertyValue {
    param(
        [Parameter(Mandatory = $true)]
        [AllowNull()]
        $Object,

        [Parameter(Mandatory = $true)]
        [string] $Name
    )

    if ($null -eq $Object) {
        return $null
    }

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) {
        return $null
    }

    return $property.Value
}

function Assert-IPv4Expectation {
    param(
        [Parameter(Mandatory = $true)][string] $Name,
        [AllowEmptyString()][string] $Value,
        [switch] $RequireValue
    )

    if ([string]::IsNullOrWhiteSpace($Value)) {
        if ($RequireValue) {
            throw "$Name must contain an IPv4 address."
        }
        return
    }

    $parsed = $null
    if (-not [System.Net.IPAddress]::TryParse($Value, [ref]$parsed) -or
        $parsed.AddressFamily -ne [System.Net.Sockets.AddressFamily]::InterNetwork) {
        throw "$Name must be an IPv4 address, got '$Value'."
    }
}

function Get-CapturedProcesses {
    param(
        [Parameter(Mandatory = $true)] $Bundle,
        [Parameter(Mandatory = $true)][string] $Stage
    )

    if ($null -eq $Bundle.Evidence.process -or -not $Bundle.Evidence.process.succeeded) {
        throw "Required '$Stage' process evidence capture did not succeed."
    }

    return @($Bundle.Evidence.process.value | Where-Object { $null -ne $_ })
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

function Get-RecordedExpectationContract {
    param([Parameter(Mandatory = $true)] $Evidence)

    $metadata = Get-OptionalPropertyValue -Object $Evidence -Name 'expectations'
    if ($null -eq $metadata) {
        throw 'Ready evidence does not contain expectation-contract metadata.'
    }

    $direct = [string](Get-OptionalPropertyValue -Object $metadata -Name 'directPublicIPv4')
    $defaultProxy = [string](Get-OptionalPropertyValue -Object $metadata -Name 'defaultProxyPublicIPv4')
    $proxy = [ordered]@{}
    foreach ($record in @((Get-OptionalPropertyValue -Object $metadata -Name 'proxyPublicIPv4') | Where-Object { $null -ne $_ })) {
        $endpoint = [string](Get-OptionalPropertyValue -Object $record -Name 'endpoint')
        $expected = [string](Get-OptionalPropertyValue -Object $record -Name 'publicIPv4')
        if ([string]::IsNullOrWhiteSpace($endpoint) -or $proxy.Contains($endpoint)) {
            throw "Ready evidence contains an invalid/duplicate per-proxy expectation endpoint '$endpoint'."
        }
        Assert-IPv4Expectation -Name "Ready expectations.proxyPublicIPv4[$endpoint]" -Value $expected -RequireValue
        $proxy[$endpoint] = $expected
    }

    if (-not [string]::IsNullOrWhiteSpace($direct)) {
        Assert-IPv4Expectation -Name 'Ready expectations.directPublicIPv4' -Value $direct
    }
    if (-not [string]::IsNullOrWhiteSpace($defaultProxy)) {
        Assert-IPv4Expectation -Name 'Ready expectations.defaultProxyPublicIPv4' -Value $defaultProxy
    }

    return [pscustomobject]@{
        Direct = $direct
        DefaultProxy = $defaultProxy
        Proxy = $proxy
    }
}

Assert-IPv4Expectation -Name 'ExpectedVpnPublicIPv4' -Value $ExpectedVpnPublicIPv4
Assert-IPv4Expectation -Name 'ExpectedDirectPublicIPv4' -Value $ExpectedDirectPublicIPv4

$seenProxyEndpoints = @{}
foreach ($endpointValue in $ProxyEndpoint) {
    $endpoint = [string]$endpointValue
    if ([string]::IsNullOrWhiteSpace($endpoint)) {
        throw 'ProxyEndpoint values must not be empty.'
    }
    if ($seenProxyEndpoints.ContainsKey($endpoint)) {
        throw "ProxyEndpoint contains duplicate endpoint '$endpoint'."
    }
    $seenProxyEndpoints[$endpoint] = $true
}

if ($null -eq $ExpectedProxyPublicIPv4) {
    $ExpectedProxyPublicIPv4 = @{}
}
$requestedProxyExpectations = [ordered]@{}
foreach ($key in @($ExpectedProxyPublicIPv4.Keys | Sort-Object)) {
    $endpoint = [string]$key
    $expected = [string]$ExpectedProxyPublicIPv4[$key]
    if ([string]::IsNullOrWhiteSpace($endpoint)) {
        throw 'ExpectedProxyPublicIPv4 endpoint keys must not be empty.'
    }
    if (-not $seenProxyEndpoints.ContainsKey($endpoint)) {
        throw "ExpectedProxyPublicIPv4 endpoint '$endpoint' is not present in -ProxyEndpoint."
    }
    Assert-IPv4Expectation -Name "ExpectedProxyPublicIPv4[$endpoint]" -Value $expected -RequireValue
    $requestedProxyExpectations[$endpoint] = $expected
}

$hasRequestedEgressExpectations =
    -not [string]::IsNullOrWhiteSpace($ExpectedVpnPublicIPv4) -or
    -not [string]::IsNullOrWhiteSpace($ExpectedDirectPublicIPv4) -or
    $requestedProxyExpectations.Count -gt 0
if ($hasRequestedEgressExpectations -and -not $RequireExternalProbes) {
    throw 'Public-IPv4 expectations require -RequireExternalProbes so they cannot be silently accepted without probe validation.'
}
if ($RequireProxyHttpProbe -and -not $RequireExternalProbes) {
    throw '-RequireProxyHttpProbe also requires -RequireExternalProbes.'
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

$baselineProcesses = @(Get-CapturedProcesses -Bundle $baseline -Stage 'Baseline')
$readyProcesses = @(Get-CapturedProcesses -Bundle $ready -Stage 'Ready')
$finalProcesses = @(Get-CapturedProcesses -Bundle $final -Stage 'Final')

if ($RequireProcessLifecycle) {
    if ($baselineProcesses.Count -ne 0) {
        throw "Process lifecycle acceptance expected no ProxyToAnyConnect process at Baseline, found $($baselineProcesses.Count)."
    }
    if ($readyProcesses.Count -ne 1) {
        throw "Process lifecycle acceptance expected exactly one ProxyToAnyConnect process at Ready, found $($readyProcesses.Count)."
    }
    if ($finalProcesses.Count -ne 0) {
        throw "Process lifecycle acceptance expected no ProxyToAnyConnect process after explicit Exit, found $($finalProcesses.Count)."
    }
}

$expectedExecutableHash = $ExpectedExecutableSha256.Trim().ToLowerInvariant()
if (-not [string]::IsNullOrWhiteSpace($expectedExecutableHash) -and
    $expectedExecutableHash -notmatch '^[0-9a-f]{64}$') {
    throw '-ExpectedExecutableSha256 must be exactly 64 hexadecimal SHA-256 characters.'
}

$readyExecutableSha256 = $null
if (-not [string]::IsNullOrWhiteSpace($expectedExecutableHash)) {
    if ($readyProcesses.Count -ne 1) {
        throw "Exact binary acceptance requires exactly one ProxyToAnyConnect process at Ready, found $($readyProcesses.Count)."
    }

    $readyExecutableSha256 = [string](Get-OptionalPropertyValue -Object $readyProcesses[0] -Name 'ExecutableSha256')
    $readyExecutableHashError = [string](Get-OptionalPropertyValue -Object $readyProcesses[0] -Name 'ExecutableHashError')
    if ([string]::IsNullOrWhiteSpace($readyExecutableSha256)) {
        $detail = if ([string]::IsNullOrWhiteSpace($readyExecutableHashError)) {
            'no hash or hash-error metadata was recorded'
        }
        else {
            "capture error type '$readyExecutableHashError'"
        }
        throw "Ready process executable SHA-256 was not captured ($detail)."
    }

    $readyExecutableSha256 = $readyExecutableSha256.Trim().ToLowerInvariant()
    if ($readyExecutableSha256 -ne $expectedExecutableHash) {
        throw "Ready ProxyToAnyConnect executable SHA-256 '$readyExecutableSha256' does not match expected CI binary '$expectedExecutableHash'."
    }
}
elseif ($readyProcesses.Count -eq 1) {
    $capturedHash = [string](Get-OptionalPropertyValue -Object $readyProcesses[0] -Name 'ExecutableSha256')
    if (-not [string]::IsNullOrWhiteSpace($capturedHash)) {
        $readyExecutableSha256 = $capturedHash.Trim().ToLowerInvariant()
    }
}

$recordedExpectations = Get-RecordedExpectationContract -Evidence $ready.Evidence
$recordedHasEgressExpectations =
    -not [string]::IsNullOrWhiteSpace($recordedExpectations.Direct) -or
    -not [string]::IsNullOrWhiteSpace($recordedExpectations.DefaultProxy) -or
    $recordedExpectations.Proxy.Count -gt 0
if ($recordedHasEgressExpectations -and -not $RequireExternalProbes) {
    throw 'Ready evidence contains public-IPv4 expectations; aggregate acceptance must use -RequireExternalProbes.'
}

$directPublicIPv4 = $null
$proxyPublicIPv4 = [ordered]@{}
$effectiveExpectedProxyPublicIPv4 = [ordered]@{}
$proxyHttpValidated = [ordered]@{}

if ($RequireExternalProbes) {
    if ($ProxyEndpoint.Count -eq 0) {
        throw '-RequireExternalProbes requires at least one -ProxyEndpoint.'
    }

    if ([string]$recordedExpectations.Direct -ne [string]$ExpectedDirectPublicIPv4) {
        throw "Ready evidence direct egress expectation '$($recordedExpectations.Direct)' does not match aggregate expectation '$ExpectedDirectPublicIPv4'."
    }
    if ([string]$recordedExpectations.DefaultProxy -ne [string]$ExpectedVpnPublicIPv4) {
        throw "Ready evidence default proxy egress expectation '$($recordedExpectations.DefaultProxy)' does not match aggregate expectation '$ExpectedVpnPublicIPv4'."
    }
    if ($recordedExpectations.Proxy.Count -ne $requestedProxyExpectations.Count) {
        throw 'Ready evidence per-proxy expectation set does not match aggregate expectation set.'
    }
    foreach ($endpoint in $requestedProxyExpectations.Keys) {
        if (-not $recordedExpectations.Proxy.Contains($endpoint) -or
            [string]$recordedExpectations.Proxy[$endpoint] -ne [string]$requestedProxyExpectations[$endpoint]) {
            throw "Ready evidence per-proxy expectation for '$endpoint' does not match aggregate expectation."
        }
    }

    $directPublicIPv4 = Get-SuccessfulProbeOutput -Evidence $ready.Evidence -Name 'directHttps'
    Assert-IPv4Expectation -Name 'Observed direct public IPv4' -Value $directPublicIPv4 -RequireValue
    if (-not [string]::IsNullOrWhiteSpace($ExpectedDirectPublicIPv4) -and
        $directPublicIPv4 -ne $ExpectedDirectPublicIPv4) {
        throw "Direct host egress '$directPublicIPv4' does not match expected public IPv4 '$ExpectedDirectPublicIPv4'."
    }

    foreach ($endpoint in $ProxyEndpoint) {
        $proxyOutput = Get-SuccessfulProbeOutput -Evidence $ready.Evidence -Name "proxyHttps:$endpoint"
        Assert-IPv4Expectation -Name "Observed proxy '$endpoint' public IPv4" -Value $proxyOutput -RequireValue
        $proxyPublicIPv4[$endpoint] = $proxyOutput

        $expectedProxyOutput = if ($requestedProxyExpectations.Contains($endpoint)) {
            [string]$requestedProxyExpectations[$endpoint]
        }
        else {
            $ExpectedVpnPublicIPv4
        }
        $effectiveExpectedProxyPublicIPv4[$endpoint] = if ([string]::IsNullOrWhiteSpace($expectedProxyOutput)) { $null } else { $expectedProxyOutput }

        if (-not [string]::IsNullOrWhiteSpace($expectedProxyOutput) -and
            $proxyOutput -ne $expectedProxyOutput) {
            throw "Proxy '$endpoint' egress '$proxyOutput' does not match expected public IPv4 '$expectedProxyOutput'."
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

function New-StageSummary {
    param(
        [Parameter(Mandatory = $true)] $Bundle,
        [Parameter(Mandatory = $true)][int] $ProcessCount
    )

    return [ordered]@{
        evidencePath = $Bundle.EvidencePath
        summaryPath = $Bundle.SummaryPath
        manifestPath = $Bundle.ManifestPath
        validatedFileCount = $Bundle.Manifest.fileCount
        routeFingerprint = [string]$Bundle.Evidence.routeFingerprint
        profileFingerprint = [string]$Bundle.Evidence.profileFingerprint
        processCount = $ProcessCount
        assertionCount = $Bundle.Summary.assertionCount
        failedAssertionCount = $Bundle.Summary.failedAssertionCount
    }
}

$acceptancePath = Join-Path $OutputDirectory 'acceptance-summary.json'
$acceptance = [ordered]@{
    schemaVersion = 1
    passed = $true
    completedAtUtc = [DateTimeOffset]::UtcNow
    routeFingerprint = $routeFingerprint
    baselineProfileFingerprint = $profileFingerprint
    finalProfileFingerprint = [string]$final.Evidence.profileFingerprint
    processLifecycleRequired = [bool]$RequireProcessLifecycle
    expectedExecutableSha256 = if ([string]::IsNullOrWhiteSpace($expectedExecutableHash)) { $null } else { $expectedExecutableHash }
    readyExecutableSha256 = $readyExecutableSha256
    externalProbesRequired = [bool]$RequireExternalProbes
    proxyHttpProbeRequired = [bool]$RequireProxyHttpProbe
    allowDirectPublicIPv4Match = [bool]$AllowDirectPublicIPv4Match
    expectedDirectPublicIPv4 = if ([string]::IsNullOrWhiteSpace($ExpectedDirectPublicIPv4)) { $null } else { $ExpectedDirectPublicIPv4 }
    directPublicIPv4 = $directPublicIPv4
    expectedVpnPublicIPv4 = if ([string]::IsNullOrWhiteSpace($ExpectedVpnPublicIPv4)) { $null } else { $ExpectedVpnPublicIPv4 }
    expectedProxyPublicIPv4 = $requestedProxyExpectations
    effectiveExpectedProxyPublicIPv4 = $effectiveExpectedProxyPublicIPv4
    proxyPublicIPv4 = $proxyPublicIPv4
    proxyHttpValidated = $proxyHttpValidated
    stages = [ordered]@{
        Baseline = New-StageSummary -Bundle $baseline -ProcessCount $baselineProcesses.Count
        Ready = New-StageSummary -Bundle $ready -ProcessCount $readyProcesses.Count
        Final = New-StageSummary -Bundle $final -ProcessCount $finalProcesses.Count
    }
}

$acceptance | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $acceptancePath -Encoding utf8
$acceptance | ConvertTo-Json -Depth 8
