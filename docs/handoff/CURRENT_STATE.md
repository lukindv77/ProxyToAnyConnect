# ProxyToAnyConnect — current handoff state

> Prepared 2026-08-27. Live protected GitHub `main` is authoritative. Before coding, fetch exact head, live issues/comments and exact-head Actions.

## Repository / product

- Repository: `lukindv77/ProxyToAnyConnect` — public.
- Branch: protected `main`; owner + ChatGPT Codex Connector bypass the update restriction.
- Target: Windows 11 x64, C#/.NET 10 `net10.0-windows`, WinForms + tray.
- Multiple local HTTP/HTTPS forward proxies; every proxied connection is forced through its selected L2TP connection with no DIRECT fallback.

The canonical requirements are `docs/requirements.md`, `docs/architecture.md` and `docs/memory-stability.md`.

## Last fully verified substantive checkpoint before handoff packaging

`4b100f3bb6c744b08918ce122ab75982fa263740` — `evidence: support per-proxy and direct egress expectations`.

Windows build #534 completed successfully, including evidence smoke, restore/build, aggregate self-tests, self-contained win-x64 publish, binary identity manifest, ZIP and artifact upload. Build artifact `9637762202`, digest `sha256:be01041fefa07c4fe4dd39f4a02e5c038b9e729b97049a7da4880d685aedf239`.

Handoff #340 for that SHA also succeeded. Later handoff-document/workflow commits intentionally move `main`; therefore the new chat must verify the exact live head instead of assuming `4b100f3...` is still current.

## Implemented/accepted architecture

### Fail-closed routing

- Outbound TCP uses both L2TP source IPv4 `Bind()` and `IP_UNICAST_IF`.
- Proxied DNS is custom L2TP-bound UDP/TCP resolver with CNAME handling and bounded cache; no system-DNS/DIRECT fallback.
- Existing Windows VPN profile must be L2TP + split-tunnel before dial.
- IPv4 default routes are guarded before/after dial and continuously.
- State is `Disconnected -> Dialing -> Verifying -> Ready`; usable context is not published before Ready.
- HTTPS verification is performed through the bound L2TP path.
- L2TP loss cancels all dependent sessions fail-closed.

### Proxy / HTTP

- HTTP forward proxy + HTTPS CONNECT, no MITM.
- Strict HTTP framing/request-smuggling protection is implemented and #14 is closed.
- 32 KiB pooled transfer buffers, bounded accepted-session admission/backpressure, incremental header scanning and allocation/timing/data-path regressions.
- Accepted sessions are drained before `ProxyServer.RunAsync` returns; higher runtime releases VPN lease only after exact proxy run drain.

### Proxy runtime / coordinator

- Transactional startup ownership is implemented and #15 is closed.
- Pause/Dispose preserve `cancel exact generation -> drain -> release lease` ordering.
- Desired enabled starts survive cancellation/failure as pending and retry on identical config.
- Same-config reconfigure detects missing runtime topology as drift and restores missing owners.
- Independent proxy starts/restarts may overlap while the coordinator operation itself remains serialized.
- A failed independent group remains Error/pending while unrelated group can reach Running.
- Independent cleanup owners overlap inside dependency phases; all proxy owners drain before VPN-manager cleanup begins.
- Cleanup failures are reported deterministically by input order while independent cleanup continues.

### RAS / L2TP

- Async callback-based `RasDialW` with exact `HRASCONN` ownership.
- Password managed carrier is cleared immediately after native dial handoff; PSK carrier after native credentials handoff.
- Native callback root is retained until Connected or proven terminal `ERROR_INVALID_HANDLE`.
- One RAS hangup/drain attempt is bounded to prevent unbounded Pause/Reconfigure/Exit; timeout deliberately retains callback root and exact handle for safe retry.
- RAS manager cleanup preserves primary failures while independently draining monitor/context/phonebook/lifetime owners.
- CustomEphemeral private PBK has lock-first ownership marker protocol, stale-session recovery and repeated failure cleanup coverage.
- ExistingWindowsProfile enumeration owns its PowerShell process tree; dialog shutdown waits exact profile-helper task drain.

