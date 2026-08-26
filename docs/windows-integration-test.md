# Windows 11 integration test

This procedure validates ProxyToAnyConnect against a real Windows 11 x64 machine and a real L2TP endpoint.

## Prerequisites

- A Windows VPN profile already exists for the target L2TP connection.
- The profile stores the credentials required by Windows RAS.
- `TunnelType` is `L2tp`.
- `SplitTunneling` is enabled.
- `src/ProxyToAnyConnect/appsettings.json` (or a copied runtime configuration) contains:
  - the real VPN profile name in `l2tp.entryName`;
  - the expected public L2TP egress IPv4 or DNS identity in `l2tp.verification.publicAddress`.

Do not store VPN passwords or pre-shared keys in the repository.

## 1. Record the baseline

In PowerShell:

```powershell
Get-VpnConnection -Name '<PROFILE>' |
    Format-List Name,TunnelType,SplitTunneling,ConnectionStatus

Get-NetRoute -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' |
    Sort-Object ifIndex,NextHop,RouteMetric |
    Format-Table ifIndex,InterfaceAlias,NextHop,RouteMetric,PolicyStore
```

Save the output for comparison.

Optionally record the ordinary non-proxy public IPv4:

```powershell
curl.exe --noproxy "*" https://api.ipify.org
```

This address is expected to remain the ordinary host egress while ProxyToAnyConnect is running.

## 2. Run verification only

From the self-contained publish directory:

```powershell
.\ProxyToAnyConnect.exe .\appsettings.json --verify-only
```

Expected result:

```text
L2TP READY: <profile>
  IPv4: <RAS assigned IPv4>
  Interface: <VPN interface> (index <n>)
  DNS: <VPN DNS servers>
  Verification target IPv4: <probe endpoint IPv4>
  ... verification PASSED or fixed-IP comparison SKIPPED for DNS publicAddress
Verification-only mode completed successfully. Proxy listener was not started.
```

The process exits with code `0` only after the full `Dialing -> Verifying -> Ready` sequence succeeds.

If `publicAddress` is a fixed IPv4, the observed public IPv4 must exactly match it.

If `publicAddress` is a DNS name, fixed-IP equality checks are intentionally skipped; the L2TP source binding, `IP_UNICAST_IF`, default-route guards and real HTTPS probe still run.

After the process exits, confirm that the test RAS connection was disconnected and that the default-route table still matches the baseline.

## 3. Start the proxy

```powershell
.\ProxyToAnyConnect.exe .\appsettings.json
```

Expected state:

```text
L2TP READY: ...
Proxy listening on 127.0.0.1:18080
```

## 4. Verify HTTPS through the proxy

In a second terminal:

```powershell
curl.exe --proxy http://127.0.0.1:18080 https://api.ipify.org
```

For a fixed L2TP public IPv4, the returned address must be exactly the configured `publicAddress`.

This request exercises standard HTTP `CONNECT`; TLS remains end-to-end between curl and the destination.

## 5. Verify plain HTTP through the proxy

Use any controlled HTTP endpoint, for example an internal test endpoint or a public endpoint that still supports HTTP:

```powershell
curl.exe --proxy http://127.0.0.1:18080 http://<HTTP_TEST_HOST>/
```

The request must succeed only while the verified L2TP context is `Ready`.

## 6. Verify unrelated traffic remains direct

While ProxyToAnyConnect is still running:

```powershell
curl.exe --noproxy "*" https://api.ipify.org
```

Expected result: the ordinary host/public ISP address, not the L2TP egress address (unless both happen to be the same by network design).

Capture the default routes again:

```powershell
Get-NetRoute -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' |
    Sort-Object ifIndex,NextHop,RouteMetric |
    Format-Table ifIndex,InterfaceAlias,NextHop,RouteMetric,PolicyStore
```

They must match the pre-dial baseline. ProxyToAnyConnect also enforces this invariant continuously while `Ready`.

## 7. Fail-closed test: disconnect L2TP

While the proxy is running, disconnect the VPN externally:

```powershell
rasdial "<PROFILE>" /disconnect
```

Expected behavior:

- the active `VpnContext` is cancelled;
- existing proxy tunnels terminate;
- the proxy never retries those sockets through the ordinary interface;
- a later new proxy request may initiate a fresh `RasDial`, but it must pass all verification guards again before traffic is allowed.

If the L2TP endpoint is intentionally made unavailable, a new proxy request must fail rather than use DIRECT Internet access.

## 8. Continuous route-guard test

While the VPN is `Ready`, any change in the host IPv4 default-route set must invalidate the active VPN context on the next `routeMonitorIntervalMilliseconds` check.

For safety, do not alter production routing merely to test this. Perform this test only in a disposable/test environment where route changes can be reverted immediately.

Expected ProxyToAnyConnect behavior after detecting a mismatch:

```text
L2TP fail-closed monitor rejected the active connection: ...
```

The application cancels active proxy sessions and calls `RasHangUp` for the current RAS handle.

## Acceptance checklist

- [ ] L2TP is established by ProxyToAnyConnect itself.
- [ ] RAS-assigned IPv4 is detected correctly.
- [ ] VPN interface index and DNS servers are detected correctly.
- [ ] `--verify-only` reaches `Ready` and exits successfully.
- [ ] Fixed public IPv4 comparison passes, or is correctly skipped for DNS `publicAddress`.
- [ ] HTTPS `CONNECT` works through `127.0.0.1:18080`.
- [ ] Plain HTTP works through `127.0.0.1:18080`.
- [ ] Non-proxy traffic remains on the ordinary host route.
- [ ] Default routes remain unchanged after `RasDial`.
- [ ] External VPN disconnect terminates active proxy tunnels.
- [ ] L2TP unavailable => new proxy traffic fails closed.
- [ ] No observed DIRECT fallback from ProxyToAnyConnect.
