[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Baseline', 'Ready', 'Final')]
    [string] $Stage,

    [string] $OutputDirectory = (Join-Path $PWD 'artifacts/integration-evidence'),

    [string[]] $ProxyEndpoint = @(),

    [string] $HttpsProbeUrl = 'https://api.ipify.org',

    [string] $HttpProbeUrl = '',

    # Backward-compatible default expected egress for every proxy endpoint.
    [string] $ExpectedVpnPublicIPv4 = '',

    # Optional per-endpoint override for heterogeneous shared/dedicated groups.
    # Example: @{ '127.0.0.1:18080'='198.51.100.10'; '127.0.0.1:18081'='203.0.113.20' }
    [hashtable] $ExpectedProxyPublicIPv4 = @{},

    # Optional direct host egress assertion proving non-proxy traffic stayed on the
    # ordinary host path rather than following the L2TP-bound proxy route.
    [string] $ExpectedDirectPublicIPv4 = '',

    [string] $LogDirectory = '',

    [switch] $SkipExternalProbes
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function ConvertTo-StableJson {
    param([Parameter(Mandatory = $true)] $Value)
    return (ConvertTo-Json -InputObject $Value -Depth 8 -Compress)
}

function Get-Sha256Text {
    param([Parameter(Mandatory = $true)][string] $Text)

    $bytes = [System.Text.Encoding]::UTF8.GetBytes($Text)
    $hash = [System.Security.Cryptography.SHA256]::HashData($bytes)
    return [Convert]::ToHexString($hash).ToLowerInvariant()
}

function Write-JsonFile {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)] $Value
    )

    $Value | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $Path -Encoding utf8
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
        [AllowEmptyString()][string] $Value
    )

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return
    }

    $parsed = $null
    if (-not [System.Net.IPAddress]::TryParse($Value, [ref]$parsed) -or
        $parsed.AddressFamily -ne [System.Net.Sockets.AddressFamily]::InterNetwork) {
        throw "$Name must be an IPv4 address, got '$Value'."
    }
}

function Invoke-Capture {
    param(
        [Parameter(Mandatory = $true)][string] $Name,
        [Parameter(Mandatory = $true)][scriptblock] $Action
    )

    try {
        return [ordered]@{
            name = $Name
            succeeded = $true
            value = & $Action
            error = $null
        }
    }
    catch {
        return [ordered]@{
            name = $Name
            succeeded = $false
            value = $null
            error = $_.Exception.Message
        }
    }
}

function Invoke-CurlProbe {
    param(
        [Parameter(Mandatory = $true)][string] $Url,
        [string] $Proxy = ''
    )

    $arguments = @('--silent', '--show-error', '--fail-with-body', '--max-time', '20')
    if ([string]::IsNullOrWhiteSpace($Proxy)) {
        $arguments += @('--noproxy', '*')
    }
    else {
        $arguments += @('--proxy', "http://$Proxy")
    }
    $arguments += $Url

    $output = & curl.exe @arguments 2>&1
    $exitCode = $LASTEXITCODE
    return [ordered]@{
        url = $Url
        proxy = if ([string]::IsNullOrWhiteSpace($Proxy)) { $null } else { $Proxy }
        exitCode = $exitCode
        output = (($output | Out-String).Trim())
        succeeded = ($exitCode -eq 0)
    }
}

function Get-DefaultRouteSnapshot {
    $items = @(
        Get-NetRoute -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' -ErrorAction Stop |
            Sort-Object ifIndex, NextHop, RouteMetric, PolicyStore |
            Select-Object ifIndex, InterfaceAlias, DestinationPrefix, NextHop, RouteMetric, InterfaceMetric, PolicyStore
    )
    return ,$items
}

