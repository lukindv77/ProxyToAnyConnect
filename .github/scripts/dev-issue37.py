from pathlib import Path
import subprocess

path = Path('tools/Invoke-WindowsIntegrationEvidence.ps1')
expected_sha = '62bd7066a8cf8e39a52187de28ae65c75fda87e9'
actual_sha = subprocess.check_output(
    ['git', 'rev-parse', 'HEAD:tools/Invoke-WindowsIntegrationEvidence.ps1'],
    text=True,
).strip()
if actual_sha != expected_sha:
    raise SystemExit(f'unexpected Invoke-WindowsIntegrationEvidence.ps1 input blob: {actual_sha}')


def replace_block(data: bytes, old_lf: bytes, new_lf: bytes, label: str) -> bytes:
    matches = []
    for newline in (b'\r\n', b'\n'):
        old = old_lf.replace(b'\n', newline)
        count = data.count(old)
        if count:
            matches.append((newline, old, count))
    if len(matches) != 1 or matches[0][2] != 1:
        detail = ', '.join(f'{nl!r}:{count}' for nl, _, count in matches) or 'none'
        raise SystemExit(f'expected one {label}, matches={detail}')
    newline, old, _ = matches[0]
    return data.replace(old, new_lf.replace(b'\n', newline))


data = path.read_bytes()
old = b'''function Get-InterfaceSnapshot {
    $items = @(
        Get-NetIPConfiguration -ErrorAction Stop |
            ForEach-Object {
                $netProfile = Get-OptionalPropertyValue -Object $_ -Name 'NetProfile'
                $ipv4Addresses = @(Get-OptionalPropertyValue -Object $_ -Name 'IPv4Address')
                $gateways = @(Get-OptionalPropertyValue -Object $_ -Name 'IPv4DefaultGateway')
                $dnsServer = Get-OptionalPropertyValue -Object $_ -Name 'DNSServer'
                $dnsAddresses = @(Get-OptionalPropertyValue -Object $dnsServer -Name 'ServerAddresses')

                [pscustomobject]@{
                    InterfaceAlias = Get-OptionalPropertyValue -Object $_ -Name 'InterfaceAlias'
                    InterfaceIndex = Get-OptionalPropertyValue -Object $_ -Name 'InterfaceIndex'
                    InterfaceDescription = Get-OptionalPropertyValue -Object $_ -Name 'InterfaceDescription'
                    NetProfileName = Get-OptionalPropertyValue -Object $netProfile -Name 'Name'
                    IPv4Address = @($ipv4Addresses | ForEach-Object {
                        Get-OptionalPropertyValue -Object $_ -Name 'IPAddress'
                    } | Where-Object { $null -ne $_ })
                    IPv4DefaultGateway = @($gateways | ForEach-Object {
                        Get-OptionalPropertyValue -Object $_ -Name 'NextHop'
                    } | Where-Object { $null -ne $_ })
                    DnsServers = @($dnsAddresses | Where-Object { $null -ne $_ })
                }
            } |
            Sort-Object InterfaceIndex
    )
    return ,$items
}
'''
new = b'''function Get-InterfaceSnapshot {
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
'''
path.write_bytes(replace_block(data, old, new, 'Get-InterfaceSnapshot function'))
