# ProxyToAnyConnect — technical audit snapshot

This snapshot records the important engineering findings and reasons behind the current architecture. Live code on GitHub still wins if it differs.

## 1. Fail-closed routing decisions

### Source IPv4 binding alone is insufficient

Every outbound proxy TCP socket must use both:

- source `Bind()` = dynamically assigned L2TP client IPv4;
- `IP_UNICAST_IF` = L2TP interface index.

This is application-local and does not change normal routing for unrelated applications.

### `RasDial` can violate the unrelated-app route requirement

Existing Windows profile is checked before dial for L2TP + split tunneling. Active IPv4 default routes are snapshotted before/after dial and continuously monitored while Ready. Route mismatch is fail-closed and hangs up the managed L2TP.

### `RasDial` success is not readiness

The usable context is published only after `Disconnected -> Dialing -> Verifying -> Ready`. Verification performs an L2TP-bound HTTPS request. Fixed expected public IPv4 must match observed egress exactly.

## 2. DNS audit

`System.Net.Dns` is rejected for proxied destinations because it cannot prove selected-L2TP egress.

Implemented custom resolver:

- A/IPv4 queries;
- source/interface-bound UDP;
- TCP fallback for truncation;
- CNAME following + loop protection;
- bounded TTL cache scoped to L2TP/VpnContext identity;
- cache reset across context replacement;
- no system-DNS or DIRECT fallback.

Several DNS allocation/materialization paths have dedicated before/after self-tests.

## 3. RAS/PInvoke audit

Compilation alone did not validate native ABI. Windows runner smoke tests found and drove fixes to RAS structure/layout handling.

Custom ephemeral L2TP uses a private temporary `.pbk`; it is not a persistent Windows Settings VPN profile. Windows self-test covers L2TP device discovery, private phonebook/entry creation, PSK credential setup and cleanup. Real external authentication remains under #2/#6.

## 4. RAS session monitor ownership

Historical bug: old monitor tasks could survive normal disconnect/reconnect and race a replacement session.

Current invariant:

- one monitor CTS/task per RAS session;
- disconnect removes current context/handle, cancels and joins exact old monitor, then releases session resources;
- stale old monitor may hang up only if its handle is still atomically current;
- old monitor cannot invalidate a replacement session.

Do not regress this lifecycle.

## 5. Proxy concurrency and shutdown drain

Unbounded accepted-session tasks were rejected. Admission takes one bounded semaphore permit before Accept. At capacity, user-space acceptance stops and Windows TCP backlog provides backpressure.

Cancellation alone is not enough for Pause/reconfigure/Exit. `ProxyServer.RunAsync` drains accepted sessions before returning by acquiring the entire permit count after listener stop. Higher runtime code therefore must not release the L2TP lease until the exact `RunAsync` completes.

Proxy completion observer is tracked and joined; no forgotten fire-and-forget observer should remain after Pause/reconfigure/Exit.

## 6. Selective reconfigure audit

Configuration reload is intentionally selective:

- unchanged proxy/L2TP groups keep the same runtime object identity;
- proxy-only edit recreates only that proxy runtime;
- L2TP edit recreates changed L2TP plus dependent proxies;
- independent groups stay alive.

Canonical listener endpoint validation parses `IPAddress` before grouping, preventing textual aliases (`127.1` / `127.0.0.1`) from bypassing collision detection.

Stress regression: 250 selective reconfigure cycles preserve independent-group identity and leave 0/250 replaced proxy runtimes and 0/250 replaced VPN managers retained after test-only forced GC.

## 7. Memory stability rules

Whole-process bounded ownership is required, not just per-connection memory.

Important implementations:

- `VpnContext` reference ownership across manager + active outbound sessions;
- final ref release disposes context CTS deterministically;
- bounded DNS cache;
- bounded 5-minute ping window;
- bounded latest-L2TP-status registry;
- GUI rows updated in place;
- latest process-memory snapshot only;
- append-only disk log, no in-memory history;
- L2TP maintenance task only while active leases exist;
- runtime/observer/monitor lifetime self-tests.

Production forced GC / working-set trimming is prohibited.

## 8. Memory vs latency rule

A memory optimization is rejected if repeatable measurement shows meaningful proxy latency/tail/jitter regression or throughput loss.

Do not introduce global locks, synchronous waits, extra copy/serialization stages, per-buffer objects or smaller transfer buffers solely to shrink working set when this worsens the data path.

## 9. Proxy parsing/hot-path work

Completed:

