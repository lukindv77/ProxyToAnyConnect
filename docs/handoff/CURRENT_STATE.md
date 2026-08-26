# ProxyToAnyConnect — project handoff state

> This document is a handoff snapshot for moving development to a new ChatGPT conversation. **GitHub `main` remains the authoritative source for code.** At the start of a new conversation, read this file, `NEW_CHAT_PROMPT.md`, `docs/requirements.md`, `docs/architecture.md`, `docs/memory-stability.md`, then inspect the latest `main`, open issues and GitHub Actions status before making changes.

## Snapshot baseline

- Repository: `lukindv77/ProxyToAnyConnect` (private)
- Default branch: `main`
- Platform: Windows 11 x64
- Language/runtime: C# / .NET 10, `net10.0-windows`
- GUI: WinForms + system tray
- Last code-level baseline verified before the final handoff documentation commits: commit `5c3955fce4896c0a02b78c021eaccd8078ada8f4` (`fix: own RAS monitor lifetime per VPN session`), GitHub Actions run #181: Build, Self-tests, self-contained win-x64 Publish, ZIP and artifact upload all succeeded.
- The handoff documentation commits after that baseline are documentation/workflow packaging changes; always verify their own latest CI run before continuing.

## Product purpose

ProxyToAnyConnect is a Windows GUI application that exposes one or more local HTTP/HTTPS forward proxy listeners. Every proxy routes exclusively through a selected L2TP connection managed by the application. Protected domains are selected externally (for example by Chrome PAC). ProxyToAnyConnect itself has no DIRECT fallback path.

The application must not alter the normal Internet route used by unrelated applications on the PC.

## Non-negotiable architectural invariants

1. **Fail closed.** If the selected L2TP is unavailable, unverified or lost, dependent proxy traffic fails; it never falls back to the ordinary Internet path.
2. Every outbound proxy TCP socket is bound both to the dynamically assigned L2TP source IPv4 and to the L2TP interface with `IP_UNICAST_IF`.
3. Proxied DNS is performed through L2TP-bound sockets; normal system DNS is not used for proxy destinations.
4. L2TP must not replace/change the host default route. Existing Windows profiles are checked for L2TP + split tunneling before `RasDial`; default IPv4 routes are compared before/after dial and continuously monitored.
5. A new VPN is not exposed to proxy traffic until `Disconnected -> Dialing -> Verifying -> Ready` completes.
6. Verification performs an L2TP-bound HTTPS probe. If the configured expected public address is an IPv4 literal, the observed public IPv4 must match it. If a DNS name is configured instead, only checks inherently requiring a fixed expected IPv4 are skipped.
7. HTTPS proxying is ordinary HTTP `CONNECT`; there is no TLS MITM/decryption.
8. Proxy byte-transfer latency and sustained throughput are first-class requirements. Memory/resource hardening must not increase processing/forwarding latency, latency jitter or reduce throughput beyond measurement noise.
9. Total process memory/resource use must remain bounded during long operation. No unbounded in-memory histories/caches/task registries; no forced production GC.

## GUI lifecycle

`ProxyToAnyConnect.exe` always runs as a GUI application.

- Closing the main form with `X` hides/minimizes it to the system tray.
- Minimizing may hide it to tray.
- The process exits only through an explicit **Exit** command in the application menu or tray context menu.
- Runtime configuration can be edited without terminating the GUI process.

## Multi-proxy runtime model

The configuration contains independent `Proxies[]` and `VpnConnections[]` entities.

Each proxy has at least:

- stable ID/name;
- bind IPv4 and TCP port;
- `maxConcurrentConnections` (default 512, configurable);
- client header/read timeout;
- outbound connect timeout;
- DNS timeout;
- reference to one L2TP connection;
- enabled/runtime Running/Paused/Error state;
- RX/TX byte counters.

Each proxy can be paused/resumed independently. Pause cancels its active sessions, closes its listener and releases its VPN lease.

L2TP connections can be:

- **shared**: many active proxies may reuse one verified L2TP runtime;
- **dedicated**: only one proxy may use it.

A running proxy owns a lease on the selected VPN. First lease establishes/verifies the L2TP; the last released lease disconnects it. If a paused proxy was the last active user of its L2TP, that L2TP is hung up.

## L2TP modes

### Existing Windows profile

- GUI enumerates available Windows L2TP profiles (Current User and All Users).
- Inspector exposes split tunneling and scope.
- Full-tunnel/non-L2TP profiles are rejected before `RasDial`.
- All-user profiles use the global phonebook where required.

