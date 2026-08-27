# ProxyToAnyConnect — technical audit snapshot

This document preserves the main engineering findings and the reasons behind current architecture as of the 2026-08-27 handoff. Live GitHub code is authoritative.

## Fail-closed routing findings

- Source-IP binding alone is insufficient. Every outbound proxy TCP socket must use both L2TP source IPv4 `Bind()` and `IP_UNICAST_IF` for the selected L2TP interface.
- Proxy destination DNS must not use host/system DNS. `L2tpDnsResolver` owns source/interface-bound UDP, TCP fallback, CNAME traversal/loop protection and bounded per-context cache.
- Existing Windows VPN profiles are rejected before dial unless they are L2TP and split-tunnel.
- `RasDial` success is not readiness. A usable VPN context is published only after interface projection, route guards and real L2TP-bound HTTPS verification reach Ready.
- IPv4 default-route fingerprints are checked before/after dial and monitored continuously. Route drift invalidates the managed context fail-closed.
- Direct host traffic and proxied traffic must be proven independently in release evidence. Latest tooling supports a default expected proxy egress, per-proxy endpoint overrides and a separate direct-host expected IPv4.

## HTTP framing / parser audit

A previous plain-HTTP implementation could create request-framing ambiguity by forwarding post-header bytes without enforcing one authoritative request body length. The completed #14 work now enforces:

- one valid non-negative decimal `Content-Length` only;
- duplicate/conflicting/comma-list CL rejected;
- any `Transfer-Encoding` and TE+CL rejected until a real decode/re-encode implementation exists;
- no CL means zero request body;
- already-buffered data beyond declared CL is rejected;
- exactly CL bytes are forwarded, with early EOF failure;
- later client bytes cannot become a second/smuggled origin request;
- CONNECT remains opaque and unchanged.

Windows close/reset behavior with malicious unread trailing data was handled in tests without weakening the origin-side no-post-CL invariant. #14 is closed.

## Proxy lifecycle audit

The startup audit found a generation-ownership bug where a listener run/lease could be partially published before readiness failure. The completed #15 architecture establishes:

`acquire exact lease -> create exact run CTS/task -> wait readiness -> publish Running`

and on rejected/cancelled start:

`cancel exact run CTS -> drain exact run task -> clear only same-generation fields -> dispose CTS -> release exact lease once`.

Pause/Dispose follows the same drain-before-lease-release rule. Caller cancellation remains control flow and is not replaced by secondary cleanup exceptions. #15 is closed.

## RAS / native callback ownership audit

- `RasDialW` is callback-driven async and tracked by exact `HRASCONN`.
- The unmanaged callback thunk must remain rooted until Connected or until teardown proves `ERROR_INVALID_HANDLE`.
- Releasing a callback root on a failed/ambiguous `RasHangUp` is unsafe because native RAS may still invoke it; process-lifetime retention is safer than callback-after-free.
- One hangup/status-drain attempt is bounded to avoid indefinite application shutdown. Timeout does not claim terminal state and does not release the callback root; higher ownership keeps the exact handle for retry.
- Native callback roots are tracked in a bounded exact-handle registry with deterministic high-churn regressions and current/high-watermark telemetry.
- Managed RAS password/PSK carrier references are cleared immediately after native handoff where possible. This drops managed references; immutable string memory is not falsely claimed to be cryptographically zeroized.

## RAS manager / keepalive audit

- One monitor CTS/task belongs to one exact RAS session. A stale monitor must never hang up a replacement handle.
- Disconnect/Dispose drains monitor ownership before completing session cleanup.
- Monitor, context, ephemeral phonebook and shutdown-lifetime owners are independent cleanup phases; one failure must not silently skip the others.
- Native ICMP keepalive is event-based asynchronous, not a blocking worker loop.
- Keepalive failure threshold invalidates the shared VPN context and dependent sessions; reconnect happens only while active leases remain and observes reconnect cooldown.

## CustomEphemeral audit

