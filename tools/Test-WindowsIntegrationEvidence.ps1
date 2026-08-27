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
        [Parameter(Mandatory = $true)][string] $Value
    )

    if ([string]::IsNullOrWhiteSpace($Value)) {
        throw "$Name must contain an IPv4 address."
    }

    $parsed = $null
    if (-not [System.Net.IPAddress]::TryParse($Value, [ref]$parsed) -or
        $parsed.AddressFamily -ne [System.Net.Sockets.AddressFamily]::InterNetwork) {
        throw "$Name must be an IPv4 address, got '$Value'."
    }
}

function Get-SuccessfulProbeOutputOrEmpty {
    param([AllowNull()] $Probe)

    if ($null -ne $Probe -and
        [bool](Get-OptionalPropertyValue -Object $Probe -Name 'succeeded')) {
        $value = Get-OptionalPropertyValue -Object $Probe -Name 'value'
        if ($null -ne $value -and [bool](Get-OptionalPropertyValue -Object $value -Name 'succeeded')) {
            return ([string](Get-OptionalPropertyValue -Object $value -Name 'output')).Trim()
        }
    }

    return ''
}

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

$assertions = @((Get-OptionalPropertyValue -Object $evidence -Name 'assertions') | Where-Object { $null -ne $_ })
$assertionByName = @{}
foreach ($assertion in $assertions) {
    $name = [string](Get-OptionalPropertyValue -Object $assertion -Name 'name')
    if ([string]::IsNullOrWhiteSpace($name)) {
        throw 'Integration evidence contains an assertion without a name.'
    }
    if ($assertionByName.ContainsKey($name)) {
        throw "Integration evidence contains duplicate assertion '$name'."
    }
    $assertionByName[$name] = $assertion
}

$recomputedFailedAssertions = @($assertions | Where-Object {
    -not [bool](Get-OptionalPropertyValue -Object $_ -Name 'passed')
})
$recomputedFailedNames = @($recomputedFailedAssertions | ForEach-Object {
    [string](Get-OptionalPropertyValue -Object $_ -Name 'name')
})
$summaryFailedNames = @((Get-OptionalPropertyValue -Object $summary -Name 'failedAssertions') | ForEach-Object { [string]$_ })

if ([int](Get-OptionalPropertyValue -Object $summary -Name 'assertionCount') -ne $assertions.Count) {
    throw 'Integration summary assertionCount does not match evidence assertions.'
}
if ([int](Get-OptionalPropertyValue -Object $summary -Name 'failedAssertionCount') -ne $recomputedFailedAssertions.Count) {
    throw 'Integration summary failedAssertionCount does not match evidence assertions.'
}
if (($recomputedFailedNames -join "`n") -ne ($summaryFailedNames -join "`n")) {
    throw 'Integration summary failedAssertions does not match evidence assertions.'
}
if ($recomputedFailedAssertions.Count -ne 0) {
    throw "Integration evidence contains failed assertions: $($recomputedFailedNames -join ', ')"
}

$probes = @((Get-OptionalPropertyValue -Object $evidence -Name 'probes') | Where-Object { $null -ne $_ })
$probeByName = @{}
foreach ($probe in $probes) {
    $name = [string](Get-OptionalPropertyValue -Object $probe -Name 'name')
    if ([string]::IsNullOrWhiteSpace($name)) {
        throw 'Integration evidence contains a probe without a name.'
    }
    if ($probeByName.ContainsKey($name)) {
        throw "Integration evidence contains duplicate probe '$name'."
    }
    $probeByName[$name] = $probe
}

$expectations = Get-OptionalPropertyValue -Object $evidence -Name 'expectations'
if ($null -eq $expectations) {
    throw 'Integration evidence expectations metadata is missing.'
}

$directExpected = [string](Get-OptionalPropertyValue -Object $expectations -Name 'directPublicIPv4')
$defaultProxyExpected = [string](Get-OptionalPropertyValue -Object $expectations -Name 'defaultProxyPublicIPv4')
if (-not [string]::IsNullOrWhiteSpace($directExpected)) {
    Assert-IPv4Expectation -Name 'expectations.directPublicIPv4' -Value $directExpected
}
if (-not [string]::IsNullOrWhiteSpace($defaultProxyExpected)) {
    Assert-IPv4Expectation -Name 'expectations.defaultProxyPublicIPv4' -Value $defaultProxyExpected
}