function Get-VpnProfileSnapshot {
    $items = @()

    try {
        $items += @(
            Get-VpnConnection -ErrorAction Stop |
                Select-Object @{n='Scope';e={'CurrentUser'}}, Name, TunnelType, SplitTunneling, ConnectionStatus
        )
    }
    catch {
        $items += [pscustomobject]@{
            Scope = 'CurrentUserError'
            Name = $_.Exception.Message
            TunnelType = $null
            SplitTunneling = $null
            ConnectionStatus = $null
        }
    }

    try {
        $items += @(
            Get-VpnConnection -AllUserConnection -ErrorAction Stop |
                Select-Object @{n='Scope';e={'AllUsers'}}, Name, TunnelType, SplitTunneling, ConnectionStatus
        )
    }
    catch {
        $items += [pscustomobject]@{
            Scope = 'AllUsersError'
            Name = $_.Exception.Message
            TunnelType = $null
            SplitTunneling = $null
            ConnectionStatus = $null
        }
    }

    $sorted = @($items | Sort-Object Scope, Name)
    return ,$sorted
}

function Get-InterfaceSnapshot {
    # Get-NetIPConfiguration aggregates several CIM associations into one object.
    # On hosts with ambiguous/multiple adapter records that aggregation can fail
    # before the caller receives any data. Query the component views separately
    # and build our own immutable evidence DTO keyed by interface index instead.
    $interfaces = @(
        Get-NetIPInterface -AddressFamily IPv4 -ErrorAction Stop |
            Sort-Object InterfaceIndex, InterfaceAlias
    )
    $adapters = @(Get-NetAdapter -IncludeHidden -ErrorAction Stop)
    $profiles = @(Get-NetConnectionProfile -ErrorAction Stop)
    $addresses = @(Get-NetIPAddress -AddressFamily IPv4 -ErrorAction Stop)
    $gateways = @(
        Get-NetRoute -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' -ErrorAction Stop
    )
    $dnsRecords = @(Get-DnsClientServerAddress -AddressFamily IPv4 -ErrorAction Stop)

    $items = @(
        foreach ($interface in $interfaces) {
            $interfaceIndex = Get-OptionalPropertyValue -Object $interface -Name 'InterfaceIndex'

            $matchingAdapters = @(
                $adapters |
                    Where-Object {
                        $candidateIndex = Get-OptionalPropertyValue -Object $_ -Name 'InterfaceIndex'
                        if ($null -eq $candidateIndex) {
                            $candidateIndex = Get-OptionalPropertyValue -Object $_ -Name 'ifIndex'
                        }
                        $candidateIndex -eq $interfaceIndex
                    } |
                    Sort-Object Name, InterfaceDescription
            )
            $adapter = $matchingAdapters | Select-Object -First 1

            $matchingProfiles = @(
                $profiles |
                    Where-Object {
                        (Get-OptionalPropertyValue -Object $_ -Name 'InterfaceIndex') -eq $interfaceIndex
                    } |
                    Sort-Object Name
            )
            $profile = $matchingProfiles | Select-Object -First 1

            $ipv4Addresses = @(
                $addresses |
                    Where-Object {
                        (Get-OptionalPropertyValue -Object $_ -Name 'InterfaceIndex') -eq $interfaceIndex
                    } |
                    ForEach-Object { Get-OptionalPropertyValue -Object $_ -Name 'IPAddress' } |
                    Where-Object { $null -ne $_ } |
                    Sort-Object -Unique
            )

            $defaultGateways = @(
                $gateways |
                    Where-Object {
                        $candidateIndex = Get-OptionalPropertyValue -Object $_ -Name 'InterfaceIndex'
                        if ($null -eq $candidateIndex) {
                            $candidateIndex = Get-OptionalPropertyValue -Object $_ -Name 'ifIndex'
                        }
                        $candidateIndex -eq $interfaceIndex
                    } |
                    Sort-Object RouteMetric, NextHop |
                    ForEach-Object { Get-OptionalPropertyValue -Object $_ -Name 'NextHop' } |
                    Where-Object { $null -ne $_ } |
                    Sort-Object -Unique
            )

            $dnsServers = @(
                $dnsRecords |
                    Where-Object {
                        (Get-OptionalPropertyValue -Object $_ -Name 'InterfaceIndex') -eq $interfaceIndex
                    } |
                    ForEach-Object {
                        @(Get-OptionalPropertyValue -Object $_ -Name 'ServerAddresses')
                    } |
                    Where-Object { $null -ne $_ } |
                    Sort-Object -Unique
            )

            [pscustomobject]@{
                InterfaceAlias = Get-OptionalPropertyValue -Object $interface -Name 'InterfaceAlias'
                InterfaceIndex = $interfaceIndex
                InterfaceDescription = Get-OptionalPropertyValue -Object $adapter -Name 'InterfaceDescription'
                NetProfileName = Get-OptionalPropertyValue -Object $profile -Name 'Name'
                IPv4Address = $ipv4Addresses
                IPv4DefaultGateway = $defaultGateways
                DnsServers = $dnsServers
            }
        }
    )
    return ,$items
}

