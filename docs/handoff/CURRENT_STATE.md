# ProxyToAnyConnect — current handoff state

> Prepared 2026-08-26. Live GitHub `main` is authoritative; the new chat must fetch the current head, live issue comments and exact-head Actions before coding.

## Snapshot identity

- Repository: `lukindv77/ProxyToAnyConnect` (private)
- Branch: `main`
- Windows 11 x64 / C# / .NET 10 `net10.0-windows` / WinForms + tray
- HTTP framing production commit: `f9db53f074d6740296e46452077622099b6f64ff`
- setup timing methodology fix: `ceb591cf5bf62c43dc08a4611744acf445fa7286`
- framing reset-test fix: `d1f9edd7dbbaaeba2ebd858b9565507f159754e4`
- handoff status-doc commits observed during packaging: `b3fbe1f96c0ffa7d031cb72b81793ec6ea9c2858`, `b304a4331b8527b8280396047d3c649cfaed80f3`

## Current timing/framing hardening progress

The timing audit found a source-level comparability bug, not evidence for widening the policy: after framing commit `f9db53f...`, production parsing performs mandatory header-name and CL/TE framing validation while the old `LegacySplitParse` timing baseline still measured the lighter pre-framing semantics. The test therefore charged required security work as a parser regression while calling the baseline the immediate predecessor.

Commit `ceb591cf5bf62c43dc08a4611744acf445fa7286` changes only the self-test harness and keeps `MaxMedianSlowdownRatio = 1.25` unchanged. It uses a Split-strategy framing-equivalent parser baseline, stronger interleaved warmup, test-only GC phase normalization, adjacent order-balanced batches, per-pair ratios and a median of paired round ratios. Production/hot-path code is unchanged.

The framing/RST audit then confirmed the proxy intentionally stops client reads after exactly declared Content-Length bytes and waits only for the origin response. A malicious post-CL tail is therefore intentionally unread on the client socket; Windows may report WSAECONNRESET 10054 when that socket closes. Clean TCP EOF is not an HTTP correctness requirement in this attack case.

Commit `d1f9edd7dbbaaeba2ebd858b9565507f159754e4` changes only the framing self-test: it must first receive and validate the complete Content-Length-framed origin response (status/header/exact body). Reset before the complete response still fails. Only after complete response delivery does the test accept clean EOF or Windows ConnectionReset. The origin-side assertion that no post-CL byte arrives is unchanged. TE and TE+CL rejection likewise validate complete framed error responses instead of requiring clean EOF with unread invalid input.

Historical hosted-runner evidence on the old methodology remains useful:

- build #272 on `b3fbe1f...`: timing PASS — parser `1999 vs 2042 ns/op = 0.98x`, origin `973 vs 1222 = 0.80x`; framing then hit 10054;
- build #273 on docs-only `b304a433...`: timing FAIL — parser `5218 vs 2917 ns/op = 1.79x`;
- build #274 on docs-only `f0336f2...`: timing FAIL — parser `2777 vs 1974 ns/op = 1.41x`.

#14 remains open until a Windows build succeeds on the exact current head containing both test fixes. Do not infer green from an older SHA. At the time of this update GitHub Actions scheduling itself has also been unstable: build #275 for `ceb591c...` initially reported `startup_failure` and a rerun remained queued; handoff #87 for `d1f9edd...` initially reported `startup_failure` and was retriggered. Treat those scheduler states as infrastructure evidence, not code verdicts.

## Handoff workflow/artifacts observed

- handoff #84 for `b3fbe1f...`: success; artifact id 9611924335; SHA-256 `5b9307c6a184f3a6bf4ddc47b60af6569ea4a3611940f7cb7d9b527eaa72aa6b`.
- handoff #85 for `b304a433...`: success; artifact id 9612150421; SHA-256 `a25e61eb00c969fa96a0f56b92c4d6b9f621b0fb5386f6a6f1f18ea7855a042a`.

A final handoff-doc commit after this snapshot will create another artifact. New chat must use latest artifact for current `main` head.

## Immutable product/architecture

