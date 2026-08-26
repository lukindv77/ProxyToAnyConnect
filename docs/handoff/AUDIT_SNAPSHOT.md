# ProxyToAnyConnect — technical audit snapshot

This document preserves engineering findings and the reasons behind current architecture. Live GitHub code remains authoritative.

## Fail-closed routing

- Source-IP binding alone is insufficient; every outbound proxy TCP socket uses both L2TP source IPv4 `Bind()` and `IP_UNICAST_IF` for the L2TP interface.
- Existing Windows VPN profile is rejected before dial unless it is L2TP + split tunnel.
- Active IPv4 default routes are captured before/after dial and continuously monitored; mismatch is fail-closed and disconnects the managed VPN.
- `RasDial` success is not readiness. Usable context is published only after `Disconnected -> Dialing -> Verifying -> Ready` and L2TP-bound HTTPS verification.
- Fixed expected public IPv4 must exactly match observed egress.

## DNS

System DNS is not trusted for proxy destinations. `L2tpDnsResolver` uses source/interface-bound UDP, TCP fallback, CNAME handling, loop protection and bounded TTL cache scoped to the current VPN context. No DIRECT/system-DNS fallback.

## RAS / native ownership

Windows CI smoke tests drove fixes to RAS ABI/layout assumptions. Custom L2TP uses a private temporary `.pbk` rather than a persistent Windows Settings profile.

Critical lifecycle invariant already fixed: one monitor CTS/task per RAS session. Disconnect cancels and joins the exact old monitor; stale monitor cannot hang up a replacement RAS handle. Do not regress this.

## Proxy admission and shutdown

- `maxConcurrentConnections` bounds user-space accepted sessions; admission happens before Accept and Windows backlog supplies backpressure.
- Cancellation alone is not deterministic shutdown. `ProxyServer.RunAsync` drains accepted sessions before return by reacquiring the full permit set.
- Higher runtime must not release L2TP lease before exact proxy run task finishes that drain.
- Proxy completion observer is tracked/joined, not forgotten fire-and-forget.

## Selective reconfigure / memory

- unchanged proxy/L2TP groups retain exact runtime object identity;
- proxy-only changes recreate only proxy;
- VPN change recreates that VPN + dependents;
- canonical endpoint uniqueness parses IP addresses, so textual aliases cannot bypass listener collision validation;
- 250-cycle selective-reconfigure stress preserved independent-group identity and recorded 0/250 retained replaced proxy runtimes and 0/250 retained VPN managers after test-only forced GC.

Whole-process memory state must remain bounded. Production forced GC is prohibited. Memory-only changes cannot add meaningful latency/jitter or reduce throughput.

## Proxy parsing/hot path

- pooled transfer buffers and bounded/growing header storage;
- no full tunnel buffering;
- incremental CRLFCRLF scan avoids repeated prefix rescans under fragmented headers;
- parser/origin allocation tests and setup timing guards;
- atomic traffic accounting.

## HTTP framing / request-smuggling finding — issue #14

Old plain HTTP behavior could write all post-header remainder and then pump client→origin unboundedly while hop-by-hop filtering removed `Transfer-Encoding`, creating framing ambiguity and smuggling risk.

Production commit `f9db53f074d6740296e46452077622099b6f64ff` now:

- validates framing before outbound connect;
- accepts only one valid non-negative decimal Content-Length;
- rejects duplicate/conflicting/comma-list CL;
- rejects all Transfer-Encoding and TE+CL until decode/re-encode exists;
- treats no CL as zero body;
- rejects already-read bytes beyond CL;
- forwards exactly CL body bytes, never later pipelined/trailing bytes;
- fails early EOF;
- preserves valid CL;
- leaves CONNECT opaque.

`ProxyHttpFramingSelfTests` provides parser and loopback boundary tests.

## Current exact Windows verdict — build #272

At handoff docs commit `b3fbe1f96c0ffa7d031cb72b81793ec6ea9c2858`:

- compile succeeded, 0 warnings/errors;
- setup paired timing guard passed: parser `1999 vs 2042 ns/op = 0.98x`, origin `973 vs 1222 = 0.80x`;
- framing suite was reached;
- `ExactContentLengthBoundsClientToOriginBytesAsync` failed while its client `ReadToEndAsync` received `IOException` / Windows `SocketException 10054` (connection forcibly closed by remote host).

Likely explanation to investigate: after proxy intentionally consumes only declared CL bytes, malicious trailing client bytes remain unread; closing a TCP socket with unread receive data can produce an RST on Windows. This may be test-observation semantics rather than bytes leaking to origin, but it must be proven. Alternatives include proxy closing before full origin response or another production bug.

Do not weaken the fundamental invariant that bytes after CL must never reach origin. A deterministic test may need to read the expected response framing/body rather than requiring clean EOF if reset occurs only after a complete response and deliberate trailing attack bytes remain unread. Verify before changing test or production behavior.

## Proxy setup timing audit

Current source uses alternating paired measurement, 2048 warmup, 9 rounds, 32768 ops/round and unchanged 1.25x maximum slowdown policy. Although earlier runs #270/#271 were noisy/red, build #272 passed the paired gate. The immediate blocker is no longer timing unless later exact-head CI regresses.

## New startup ownership bug — issue #15

`ProxyInstanceRuntime.StartAsync` can publish `_lease`, `_runCancellation`, `_runTask`, then fail/cancel awaiting listener readiness. Existing catch cleanup may release/dispose local ownership without awaiting exact run-task drain or clearing already-published fields.

Required transactional order for failed/cancelled startup:

`cancel exact run CTS -> await exact runTask drain -> clear fields only if same attempt/generation -> dispose CTS -> release exact L2TP lease once`.

Preserve cancellation semantics, retry, Pause/Dispose idempotence, no double-release/unobserved observer and successful Running path.

Planned test seam is only orchestration-level: injectable lease acquisition + server lifetime (`RunAsync`, `WaitUntilListeningAsync`); production VPN/DNS/socket/proxy chain remains unchanged.

## External validation boundary

GitHub Windows CI can test native APIs, private RAS phonebook/DPAPI, loopback proxy/DNS, lifetimes, stress and packaging. It cannot validate the user's actual Windows 11 L2TP endpoint, real public egress, PPP server address, real authentication/encryption/certificate/PSK or live reconnect behavior. #2/#6/#7 remain open for this reason.

## Next-chat audit order

1. Fetch exact live head/actions/issues.
2. Investigate framing test reset 10054 while preserving strict CL boundary.
3. Get exact-head Windows framing tests green and finish #14 if acceptance is met.
4. Implement #15 transactional startup ownership with drain-before-lease-release regression coverage.
5. Continue #11/#13 and real Windows acceptance work.