function Copy-RecentJsonlLogs {
    param(
        [Parameter(Mandatory = $true)][string] $Source,
        [Parameter(Mandatory = $true)][string] $Destination
    )

    if (-not (Test-Path -LiteralPath $Source -PathType Container)) {
        return [ordered]@{ copied = 0; error = "Log directory does not exist: $Source" }
    }

    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    $files = @(
        Get-ChildItem -LiteralPath $Source -Recurse -File -Filter '*.jsonl' -ErrorAction Stop |
            Sort-Object LastWriteTimeUtc -Descending |
            Select-Object -First 20
    )

    foreach ($file in $files) {
        $relative = [IO.Path]::GetRelativePath($Source, $file.FullName)
        $target = Join-Path $Destination $relative
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $target -Force
    }

    return [ordered]@{
        copied = $files.Count
        files = @($files | ForEach-Object {
            [ordered]@{
                relativePath = [IO.Path]::GetRelativePath($Source, $_.FullName)
                length = $_.Length
                lastWriteTimeUtc = $_.LastWriteTimeUtc
            }
        })
        error = $null
    }
}

Assert-IPv4Expectation -Name 'ExpectedVpnPublicIPv4' -Value $ExpectedVpnPublicIPv4
Assert-IPv4Expectation -Name 'ExpectedDirectPublicIPv4' -Value $ExpectedDirectPublicIPv4

$proxyExpectations = @(
    foreach ($key in @($ExpectedProxyPublicIPv4.Keys | Sort-Object)) {
        $endpoint = [string]$key
        $expected = [string]$ExpectedProxyPublicIPv4[$key]
        if ($ProxyEndpoint -notcontains $endpoint) {
            throw "ExpectedProxyPublicIPv4 endpoint '$endpoint' is not present in -ProxyEndpoint."
        }
        Assert-IPv4Expectation -Name "ExpectedProxyPublicIPv4[$endpoint]" -Value $expected
        [ordered]@{
            endpoint = $endpoint
            publicIPv4 = $expected
        }
    }
)

$requiresExternalExpectation =
    -not [string]::IsNullOrWhiteSpace($ExpectedVpnPublicIPv4) -or
    -not [string]::IsNullOrWhiteSpace($ExpectedDirectPublicIPv4) -or
    $proxyExpectations.Count -gt 0
if ($SkipExternalProbes -and $requiresExternalExpectation) {
    throw 'External egress expectations cannot be used together with -SkipExternalProbes.'
}

$stageName = $Stage.ToLowerInvariant()
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$stageDirectory = Join-Path $OutputDirectory $stageName
New-Item -ItemType Directory -Force -Path $stageDirectory | Out-Null

