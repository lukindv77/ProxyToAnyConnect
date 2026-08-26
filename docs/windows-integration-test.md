# Windows 11 integration test

This procedure validates the current GUI/multi-proxy ProxyToAnyConnect runtime on a real Windows 11 x64 machine against real L2TP endpoint(s). It is the manual/E2E acceptance procedure for the parts that GitHub-hosted CI cannot prove.

`docs/requirements.md` remains the product contract. Do not store VPN passwords or PSKs in the repository or test notes.

## Prerequisites

Prepare at least one real L2TP endpoint. For the complete roadmap acceptance, test both:

1. an existing Windows L2TP profile with split tunneling enabled;
2. a custom ephemeral L2TP configuration in ProxyToAnyConnect.

For multi-proxy isolation testing, preferably prepare either two independent L2TP endpoints/profiles or one shared L2TP plus a second independent dedicated L2TP.

Before starting, configure the relevant L2TP verification `publicAddress` as either:

- the expected public egress IPv4; or
- a DNS identity when a fixed expected public IPv4 is not available.

## 1. Record the host baseline

In PowerShell, capture existing Windows VPN profiles and default IPv4 routes:

```powershell
Get-VpnConnection |
    Format-Table Name,TunnelType,SplitTunneling,ConnectionStatus

Get-VpnConnection -AllUserConnection |
    Format-Table Name,TunnelType,SplitTunneling,ConnectionStatus

Get-NetRoute -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' |
    Sort-Object ifIndex,NextHop,RouteMetric |
    Format-Table ifIndex,InterfaceAlias,NextHop,RouteMetric,PolicyStore
```

Record the ordinary non-proxy public IPv4:

```powershell
curl.exe --noproxy "*" https://api.ipify.org
```

Keep these outputs with the structured application logs for the test record.

## 2. Start the GUI and validate one existing-profile L2TP

Launch the self-contained `ProxyToAnyConnect.exe` normally. The application remains a GUI/tray process; there is no separate console verification mode in the current GUI architecture.

Configure or select an existing Windows L2TP profile and one enabled proxy. The profile must be L2TP + split tunnel.

Expected L2TP grid progression:

```text
Disconnected -> Dialing -> Verifying -> Ready
```

When `Ready`, verify the L2TP row shows:

- assigned client IPv4;
- interface index;
- active proxy lease count;
- latest verification/status detail;
- RX/TX counters;
- ping when keepalive is enabled and successful.

The `Status / reason` field should retain the latest successful verification information and later keepalive/reconnect/fail-closed detail without requiring the JSONL log for basic diagnosis.

## 3. Verify HTTPS CONNECT through the proxy

For the configured listener, for example `127.0.0.1:18080`:

```powershell
curl.exe --proxy http://127.0.0.1:18080 https://api.ipify.org
```

If a fixed expected L2TP public IPv4 is configured, the returned address must exactly match it.

This exercises ordinary HTTP `CONNECT`; TLS remains end-to-end between curl and the destination.

Verify the proxy RX/TX and aggregate L2TP RX/TX counters increase.

## 4. Verify plain HTTP through the proxy

Use a controlled HTTP endpoint:

```powershell
curl.exe --proxy http://127.0.0.1:18080 http://<HTTP_TEST_HOST>/
```

The request must succeed only while the selected L2TP has a verified `Ready` context.

## 5. Verify unrelated traffic remains direct

While ProxyToAnyConnect and L2TP are `Ready`:

```powershell
curl.exe --noproxy "*" https://api.ipify.org
```

Expected: the ordinary host/ISP egress, not the L2TP egress unless both addresses are intentionally the same.

Capture default routes again:

```powershell
Get-NetRoute -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' |
    Sort-Object ifIndex,NextHop,RouteMetric |
    Format-Table ifIndex,InterfaceAlias,NextHop,RouteMetric,PolicyStore
```

They must match the pre-dial baseline. A route-set change while `Ready` is itself a fail-closed condition.

## 6. Shared L2TP lease semantics

Configure two enabled proxies with distinct local IPv4:port endpoints referencing the same **shared** L2TP.

Verify:

1. both listeners reach Running using one L2TP RAS session;
2. L2TP active proxy count is `2`;
3. traffic through both proxies uses the same expected L2TP egress;
4. pause proxy A — its listener/sessions stop, proxy B remains Running, L2TP stays `Ready`, lease count becomes `1`;
5. pause proxy B — the last lease is released and the L2TP disconnects;
6. resume either proxy — L2TP performs full `Dialing -> Verifying -> Ready` before traffic succeeds again.

## 7. Dedicated/unrelated group isolation

Configure an independent proxy using a **dedicated** L2TP connection.

While both groups are active, pause/fail the shared group and verify the dedicated proxy/L2TP remains operational. Then perform the inverse test.

No pause, failure or selective settings reload of one independent group should restart an unaffected group.

## 8. Fail-closed active tunnel loss

Establish a long-lived HTTPS/CONNECT session through a test proxy, then force its selected VPN down externally. For an existing profile this can be done with:

```powershell
rasdial "<PROFILE>" /disconnect
```

Expected:

- the exact active `VpnContext` is invalidated;
- dependent active proxy sessions terminate;
- the L2TP grid records the fail-closed reason;
- no socket retries through the ordinary interface;
- unrelated proxy/L2TP groups remain operational;
- while active leases remain, reconnect occurs only after cooldown and then repeats complete verification;
- if the last lease is paused/released, reconnect stops and the L2TP remains disconnected.

