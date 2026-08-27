# ProxyToAnyConnect — GitHub issues snapshot

Prepared 2026-08-27. Query live GitHub again in the new chat; issue comments after this snapshot are authoritative.

## Open release / architecture issues

### #2 — Windows 11 integration test with real L2TP endpoint

Highest external-validation priority. Requires exact self-contained CI binary on Windows 11 x64 + real L2TP endpoint(s): route/profile fingerprints, exact executable SHA, HTTP/CONNECT egress, direct-host independence, fail-closed loss, shared/dedicated groups, keepalive/reconnect and CustomEphemeral cleanup.

Latest evidence work supports expected proxy public IPv4 per proxy endpoint plus a distinct expected direct-host public IPv4; verify the whole Invoke/Test/Complete toolchain and CI smoke on current head.

### #4 — Multi-proxy runtime with shared/dedicated L2TP leases and Pause/Resume

Implementation is substantially complete and heavily self-tested: shared/dedicated lease ownership, first-connect/last-disconnect, independent Pause/Resume, isolated unrelated groups, concurrent independent starts and dependency-phased cleanup. Real active-L2TP multi-proxy acceptance remains with #2.

### #5 — Settings UI / Windows L2TP selection / timeouts

Implementation substantially complete: strict FIFO GUI generations, staged repair of legacy-invalid config, transactional persistence, desired∪actual topology display, selective runtime reconciliation, Start/Pause serialization, modal/profile-helper shutdown ownership and secret pruning. Remaining acceptance is real operator-facing Windows/profile/address interaction and live selective effects.

### #6 — Custom ephemeral L2TP

Private temporary PBK, DPAPI, PSK/native credentials handoff, lock-first ownership marker, orphan recovery and repeated partial-failure cleanup exist. Real external authentication/encryption/certificate/PSK endpoint acceptance remains.

### #7 — Keepalive/reconnect

Off / PPP-server IPv4 / CustomIPv4 modes, L2TP-bound asynchronous ICMP, failure threshold invalidation, fail-closed dependent cancellation, cooldown and reconnect-while-leases-remain architecture exist. Needs real L2TP failure/reconnect validation.

### #11 — Performance and memory

Permanent architecture goal. Keep low-latency/throughput and whole-process bounded memory together. No optimization may add hot-path blocking/global locks/extra copies/per-buffer allocations or weaken routing/verification. Production forced GC is prohibited.

### #13 — Long-run memory stability / deterministic ownership

Ongoing. Current code includes deterministic cleanup-through-faults across proxy/VPN/RAS/GUI owners, native callback-root current/high-watermark telemetry, bounded process-memory snapshots, portable manifest-protected exact-binary soak evidence and streaming managed-log correlation. Final acceptance requires representative 12–24 h Windows 11 + L2TP soak with traffic/reconnect/Pause/Resume/reconfigure.

## Recently closed major issues

### #14 — HTTP request framing / request-smuggling boundary — completed

Strict Content-Length framing, ambiguous CL/TE rejection, exact body forwarding and Windows reset-safe response tests are accepted. Do not reopen or weaken this invariant without new evidence.

### #15 — Transactional, drain-safe proxy startup ownership — completed

Rejected/cancelled startup drains the exact listener/session generation before lease release; retry/cancellation/idempotence coverage exists. Do not reintroduce release-before-drain ordering.

Earlier completed issues include #1, #3, #8, #9, #10 and #12.

## Snapshot sets

Open: `#2, #4, #5, #6, #7, #11, #13`

Closed/completed include: `#1, #3, #8, #9, #10, #12, #14, #15`.