$routesCapture = Invoke-Capture 'defaultRoutes' { Get-DefaultRouteSnapshot }
$vpnCapture = Invoke-Capture 'vpnProfiles' { Get-VpnProfileSnapshot }
$interfacesCapture = Invoke-Capture 'interfaces' { Get-InterfaceSnapshot }
$processCapture = Invoke-Capture 'process' {
    $items = @(
        Get-Process -Name 'ProxyToAnyConnect' -ErrorAction SilentlyContinue |
            ForEach-Object {
                $executableSha256 = $null
                $executableHashError = $null
                try {
                    $processPath = Get-OptionalPropertyValue -Object $_ -Name 'Path'
                    if ([string]::IsNullOrWhiteSpace([string]$processPath) -or
                        -not (Test-Path -LiteralPath $processPath -PathType Leaf)) {
                        $executableHashError = 'ExecutablePathUnavailable'
                    }
                    else {
                        $executableSha256 = (Get-FileHash -LiteralPath $processPath -Algorithm SHA256 -ErrorAction Stop).Hash.ToLowerInvariant()
                    }
                }
                catch {
                    # Do not persist the exception message: filesystem errors can
                    # contain the user's local executable path. The type is enough
                    # to diagnose why binary identity could not be captured.
                    $executableHashError = $_.Exception.GetType().Name
                }

                [pscustomobject]@{
                    Id = $_.Id
                    ProcessName = $_.ProcessName
                    StartTime = $_.StartTime
                    WorkingSet64 = $_.WorkingSet64
                    PrivateMemorySize64 = $_.PrivateMemorySize64
                    HandleCount = $_.HandleCount
                    ThreadCount = $_.Threads.Count
                    ExecutableSha256 = $executableSha256
                    ExecutableHashError = $executableHashError
                }
            }
    )
    return ,$items
}

$routeFingerprint = $null
if ($routesCapture.succeeded) {
    $routeFingerprint = Get-Sha256Text (ConvertTo-StableJson $routesCapture.value)
    Set-Content -LiteralPath (Join-Path $stageDirectory 'default-routes.sha256') -Value $routeFingerprint -Encoding ascii
}

$profileFingerprint = $null
if ($vpnCapture.succeeded) {
    $profileFingerprint = Get-Sha256Text (ConvertTo-StableJson $vpnCapture.value)
    Set-Content -LiteralPath (Join-Path $stageDirectory 'vpn-profiles.sha256') -Value $profileFingerprint -Encoding ascii
}

$probes = @()
if (-not $SkipExternalProbes) {
    $probes += Invoke-Capture 'directHttps' { Invoke-CurlProbe -Url $HttpsProbeUrl }
    foreach ($endpoint in $ProxyEndpoint) {
        $probes += Invoke-Capture "proxyHttps:$endpoint" { Invoke-CurlProbe -Url $HttpsProbeUrl -Proxy $endpoint }
        if (-not [string]::IsNullOrWhiteSpace($HttpProbeUrl)) {
            $probes += Invoke-Capture "proxyHttp:$endpoint" { Invoke-CurlProbe -Url $HttpProbeUrl -Proxy $endpoint }
        }
    }
}

$assertions = @()
if (-not [string]::IsNullOrWhiteSpace($ExpectedDirectPublicIPv4) -and -not $SkipExternalProbes) {
    $probe = $probes | Where-Object name -eq 'directHttps' | Select-Object -First 1
    $actual = if ($null -ne $probe -and $probe.succeeded -and $probe.value.succeeded) {
        [string]$probe.value.output
    }
    else {
        ''
    }
    $assertions += [ordered]@{
        name = 'expectedDirectPublicIPv4'
        expected = $ExpectedDirectPublicIPv4
        actual = $actual
        passed = ($actual -eq $ExpectedDirectPublicIPv4)
    }
}