With the L2TP endpoint deliberately unavailable, new dependent proxy requests must fail rather than use DIRECT Internet access.

## 9. Keepalive validation

Test both supported active keepalive targets when available.

### `VpnServerInternalIPv4`

Verify the PPP server/internal IPv4 discovered from RAS is present and keepalive succeeds using the L2TP-assigned client IPv4 as source.

### `CustomIPv4`

Configure an explicitly reachable IPv4 target through the L2TP and verify successful RTT updates.

For each mode, force consecutive failures to the configured threshold and verify:

```text
keepalive failures
    -> threshold reached
    -> fail-closed context invalidation
    -> dependent tunnel cancellation
    -> RasHangUp
    -> reconnect cooldown
    -> reconnect + full verification while leases remain
```

The GUI should show current keepalive RTT or failure count/threshold, reconnect/cooldown state and preserve the last fail-closed reason.

## 10. Custom ephemeral L2TP

Before connecting, save the output of both Current User and All User `Get-VpnConnection` commands.

In the GUI create a `CustomEphemeral` L2TP with the real endpoint settings. Test PSK first when available; test certificate mode separately when the environment supports it. Exercise the actual server-supported PPP auth/encryption combinations.

Verify:

- connection reaches `Ready` and proxy HTTP/CONNECT traffic succeeds;
- no new persistent Windows VPN profile appears in Windows Settings or `Get-VpnConnection` output;
- persisted configuration does not contain plaintext password/PSK;
- JSONL logs do not contain password/PSK;
- the private temporary `.pbk` is removed after normal last-lease disconnect;
- it is also removed after fail-closed hangup/reconnect and after explicit application Exit.

After exit, compare Windows VPN profile lists to the pre-test snapshot.

## 11. Pause/Resume and selective reconfigure stress

Repeat a representative sequence many times while watching process memory/handles and unaffected groups:

- Start -> Pause -> Resume;
- shared proxy Pause/Resume while another lease remains;
- last-lease Pause -> disconnect -> Resume -> reconnect;
- edit one proxy setting;
- edit one L2TP setting;
- leave an unrelated proxy/L2TP group unchanged.

Expected:

- replaced listeners/RAS sessions/monitors/temporary resources are released;
- no monotonic retained handle/object growth is evident across cycles;
- unaffected groups remain running during selective reload;
- proxy latency/throughput does not regress as a consequence of memory cleanup logic.

Use the tray `Состояние памяти...` snapshot and structured `process.memory` records for evidence; do not use forced production GC as a test mechanism.

## 12. Final route/profile cleanup check

After explicit application Exit, capture routes and Windows VPN profiles again. Confirm:

- host IPv4 default routes match the original baseline;
- managed RAS sessions are disconnected;
- custom ephemeral phonebook resources are gone;
- no persistent custom VPN profile was created.

## Acceptance checklist

### Existing / fail-closed fundamentals

- [ ] Existing Windows L2TP is established by ProxyToAnyConnect itself.
- [ ] RAS client IPv4 is detected correctly.
- [ ] PPP server/internal IPv4 is detected when supplied by the endpoint.
- [ ] VPN interface index and DNS servers are detected correctly.
- [ ] L2TP reaches `Ready` only after active verification.
- [ ] Fixed public IPv4 equality passes, or fixed-IP-only checks are correctly skipped for DNS `publicAddress`.
- [ ] HTTPS `CONNECT` works through the configured proxy listener.
- [ ] Plain HTTP works through the configured proxy listener.
- [ ] Non-proxy traffic remains on the ordinary host path.
- [ ] Default IPv4 routes remain unchanged.
- [ ] External L2TP loss terminates dependent active tunnels.
- [ ] L2TP unavailable => dependent proxy traffic fails closed.
- [ ] No observed DIRECT fallback from ProxyToAnyConnect.

### Multi-proxy / leases

- [ ] At least two distinct proxy endpoints run concurrently.
- [ ] Two proxies share one shared L2TP RAS session.
- [ ] Pausing one shared proxy preserves the L2TP while another lease remains.
- [ ] Pausing the last shared proxy disconnects that L2TP.
- [ ] Resume reconnects and fully verifies before traffic resumes.
- [ ] Independent dedicated/shared groups remain isolated from unrelated pause/failure/reconfigure.

### Keepalive / reconnect

- [ ] `VpnServerInternalIPv4` keepalive is validated when the PPP server address is available.
- [ ] `CustomIPv4` keepalive is validated.
- [ ] Threshold failure performs fail-closed teardown, cooldown and reconnect while leases remain.
- [ ] No reconnect occurs after the last lease is released.
- [ ] GUI `Status / reason` exposes keepalive/reconnect/fail-closed diagnostics.

### Custom ephemeral

- [ ] Custom ephemeral L2TP connects to the real endpoint.
- [ ] No persistent Windows VPN profile is created.
- [ ] Password/PSK are not plaintext in configuration or logs.
- [ ] Private phonebook resources are removed after disconnect/failure/exit.

### Long-run/resource evidence

- [ ] Repeated Pause/Resume/reconnect/selective-reconfigure cycles do not show monotonic retained resource growth.
- [ ] Memory hardening does not measurably regress proxy latency/jitter/throughput beyond measurement noise.
- [ ] Structured logs and Windows route/profile snapshots are retained as the E2E test record.