- pooled transfer buffers;
- pooled/growing bounded header acquisition;
- no full tunnel buffering;
- incremental CRLFCRLF scanning: after a read without terminator, next search starts at most three bytes before previous end instead of rescanning entire header prefix;
- parser/origin-header allocation regressions;
- bounded session admission;
- atomic traffic accounting.

## 10. HTTP framing / request-smuggling audit — newest production change

Finding behind issue #14: previous plain HTTP path wrote the full header-read remainder and then pumped client→origin unboundedly, while hop-by-hop filtering stripped `Transfer-Encoding`. This could forward encoded or trailing/pipelined bytes with inconsistent framing.

Commit `f9db53f074d6740296e46452077622099b6f64ff` changes plain HTTP framing to fail closed:

- framing validated before outbound connect;
- only one valid non-negative decimal `Content-Length` accepted;
- duplicate/conflicting/comma-list CL rejected;
- any `Transfer-Encoding` rejected until decode/re-encode semantics exist;
- no CL means zero-length body;
- already-read remainder cannot exceed CL;
- body forwarding stops exactly after CL bytes;
- later bytes are not forwarded on that origin connection;
- early EOF before CL completes fails the session;
- CONNECT remains an opaque tunnel.

`ProxyHttpFramingSelfTests` includes parser and loopback boundary coverage, including a smuggling/trailing-byte case.

### Current validation blocker

The code compiles on Windows, but exact-head self-tests currently stop earlier in `ProxySetupTimingSelfTests`:

- build #270 on `f9db53f...`: text-span parser `5322` vs predecessor `3206 ns/op` = `1.66x`, limit `1.25x`;
- commit `71a93e5d...` increased benchmark warmup/sample only, keeping 1.25x limit;
- build #271: `5859` vs `3350 ns/op` = `1.75x`, still failed.

The framing suite therefore has not yet been reached by exact Windows CI. Do not lower the threshold merely to pass. Audit current `ParsedProxyRequest.Parse` and `ProxySetupTimingSelfTests` to decide whether framing bookkeeping caused a real regression or predecessor/current benchmark work is not equivalent.

## 11. Newly discovered startup ownership bug — issue #15

Current `ProxyInstanceRuntime.StartAsync` can assign `_lease`, `_runCancellation` and `_runTask`, then throw/cancel while awaiting listener readiness. Catch cleanup can dispose local resources without first awaiting exact run-task drain or clearing already-published fields.

Risk:

- stale disposed ownership fields;
- background run cleanup after startup caller has failed;
- L2TP lease release before `ProxyServer.RunAsync` completes accepted-session drain;
- retry/observer/double-cleanup complexity.

Required transactional order for a failed/cancelled startup attempt:

`cancel exact run CTS -> await exact runTask drain -> clear fields only if same attempt/generation -> dispose CTS -> release exact lease once`.

Caller cancellation must remain cancellation; later retry must be safe; Pause/Dispose remain idempotent.

Planned deterministic test seam is limited to orchestration: injectable lease acquisition and server-lifetime (`RunAsync`, `WaitUntilListeningAsync`). Production network path remains the same.

## 12. Runtime/reconfigure cancellation audit

A dedicated regression already verifies runtime start/reconfigure cancellation propagates, preserves independent groups and retries pending desired starts. Keep this test green when implementing #15; do not conflate coordinator reconciliation with per-proxy startup ownership.

## 13. CI/benchmark policy

- Build success is not enough; exact current-head self-tests and packaging matter.
- No hard throughput absolute gate on noisy hosted runners unless explicitly justified.
- Relative setup/allocation gates exist to prevent obvious regressions.
- A flaky/biased benchmark should be fixed by making compared work equivalent/stable, not by silently widening architecture policy.

## 14. External validation boundary

GitHub Windows CI validates native API smoke tests, local loopback proxy behavior, DNS, ownership, stress, DPAPI, private RAS phonebook and packaging. It cannot validate the user's actual Windows 11 L2TP endpoint/profile, real public egress, PPP server IP availability, actual auth/encryption/PSK/certificate behavior or live reconnect failures. #2/#6/#7 remain open for that reason.

## 15. Handoff order of operations

Next chat should:

1. fetch exact live head and Actions;
2. inspect latest issue comments;
3. resolve timing/parser verdict without weakening fail-closed or 1.25x policy casually;
4. execute/validate framing suite and finish #14;
5. implement #15 with drain-before-lease-release tests;
6. continue #11/#13 and real Windows acceptance work.