- Always GUI; form `X` hides to tray; process exits only explicit Exit.
- Multiple independent proxy listeners with bind IPv4/port/timeouts/max concurrency/state/RX/TX and Pause/Resume.
- Shared/dedicated L2TP lease model; first lease dial+verify, last release disconnect.
- Existing Windows profile + CustomEphemeral private `.pbk` modes.
- DPAPI-only password/PSK storage.
- Keepalive Off / PPP server IPv4 / CustomIPv4 with fail-closed threshold and reconnect while leases remain.
- Append-only JSONL daily logs with retention, no secrets/body/tunnel contents.
- No DIRECT fallback.
- Outbound TCP binds L2TP source IPv4 + `IP_UNICAST_IF`.
- Proxied DNS custom L2TP-bound only.
- Existing profile preflight L2TP+split-tunnel; default-route before/after/continuous guard.
- Lifecycle `Disconnected -> Dialing -> Verifying -> Ready`; no usable context before Ready.
- L2TP-bound HTTPS verification; fixed expected IPv4 equality.
- L2TP loss cancels dependent sessions.
- HTTPS CONNECT, no MITM.
- Accepted proxy sessions drain before higher runtime releases L2TP lease.
- Latency/throughput and bounded whole-process memory are first-class requirements; no production forced GC.

## Implemented blocks

- WinForms/tray/settings.
- Multi-proxy runtime and shared/dedicated lease manager.
- Existing Windows L2TP profile enumeration/validation, current/all-user handling.
- Custom ephemeral RAS phonebook + DPAPI + native Windows PSK/create/cleanup smoke test.
- RAS assigned IPv4/PPP server IPv4/interface/DNS discovery.
- Source address + interface socket binding.
- Route guards and L2TP-bound HTTPS verification.
- Custom DNS UDP/TCP/CNAME/bounded TTL cache.
- HTTP forward proxy + CONNECT.
- `ArrayPool<byte>` pumps, bounded session admission, deterministic shutdown drain.
- Runtime traffic/ping metrics, append-only logs, bounded latest L2TP status GUI/backend.
- `VpnContext` deterministic ownership.
- Per-RAS-session monitor CTS/task ownership; stale monitor cannot hang up replacement handle.
- Selective reconfigure exact identity preservation.
- Runtime start/reconfigure cancellation reconciliation.
- Process memory health snapshot and lifecycle/collectability stress tests.

## Recent hardening evidence

- canonical listener uniqueness parses `IPAddress`, so aliases like `127.1`/`127.0.0.1` collide correctly;
- 250 selective reconfigure cycles preserve independent-group object identity;
- recorded retained replacements: `ProxyInstanceRuntime` 0/250, `VpnLeaseManager` 0/250;
- incremental CRLFCRLF search is boundary-safe and avoids repeated prefix rescans;
- shutdown drain test proves `ProxyServer.RunAsync` completes accepted session cleanup before return.

## Issue #14 — HTTP framing / request smuggling

Production code in `f9db53f...` validates framing before outbound connect, allows one valid non-negative decimal Content-Length, rejects duplicate/conflicting/comma-list CL and any Transfer-Encoding, treats no CL as zero body, rejects initial remainder beyond CL, forwards exactly CL bytes, rejects early EOF, preserves valid CL and leaves CONNECT unchanged.

`ProxyHttpFramingSelfTests` is wired into the runner. Timing methodology is now corrected in `ceb591c...` and response-before-reset semantics in `d1f9edd...`; #14 stays open until exact-current-head Windows CI is green through the framing suite.

## Issue #15 — transactional proxy startup ownership

Confirmed lifecycle bug pending implementation: `ProxyInstanceRuntime.StartAsync` can publish `_lease/_runCancellation/_runTask`, then fail/cancel waiting for listener readiness without guaranteed exact run-task drain and field cleanup before lease release.

Required failed-start order:

`cancel exact run CTS -> await exact runTask drain -> clear same-generation fields -> dispose CTS -> release exact lease once`.

Preserve caller cancellation, safe retry, Pause/Dispose idempotence, no double-release/unobserved observer, unchanged successful Running path.

Planned testability seam is orchestration-only: injectable lease acquisition + server lifetime (`RunAsync`, `WaitUntilListeningAsync`); production networking chain remains unchanged.

## Issue map

Open: `#2, #4, #5, #6, #7, #11, #13, #14, #15`

Closed: `#1, #3, #8, #9, #10, #12`

## Immediate continuation order

1. Get exact-current-head Windows `build` and `handoff` runs executing normally.
2. Confirm corrected paired timing gate and full `ProxyHttpFramingSelfTests` suite green on that exact head; update/close #14 only after acceptance.
3. Implement #15 transactional startup ownership with deterministic fail/cancel/drain/retry/single-release tests.
4. Continue #11/#13 and real Windows #2/#4/#5/#6/#7 acceptance.