### VPN leases / keepalive

- Shared/dedicated lease semantics; first active lease connects/verifies, last release disconnects.
- Last release and manager disposal clear DNS/status/lifetime ownership even if another cleanup phase fails.
- Shared L2TP failure invalidates all dependent sessions while unrelated groups remain isolated.
- Reconnect cooldown is exposed/observed without repeated exception churn; maintenance stops after last lease.
- Keepalive ICMP is native asynchronous/event-driven and bound to the selected L2TP context.

### GUI / configuration

- All Add/Edit/Remove/Logging and Start/Pause actions use a strict FIFO GUI generation queue.
- Durable file save is the persisted desired-state publication boundary.
- `appsettings.json` save uses a unique sibling temp file and mandatory cleanup; incomplete writes do not replace the current config.
- Loaded legacy-invalid config can be repaired across several in-memory staged edits. Invalid partial generations do not reach disk/runtime; the final valid repair publishes the complete draft.
- Logging and runtime are independent consumers of the same durable generation. Caller cancellation remains primary control flow even if a consumer faults.
- UI projects `desired ∪ actual`, so desired-but-missing runtime and residual cleanup drift remain visible.
- Explicit Exit closes owned modal configuration UI, stops queue admission, cancels/drains exact config generations and L2TP profile helper, then disposes runtime and process-memory monitor.
- Password/PSK protected values are pruned when the selected auth/mode no longer uses them.

### Diagnostics / #13

- Append-only daily JSONL logging, bounded retention and no in-memory log history.
- Current process-memory snapshot includes PID/process start time, managed heap/allocation/GC, working/private bytes, handles/threads and native RAS callback-root current/high-watermark.
- Portable manifest-protected Windows soak bundle binds to exact PID/start-time/executable SHA and rejects PID reuse.
- Soak/evidence validators stream large JSONL data rather than materializing multi-day history.
- External working/private/handle/thread series can be correlated with `process.memory.*` managed records using the same exact process lifetime.
- Production forced GC remains prohibited.

## Evidence state

Baseline -> Ready -> Final integration evidence captures routes, VPN profiles, interfaces, process identity, proxy/direct probes and log excerpts. Release-grade mode can bind the run to exact `ProxyToAnyConnect.exe` SHA-256 from CI.

Latest evidence work at `4b100f3...` adds:

- backward-compatible default expected proxy egress IPv4;
- optional expected public IPv4 per proxy endpoint for heterogeneous shared/dedicated groups;
- optional explicit direct-host expected public IPv4 proving ordinary host traffic stays off the proxy/L2TP path.

The new chat must inspect whether the corresponding Test/Complete scripts and hosted positive/negative smoke fully enforce these newest expectations end-to-end; do not assume the collector-only change is the entire acceptance path.

## Issue state

Open: `#2, #4, #5, #6, #7, #11, #13`.

Closed/completed include `#14` HTTP framing and `#15` transactional startup, in addition to earlier closed issues.

The main remaining release boundary is real Windows 11 + real L2TP endpoint acceptance for #2/#4/#5/#6/#7 and a representative 12–24 h exact-binary soak for #13. #11 remains an ongoing performance/memory architecture requirement.

## Immediate continuation

1. Fetch exact live head + exact-head build/handoff verdict.
2. Finish/verify per-proxy and direct egress expectation validation across Invoke/Test/Complete evidence scripts and CI smoke.
3. Continue broad deterministic lifecycle/resource/performance hardening that does not require a real endpoint.
4. When a real endpoint is available, execute #2/#4/#5/#6/#7 with exact-binary evidence.
5. Run the documented 12–24 h #13 soak and correlate external/native resource data with managed `process.memory.*` records.
