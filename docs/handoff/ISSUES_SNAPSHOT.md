# ProxyToAnyConnect — GitHub issues snapshot

Prepared 2026-08-26. Query live GitHub again in the new chat.

## Closed

- #1 split-tunnel RAS profile preflight — completed.
- #3 GUI/tray lifecycle — completed.
- #8 GUI/multi-proxy transition build blocker — completed.
- #9 append-only logging/retention — completed.
- #10 runtime proxy/L2TP traffic + 5-minute ping metrics — completed/closed.
- #12 L2TP latest status/reason GUI diagnostics — completed/closed.

## Open

### #2 — Windows 11 integration test with real L2TP endpoint

Highest external-validation priority. Requires real Windows 11 x64 + real L2TP endpoints for route behavior, egress, HTTP/CONNECT, fail-closed loss, shared/dedicated leases, keepalive/reconnect and custom ephemeral cleanup.

### #4 — Multi-proxy runtime with shared/dedicated L2TP leases and Pause/Resume

Implementation substantially present. Canonical listener endpoint validation and selective-reconfigure identity tests are implemented. Real active-L2TP multi-proxy acceptance remains with #2.

### #5 — Settings UI / Windows L2TP selection / timeouts

Substantially implemented. Deterministic selective reload preserves independent runtime groups. Remaining acceptance primarily real Windows/profile/address interaction and polish.

### #6 — Custom ephemeral L2TP

Private `.pbk`, DPAPI, native Windows phonebook/PSK/cleanup smoke test and common dial/verify integration exist. Real external endpoint/auth/encryption validation remains.

### #7 — Keepalive/reconnect

Off / PPP-server IPv4 / CustomIPv4, source-bound probes, threshold fail-closed teardown and reconnect architecture exist. Needs real L2TP validation.

### #11 — Performance and memory

Ongoing architecture goal. Current setup paired timing gate passed on build #272; continue preserving latency/throughput while hardening memory/lifetimes.

### #13 — Long-run memory stability

Ongoing. 250 selective-reconfigure cycles preserve independent object identity with recorded retained replacements 0/250 proxy runtimes and 0/250 VPN managers.

### #14 — HTTP request framing / request-smuggling boundary

Open. Production code in `f9db53f074d6740296e46452077622099b6f64ff` implements strict Content-Length framing and rejects Transfer-Encoding.

Exact build #272 at handoff docs head `b3fbe1f96c0ffa7d031cb72b81793ec6ea9c2858` reached the new `ProxyHttpFramingSelfTests` after the paired timing guard passed. Current failure:

- `ExactContentLengthBoundsClientToOriginBytesAsync`
- `ReadToEndAsync` receives IOException / Windows SocketException 10054 (connection reset by remote host).

Next task is to determine whether reset is a Windows consequence of closing with intentionally unread malicious trailing bytes after exactly CL bytes were forwarded, a premature production close, or an over-strict test expectation. **Do not weaken the invariant that trailing/pipelined bytes after CL never reach origin.** #14 stays open until exact-head Windows framing tests and other gates pass.

### #15 — Transactional, drain-safe proxy startup ownership

Newest confirmed lifecycle bug; implementation pending.

`ProxyInstanceRuntime.StartAsync` can publish lease/run ownership then fail/cancel waiting for listener readiness without guaranteed exact run-task drain and field cleanup before lease release.

Required order: cancel exact run -> await drain -> clear same-generation fields -> dispose CTS -> release exact lease once. Preserve cancellation/retry/Pause/Dispose idempotence and successful Running behavior.

## Snapshot sets

Open: `#2, #4, #5, #6, #7, #11, #13, #14, #15`

Closed: `#1, #3, #8, #9, #10, #12`