### Custom ephemeral L2TP

Implemented architecture uses a private temporary RAS phonebook under `%TEMP%`, not a persistent Windows VPN profile.

Configuration/UI includes server address, username/password/domain or current Windows credentials, IPsec PSK or certificate mode, PPP authentication/encryption options and relevant timeouts.

- Password/PSK are protected with Windows user-bound DPAPI; plaintext secrets are not stored in JSON.
- The private `.pbk` and RAS entry exist only for the session/runtime and are removed after disconnect.
- Windows CI has a native smoke test that creates a private L2TP phonebook, sets PSK credentials and removes it successfully.
- Custom ephemeral mode has been wired into the common `RasConnectionManager` dial/verify path. **A real external custom L2TP endpoint still needs end-to-end Windows 11 validation.**

## Keepalive

Each L2TP can use:

- `Off`;
- `VpnServerInternalIPv4` — PPP server IPv4 from the RAS projection;
- `CustomIPv4` — user-specified IPv4.

Settings: interval, probe timeout and consecutive failure threshold.

ICMP keepalive uses a Windows API that binds the source address to the assigned L2TP IPv4. Successful RTT samples feed a rolling 5-minute average. Failed probes are not inserted as synthetic RTT values. Reaching the failure threshold invalidates the context, cancels dependent tunnels, hangs up RAS and triggers cooldown/reconnect while active leases exist.

## DNS

`L2tpDnsResolver` is fail-closed and L2TP-bound.

- IPv4 A queries.
- UDP first.
- `TC=1` or transport truncation -> TCP DNS fallback, also bound to L2TP.
- CNAME following with loop protection.
- A bounded L2TP-scoped DNS cache is shared by all proxies using the same shared L2TP.
- Cache capacity is fixed (currently 512 names), obeys DNS TTL and is reset when the `VpnContext` changes.
- No unbounded DNS cache and no system DNS fallback for proxied destinations.

## Proxy data path

- Plain HTTP forward proxy and HTTPS `CONNECT` are implemented.
- Hop-by-hop/proxy-only headers are stripped for origin HTTP forwarding.
- Transfer buffers use `ArrayPool<byte>`.
- Header acquisition avoids `MemoryStream` and redundant full-header copies.
- No full request/response/tunnel buffering.
- Traffic counters are low-cost atomic counters.
- `maxConcurrentConnections` bounds user-space live sessions per proxy; when full, acceptance stops and Windows TCP backlog provides backpressure instead of unbounded Task/object creation.
- Shutdown drains all already accepted proxy sessions before the proxy runtime returns and before its VPN lease is released. The drain reuses the bounded session semaphore rather than maintaining a separate Task registry.

## Long-run memory/resource hardening already implemented

See `docs/memory-stability.md` for the normative rules.

Implemented/audited points include:

- deterministic `VpnContext` ref-count ownership so its CTS is disposed only after the manager and last live outbound session release it;
- regression creating/releasing thousands of contexts and checking collectability;
- bounded DNS cache;
- bounded L2TP latest-status registry (one current status per configured VPN, maximum 256 entries) with stale entry removal on runtime disposal;
- L2TP maintenance task exists only while active proxy leases exist;
- GUI tables update existing rows in place rather than clearing/recreating all rows every second;
- process memory-health monitor keeps only the latest immutable snapshot and logs periodic scalar snapshots to disk;
- tracked/joined proxy runtime observer (no forgotten fire-and-forget observer at Pause/reconfigure/Exit);
- semaphore-disposal race hardening in runtime/lease lifecycle;
- RAS monitor lifetime is now owned per VPN session with a dedicated cancellation source/task; disconnect joins the old monitor before session resources are released/replaced;
- stale old RAS monitor cannot hang up a newer RAS handle;
- proxy lifecycle/start-stop stress tests and proxy shutdown drain tests.

Production code does not call forced `GC.Collect()`; forced GC is permitted only inside leak/collectability self-tests.

## Logging and diagnostics

Structured operational log is append-only JSONL.

Configurable:

- log root directory; default application directory;
- retention days; default 30 days.

Layout:

```text
<log-root>/
  YYYY-MM/
    YYYY-MM-DD.jsonl
```

Each record is appended by opening the daily file in append mode, writing one line, flushing/closing. Existing log content is never read/rebuilt to append a new record. Daily retention removes expired files and empty month directories without affecting fail-closed routing.