- No persistent Windows Settings VPN profile is created.
- Private temporary PBK ownership is published lock-first, then marker. Marker-first publication was rejected because another process could misclassify a live session as orphaned.
- Cleanup only deletes directories with recognized ownership marker; ambiguous/unmarked directories are preserved fail-safe.
- Marker contains the exact RAS entry name so stale recovery can best-effort delete the RAS entry before directory deletion.
- Repeated partial-creation failures are regression-tested for non-accumulation of managed session directories.
- Real endpoint authentication/encryption/certificate/PSK acceptance is still external #6 work.

## VPN lease / coordinator audit

- Shared and dedicated VPN configurations have explicit lease ownership.
- First lease establishes/verifies; last release disconnects. Pausing one shared proxy must not disconnect a VPN still leased by another proxy.
- DNS cache and latest-status ownership must be released even if disconnect/controller cleanup fails.
- Runtime reconfigure uses desired topology plus actual-owner drift detection; identical config can recover missing runtime generations after prior cleanup failure.
- Enabled starts that fail/cancel remain pending for same-config retry.
- Independent proxy starts/restarts are allowed to overlap rather than serializing unrelated VPN groups.
- Cleanup is dependency-phased: proxy owners first, then VPN managers. Owners within a phase may drain concurrently; diagnostics preserve deterministic input-order primary/secondary failure ordering.

## GUI / persistence audit

- Whole GUI configuration transactions are serialized, not only file writes. Add/Edit/Remove/Logging and Start/Pause use one FIFO command-generation queue.
- Durable save is the desired-state publication boundary. Runtime reconciliation failure does not roll the persisted generation back in UI memory.
- `appsettings.json` uses unique sibling temp files and mandatory cleanup. Fixed shared `.tmp` paths were rejected because cancellation/overlapping saves could conflict or leave stale temp state.
- Invalid legacy config repair is staged in-memory across multiple editor operations. No partially repaired invalid generation is written; the first globally valid accumulated draft is persisted/applied as one generation.
- Logging and runtime are independent consumers of the same durable generation. A logging change completing the last invalid field must also apply earlier proxy/VPN staged repairs.
- `desired ∪ actual` projection prevents the GUI from hiding saved-but-missing runtime or residual cleanup drift.
- Explicit Exit closes the currently owned modal editor, stops command admission, cancels/drains the queue and exact Windows-profile helper task, then tears down runtime owners.

## Logging / process-memory audit

- File logging is fail-soft and transactional; malformed editable logging paths must not crash startup before the user can repair them.
- Retention cleanup owns its cancellation source until the last worker exits; cancellation callback faults cannot abort independent cleanup.
- No unbounded in-memory log history is retained.
- Process memory health retains a bounded latest snapshot only: managed heap/allocation/GC counts, working/private bytes, handles/threads, PID/start-time and native callback-root current/high watermark.
- Production forced GC and working-set trimming are prohibited.

## Long-run evidence audit (#13)

The soak path must measure rather than perturb the process:

- collector binds to exact PID, process start time and executable SHA-256 and rejects PID reuse;
- bundle is portable and manifest-protected;
- sample and log validators stream multi-day data with bounded memory;
- external working/private/handle/thread samples correlate with application `process.memory.*` managed records using exact PID + start time;
- hosted smoke proves tooling mechanics only, not leak absence;
- final acceptance needs 12–24 h representative traffic/reconnect/Pause/Resume/reconfigure workload and trend review without adding arbitrary machine-specific working-set thresholds.

## Current external validation boundary

Hosted Windows CI can validate native APIs, PBK/DPAPI mechanics, loopback data path, lifecycle ownership, stress and packaging. It cannot prove the user's actual L2TP server behavior, PPP server address, public egress, authentication policy, real keepalive failure/reconnect path or operator GUI experience.

Therefore release-critical external issues remain #2/#4/#5/#6/#7, plus #13 long-run soak. #11 remains an ongoing performance/memory requirement.

## Next audit/development order

1. Re-read live head and latest issue comments.
2. Verify the new per-proxy/direct expected-egress contract across collector, validators, aggregate completion and CI positive/negative smoke.
3. Continue deterministic ownership/stress/performance work only where it preserves fail-closed and hot-path latency.
4. Execute the real Windows 11/L2TP acceptance matrix and 12–24 h soak when the environment is available.
