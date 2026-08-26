# ProxyToAnyConnect — current handoff state

> Prepared 2026-08-26. Live GitHub `main` is authoritative; the new chat must fetch the current head, live issue comments and exact-head Actions before coding.

## Snapshot identity

- Repository: `lukindv77/ProxyToAnyConnect` (private)
- Branch: `main`
- Windows 11 x64 / C# / .NET 10 `net10.0-windows` / WinForms + tray
- HTTP framing production commit: `f9db53f074d6740296e46452077622099b6f64ff`
- setup timing test commit: `71a93e5d529225adfd0e1b5125a4302d81c58da5`
- handoff status-doc commits observed during packaging: `b3fbe1f96c0ffa7d031cb72b81793ec6ea9c2858`, `b304a4331b8527b8280396047d3c649cfaed80f3`

## Important CI fact: timing gate is currently non-reproducible on hosted runners

Current `ProxySetupTimingSelfTests` source uses paired alternating current/predecessor measurement, warmup 2048, 9 rounds, 32768 iterations/round and unchanged `MaxMedianSlowdownRatio = 1.25`.

With no production/parser code change between docs-only heads:

- build #272 on `b3fbe1f...`: timing PASS — parser `1999 vs 2042 ns/op = 0.98x`, origin `973 vs 1222 = 0.80x`;
- build #273 on `b304a433...`: timing FAIL — parser `5218 vs 2917 ns/op = 1.79x`, limit 1.25x.

Therefore the next chat must treat this as benchmark-methodology instability until proven otherwise. Do not simply widen 1.25x and do not change production parser solely to appease one noisy runner. Make compared work equivalent and measurement stable.

## Important CI fact: when timing passes, framing suite exposes a real close/reset issue

Build #272 reached `ProxyHttpFramingSelfTests` and failed `ExactContentLengthBoundsClientToOriginBytesAsync`:

- `ReadToEndAsync` on the proxy client response threw `IOException`;
- inner Windows `SocketException 10054`: connection forcibly closed by remote host.

This must also be resolved. Strict Content-Length smuggling boundary must remain: bytes after declared CL never reach origin.

Hypotheses to verify:

- Windows RST may be produced because proxy closes its client socket with intentionally unread malicious trailing bytes after forwarding exactly CL bytes;
- proxy may instead close before a complete origin response;
- test may incorrectly require clean EOF after a complete response in a deliberate trailing-byte attack case.

Prefer evidence from deterministic stream/response framing tests; do not blindly drain and forward/discard extra client bytes if that adds latency or weakens request boundary semantics.

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

`ProxyHttpFramingSelfTests` is wired into the runner. #14 stays open until both timing gate methodology and the framing reset test are stable/green on exact-head Windows CI.

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

1. Fetch live current head/actions/issues.
2. Stabilize/make honest `ProxySetupTimingSelfTests` without merely widening 1.25x; use #272 PASS vs #273 FAIL on docs-only heads as evidence of hosted-runner variance.
3. Then reproduce/audit framing SocketException 10054; preserve exact CL boundary and make response/close test deterministic.
4. Get exact-head Windows CI green through `ProxyHttpFramingSelfTests`; update/close #14 only after acceptance.
5. Implement #15 transactional startup ownership with deterministic fail/cancel/drain/retry/single-release tests.
6. Continue #11/#13 and real Windows #2/#4/#5/#6/#7 acceptance.
