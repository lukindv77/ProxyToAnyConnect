# ProxyToAnyConnect — GitHub issues snapshot

Snapshot prepared 2026-08-26. The next chat must still query live GitHub issue state/comments.

## Closed

- **#1 — Enforce split-tunnel RAS profile before dialing** — completed.
- **#3 — GUI lifecycle: tray-first WinForms application with explicit Exit only** — completed.
- **#8 — Restore green CI after GUI + multi-proxy configuration refactor** — completed.
- **#9 — Daily append-only L2TP logs with monthly folders and retention** — completed.
- **#10 — Runtime proxy/L2TP traffic counters and 5-minute ping metrics in GUI** — completed/closed. Metrics plus L2TP status presentation are now present.
- **#12 — L2TP runtime diagnostics: meaningful GUI status and last fail-closed reason** — completed/closed. Bounded latest-status registry and `Status / reason` GUI surface are implemented/tested.

## Open

### #2 — Windows 11 integration test with real L2TP endpoint

Highest external-validation priority. CI cannot substitute for a real Windows 11 x64 + real L2TP server test. Covers existing/custom L2TP, real assigned/PPP IPs, route behavior, public egress verification, HTTP/CONNECT, loss/fail-closed, shared/dedicated leases, keepalive/reconnect and cleanup.

### #4 — Multi-proxy runtime with shared/dedicated L2TP leases and Pause/Resume

Implementation is substantially present. Additional hardening completed after original issue creation includes canonical listener endpoint collision validation and selective-reconfigure object-identity regression. Real active-L2TP multi-proxy acceptance remains tied to #2.

### #5 — Settings UI for proxy instances, Windows L2TP selection and timeouts

Substantially implemented. Selective reload preserves independent runtime groups in deterministic tests. Remaining acceptance is primarily real Windows/profile/address interaction polish/validation together with #2.

### #6 — Custom ephemeral L2TP with protected credentials and private temporary RAS phonebook

Private `.pbk`, DPAPI secrets, native Windows creation/PSK/cleanup smoke test and common dial/verify integration exist. Real external custom endpoint/auth/encryption validation remains.

### #7 — L2TP keepalive with internal-server/custom IPv4 targets and automatic reconnect

Implementation exists for Off / PPP server IPv4 / CustomIPv4, source-bound probing, threshold fail-closed teardown and maintenance reconnect while leases remain. Needs real L2TP validation.

### #11 — Performance and memory: low-latency proxy path and efficient process-wide memory use

Ongoing architectural goal. Recent work includes incremental header terminator scanning and HTTP framing hardening. Memory changes must not worsen proxy latency/jitter/throughput beyond measurement noise.

Current immediate blocker relevant to #11: `ProxySetupTimingSelfTests` reports current text-span parser 1.66–1.75x slower than its immediate-predecessor benchmark after framing bookkeeping. The 1.25x policy threshold has not been relaxed. Next chat must establish whether this is a real production regression or benchmark comparability problem.

### #13 — Long-run memory stability: deterministic ownership and process memory health

Ongoing. Selective-reconfigure stress now runs 250 cycles while preserving independent-group object identity. Observed retained replaced objects in the Windows test: `ProxyInstanceRuntime` 0/250, `VpnLeaseManager` 0/250. Continue broader Pause/Resume/reconnect/start-failure/resource-lifetime audit.

### #14 — Harden HTTP request framing and request-smuggling boundary

**Open. Production code is implemented but acceptance is not yet declared complete.**

Production commit: `f9db53f074d6740296e46452077622099b6f64ff`.

Implemented:
- strict single non-negative decimal `Content-Length`;
- reject duplicate/conflicting/comma-list CL;
- reject any Transfer-Encoding and TE+CL;
- no CL => body length zero;
- reject initial post-header bytes beyond CL before outbound connect;
- forward exactly CL bytes and no later pipelined/smuggled bytes;
- fail on early EOF;
- preserve valid CL in origin request;
- CONNECT unchanged;
- new `ProxyHttpFramingSelfTests` parser + loopback boundary suite.

Current blocker: build #270 and #271 both compile but fail earlier in `ProxySetupTimingSelfTests`; therefore exact Windows CI has not yet reached the new framing suite. #14 must remain open until timing verdict is resolved and framing tests execute green on exact current head.

### #15 — Make proxy startup ownership transactional and drain-safe

**Newest audit issue, implementation pending.**

Finding: `ProxyInstanceRuntime.StartAsync` can publish `_lease/_runCancellation/_runTask`, then fail/cancel while waiting for listener readiness. Catch cleanup may release/dispose local resources without awaiting exact `runTask` drain or clearing already-published fields.

Acceptance:
- startup attempt has generation-scoped ownership;
- fail/cancel after run creation: cancel exact run -> await drain -> clear matching fields -> dispose CTS -> release exact lease once;
- never release L2TP lease before `ProxyServer.RunAsync` session drain completes;
- cancellation propagation and retry remain correct;
- Pause/Dispose idempotent, no double release;
- no unobserved observers;
- successful Running path unchanged.

Planned test seam is orchestration-only; production L2TP/network/data path must not change.

## Snapshot sets

Open: `#2, #4, #5, #6, #7, #11, #13, #14, #15`

Closed: `#1, #3, #8, #9, #10, #12`