if (-not $SkipExternalProbes) {
    foreach ($endpoint in $ProxyEndpoint) {
        $override = $proxyExpectations | Where-Object endpoint -eq $endpoint | Select-Object -First 1
        $expected = if ($null -ne $override) {
            [string]$override.publicIPv4
        }
        else {
            $ExpectedVpnPublicIPv4
        }

        if ([string]::IsNullOrWhiteSpace($expected)) {
            continue
        }

        $probe = $probes | Where-Object name -eq "proxyHttps:$endpoint" | Select-Object -First 1
        $actual = if ($null -ne $probe -and $probe.succeeded -and $probe.value.succeeded) {
            [string]$probe.value.output
        }
        else {
            ''
        }
        $assertions += [ordered]@{
            name = "expectedProxyPublicIPv4:$endpoint"
            expected = $expected
            actual = $actual
            passed = ($actual -eq $expected)
        }
    }
}

if ($Stage -ne 'Baseline') {
    $baselineRouteFile = Join-Path (Join-Path $OutputDirectory 'baseline') 'default-routes.sha256'
    if (Test-Path -LiteralPath $baselineRouteFile) {
        $baselineRouteFingerprint = (Get-Content -LiteralPath $baselineRouteFile -Raw).Trim()
        $assertions += [ordered]@{
            name = 'defaultRoutesMatchBaseline'
            expected = $baselineRouteFingerprint
            actual = $routeFingerprint
            passed = ($null -ne $routeFingerprint -and $routeFingerprint -eq $baselineRouteFingerprint)
        }
    }

    $baselineProfileFile = Join-Path (Join-Path $OutputDirectory 'baseline') 'vpn-profiles.sha256'
    if ($Stage -eq 'Final' -and (Test-Path -LiteralPath $baselineProfileFile)) {
        $baselineProfileFingerprint = (Get-Content -LiteralPath $baselineProfileFile -Raw).Trim()
        $assertions += [ordered]@{
            name = 'vpnProfilesMatchBaseline'
            expected = $baselineProfileFingerprint
            actual = $profileFingerprint
            passed = ($null -ne $profileFingerprint -and $profileFingerprint -eq $baselineProfileFingerprint)
        }
    }
}

$logCapture = $null
if (-not [string]::IsNullOrWhiteSpace($LogDirectory)) {
    $logCapture = Copy-RecentJsonlLogs -Source $LogDirectory -Destination (Join-Path $stageDirectory 'logs')
}

$record = [ordered]@{
    schemaVersion = 1
    stage = $Stage
    capturedAtUtc = [DateTimeOffset]::UtcNow
    machine = [ordered]@{
        computerName = $env:COMPUTERNAME
        osVersion = [Environment]::OSVersion.VersionString
        is64BitOperatingSystem = [Environment]::Is64BitOperatingSystem
        powershellVersion = $PSVersionTable.PSVersion.ToString()
    }
    expectations = [ordered]@{
        directPublicIPv4 = if ([string]::IsNullOrWhiteSpace($ExpectedDirectPublicIPv4)) { $null } else { $ExpectedDirectPublicIPv4 }
        defaultProxyPublicIPv4 = if ([string]::IsNullOrWhiteSpace($ExpectedVpnPublicIPv4)) { $null } else { $ExpectedVpnPublicIPv4 }
        proxyPublicIPv4 = $proxyExpectations
    }
    routes = $routesCapture
    routeFingerprint = $routeFingerprint
    vpnProfiles = $vpnCapture
    profileFingerprint = $profileFingerprint
    interfaces = $interfacesCapture
    process = $processCapture
    probes = $probes
    assertions = $assertions
    logCapture = $logCapture
}

Write-JsonFile -Path (Join-Path $stageDirectory 'evidence.json') -Value $record

$failedAssertions = @($assertions | Where-Object { -not $_.passed })
$summary = [ordered]@{
    stage = $Stage
    evidencePath = (Join-Path $stageDirectory 'evidence.json')
    assertionCount = $assertions.Count
    failedAssertionCount = $failedAssertions.Count
    failedAssertions = @($failedAssertions | ForEach-Object name)
}
Write-JsonFile -Path (Join-Path $stageDirectory 'summary.json') -Value $summary

$summary | ConvertTo-Json -Depth 4
if ($failedAssertions.Count -gt 0) {
    exit 2
}