Secrets, HTTP bodies and HTTPS tunnel contents are never logged.

Runtime metrics:

- proxy RX/TX;
- aggregate L2TP RX/TX from associated proxy traffic;
- rolling 5-minute keepalive RTT average;
- process memory snapshot: managed heap, total allocated bytes, working set, private bytes, GC counts, handles and threads.

## Tests/CI already present

Windows GitHub Actions uses .NET 10 and currently covers, among other things:

- solution build with warnings as errors;
- split/full-tunnel profile rules;
- native IPv4 route-table inspection;
- public-IP-vs-DNS verification behavior;
- DNS A/CNAME/truncation/TCP fallback/cache TTL/capacity/context reset;
- HTTP forwarding and bidirectional CONNECT loopback integration;
- active CONNECT cancellation on VPN lifetime loss;
- proxy admission/backpressure and multi-megabyte CONNECT transfer regression;
- proxy session shutdown drain;
- logging path/append/retention;
- traffic counters and rolling ping;
- DPAPI secret protection;
- private ephemeral RAS phonebook + PSK create/cleanup smoke test on Windows;
- `VpnContext` lifetime/collectability;
- process memory monitor lifecycle;
- repeated proxy lifecycle stress;
- self-contained `win-x64` publish + ZIP artifact.

Do not claim a change is verified until the GitHub Actions run for the actual current head is green.

## Important remaining work / audit risks

1. **Real Windows 11 L2TP E2E is still the biggest validation gap.** CI validates native APIs and local networking but does not have the user's real VPN endpoint. Test both existing Windows-profile and custom-ephemeral modes.
2. Validate on a real endpoint that RAS P/Invoke layouts, PPP server/client addresses, authentication/encryption combinations and cleanup behave as expected on Windows 11.
3. Complete/verify GUI presentation of the latest L2TP verification/keepalive/reconnect/fail-closed reason. A bounded latest-status registry exists; check whether the current GUI head already displays it before implementing anything.
4. Complete settings UI polish and validation for all custom L2TP parameters and timeouts; verify selective reload affects only dependent proxy/VPN groups.
5. Continue long-run Pause/Resume/reconfigure stress and verify no monotonic retained object/handle graph. Use deterministic ownership/bounded-count tests rather than machine-specific absolute working-set gates.
6. For performance changes, maintain before/after repeatable data-path tests. Memory-only changes that increase latency/jitter or reduce throughput are rejected by project requirements.
7. Update `docs/windows-integration-test.md` as real Windows results become known.

## GitHub issue map

Issues are the live roadmap. At handoff time the historically relevant items include:

- #1 split-tunnel preflight — completed/closed.
- #2 real Windows 11 integration/E2E — open and important.
- #3 GUI/tray lifecycle — completed/closed.
- #4 multi-proxy/shared-dedicated L2TP/Pause/lease semantics — implementation substantially present; verify current issue status and acceptance before closing.
- #5 settings UI / bind IP+port / timeouts / existing L2TP selection — substantially implemented; verify remaining acceptance items.
- #6 custom ephemeral L2TP — native smoke test + runtime integration implemented; real external endpoint test remains.
- #7 keepalive/reconnect — implementation present; real L2TP validation remains.
- #8 transition build blocker — completed/closed.
- #9 logging/retention — implementation present; inspect current issue status.
- #10 runtime traffic/ping metrics — implementation present; inspect current issue status.
- #11 project-wide performance/memory optimization — ongoing architectural goal.
- #12 L2TP runtime status/diagnostics in GUI — bounded status backend implemented; GUI completion must be checked on current head.
- #13 long-run memory stability — open ongoing hardening/audit goal.

Do not rely on the statuses in this snapshot if GitHub differs; fetch the issues in the new chat.

## Development style / process

- Work directly from the private GitHub repository and inspect actual files before changing them.
- Direct commits to `main` have been acceptable in this project unless the user says otherwise.
- Keep user updates concise but technical during longer runs.
- Do not ask questions for decisions already fixed in requirements/handoff; make implementation-oriented best-effort decisions.
- Do not regress to .NET 8; target is fixed at .NET 10.
- Do not introduce a DIRECT fallback.
- Do not replace custom L2TP-bound DNS with `System.Net.Dns`.
- Do not introduce WFP/firewall as a baseline requirement; application-level fail-closed is the accepted baseline, with firewall/WFP only optional future hardening.
- Do not optimize memory by adding latency to the proxy transfer hot path.