$proxyExpectationMap = @{}
$proxyExpectationRecords = @((Get-OptionalPropertyValue -Object $expectations -Name 'proxyPublicIPv4') | Where-Object { $null -ne $_ })
foreach ($record in $proxyExpectationRecords) {
    $endpoint = [string](Get-OptionalPropertyValue -Object $record -Name 'endpoint')
    $expected = [string](Get-OptionalPropertyValue -Object $record -Name 'publicIPv4')
    if ([string]::IsNullOrWhiteSpace($endpoint)) {
        throw 'Integration evidence contains a per-proxy expectation without an endpoint.'
    }
    if ($proxyExpectationMap.ContainsKey($endpoint)) {
        throw "Integration evidence contains duplicate per-proxy expectation '$endpoint'."
    }
    Assert-IPv4Expectation -Name "expectations.proxyPublicIPv4[$endpoint]" -Value $expected
    $proxyExpectationMap[$endpoint] = $expected

    $probeName = "proxyHttps:$endpoint"
    if (-not $probeByName.ContainsKey($probeName)) {
        throw "Per-proxy expectation '$endpoint' has no matching '$probeName' probe."
    }
}

$directAssertionName = 'expectedDirectPublicIPv4'
if (-not [string]::IsNullOrWhiteSpace($directExpected)) {
    if (-not $probeByName.ContainsKey('directHttps')) {
        throw 'Direct public-IPv4 expectation has no matching directHttps probe.'
    }
    if (-not $assertionByName.ContainsKey($directAssertionName)) {
        throw 'Direct public-IPv4 expectation has no matching assertion.'
    }

    $assertion = $assertionByName[$directAssertionName]
    $assertionExpected = [string](Get-OptionalPropertyValue -Object $assertion -Name 'expected')
    $assertionActual = [string](Get-OptionalPropertyValue -Object $assertion -Name 'actual')
    $probeActual = Get-SuccessfulProbeOutputOrEmpty -Probe $probeByName['directHttps']
    if ($assertionExpected -ne $directExpected -or $assertionActual -ne $probeActual -or
        [bool](Get-OptionalPropertyValue -Object $assertion -Name 'passed') -ne ($probeActual -eq $directExpected)) {
        throw 'Direct public-IPv4 assertion does not match recorded expectation/probe evidence.'
    }
}
elseif ($assertionByName.ContainsKey($directAssertionName)) {
    throw 'Direct public-IPv4 assertion exists without a recorded direct expectation.'
}

$proxyExpectationAssertionNames = @($assertionByName.Keys | Where-Object { $_.StartsWith('expectedProxyPublicIPv4:', [StringComparison]::Ordinal) })
foreach ($assertionName in $proxyExpectationAssertionNames) {
    $endpoint = $assertionName.Substring('expectedProxyPublicIPv4:'.Length)
    if ([string]::IsNullOrWhiteSpace($endpoint)) {
        throw 'Per-proxy public-IPv4 assertion has an empty endpoint.'
    }

    $probeName = "proxyHttps:$endpoint"
    if (-not $probeByName.ContainsKey($probeName)) {
        throw "Per-proxy public-IPv4 assertion '$endpoint' has no matching probe."
    }

    $expected = if ($proxyExpectationMap.ContainsKey($endpoint)) {
        [string]$proxyExpectationMap[$endpoint]
    }
    else {
        $defaultProxyExpected
    }
    if ([string]::IsNullOrWhiteSpace($expected)) {
        throw "Per-proxy public-IPv4 assertion '$endpoint' exists without a recorded expectation."
    }

    $assertion = $assertionByName[$assertionName]
    $assertionExpected = [string](Get-OptionalPropertyValue -Object $assertion -Name 'expected')
    $assertionActual = [string](Get-OptionalPropertyValue -Object $assertion -Name 'actual')
    $probeActual = Get-SuccessfulProbeOutputOrEmpty -Probe $probeByName[$probeName]
    if ($assertionExpected -ne $expected -or $assertionActual -ne $probeActual -or
        [bool](Get-OptionalPropertyValue -Object $assertion -Name 'passed') -ne ($probeActual -eq $expected)) {
        throw "Per-proxy public-IPv4 assertion '$endpoint' does not match recorded expectation/probe evidence."
    }
}

foreach ($probeName in @($probeByName.Keys | Where-Object { $_.StartsWith('proxyHttps:', [StringComparison]::Ordinal) })) {
    $endpoint = $probeName.Substring('proxyHttps:'.Length)
    $expected = if ($proxyExpectationMap.ContainsKey($endpoint)) {
        [string]$proxyExpectationMap[$endpoint]
    }
    else {
        $defaultProxyExpected
    }
    if (-not [string]::IsNullOrWhiteSpace($expected)) {
        $assertionName = "expectedProxyPublicIPv4:$endpoint"
        if (-not $assertionByName.ContainsKey($assertionName)) {
            throw "Proxy '$endpoint' has a recorded public-IPv4 expectation but no matching assertion."
        }
    }
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
    expectationContractValidated = $true
} | ConvertTo-Json -Depth 4
