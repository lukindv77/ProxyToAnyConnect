[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Baseline', 'Ready', 'Final')]
    [string] $Stage,

    [string] $OutputDirectory = (Join-Path $PWD 'artifacts/integration-evidence'),

    [string[]] $ProxyEndpoint = @(),

    [string] $HttpsProbeUrl = 'https://api.ipify.org',

    [string] $HttpProbeUrl = '',

    [string] $ExpectedVpnPublicIPv4 = '',

    [string] $LogDirectory = '',

    [switch] $SkipExternalProbes
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function ConvertTo-StableJson {
    param([Parameter(Mandatory = $true)] $Value)
    return ($Value | ConvertTo-Json -Depth 8 -Compress)
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
    return @(
        Get-NetRoute -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' -ErrorAction Stop |
            Sort-Object ifIndex, NextHop, RouteMetric, PolicyStore |
            Select-Object ifIndex, InterfaceAlias, DestinationPrefix, NextHop, RouteMetric, InterfaceMetric, PolicyStore
    )
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

    return @($items | Sort-Object Scope, Name)
}

function Get-InterfaceSnapshot {
    return @(
        Get-NetIPConfiguration -ErrorAction Stop |
            ForEach-Object {
                [pscustomobject]@{
                    InterfaceAlias = $_.InterfaceAlias
                    InterfaceIndex = $_.InterfaceIndex
                    InterfaceDescription = $_.InterfaceDescription
                    NetProfileName = $_.NetProfile.Name
                    IPv4Address = @($_.IPv4Address | ForEach-Object { $_.IPAddress })
                    IPv4DefaultGateway = @($_.IPv4DefaultGateway | ForEach-Object { $_.NextHop })
                    DnsServers = @($_.DNSServer.ServerAddresses)
                }
            } |
            Sort-Object InterfaceIndex
    )
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

$stageName = $Stage.ToLowerInvariant()
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$stageDirectory = Join-Path $OutputDirectory $stageName
New-Item -ItemType Directory -Force -Path $stageDirectory | Out-Null

$routesCapture = Invoke-Capture 'defaultRoutes' { Get-DefaultRouteSnapshot }
$vpnCapture = Invoke-Capture 'vpnProfiles' { Get-VpnProfileSnapshot }
$interfacesCapture = Invoke-Capture 'interfaces' { Get-InterfaceSnapshot }
$processCapture = Invoke-Capture 'process' {
    @(
        Get-Process -Name 'ProxyToAnyConnect' -ErrorAction SilentlyContinue |
            Select-Object Id, ProcessName, StartTime, WorkingSet64, PrivateMemorySize64, HandleCount,
                @{n='ThreadCount';e={$_.Threads.Count}}
    )
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
if (-not [string]::IsNullOrWhiteSpace($ExpectedVpnPublicIPv4) -and -not $SkipExternalProbes) {
    foreach ($endpoint in $ProxyEndpoint) {
        $probe = $probes | Where-Object name -eq "proxyHttps:$endpoint" | Select-Object -First 1
        $actual = if ($null -ne $probe -and $probe.succeeded -and $probe.value.succeeded) {
            [string]$probe.value.output
        }
        else {
            ''
        }
        $assertions += [ordered]@{
            name = "expectedVpnPublicIPv4:$endpoint"
            expected = $ExpectedVpnPublicIPv4
            actual = $actual
            passed = ($actual -eq $ExpectedVpnPublicIPv4)
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
