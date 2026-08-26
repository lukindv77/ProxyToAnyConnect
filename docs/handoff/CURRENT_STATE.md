# ProxyToAnyConnect — current handoff state

> Snapshot prepared 2026-08-26 for moving development to a new ChatGPT conversation. **Live GitHub `main` remains authoritative.** The next chat must still fetch the current head, issues/comments and Actions status before changing code.

## Snapshot baseline

- Repository: `lukindv77/ProxyToAnyConnect` (private)
- Branch: `main`
- Platform: Windows 11 x64
- Runtime: C# / .NET 10 `net10.0-windows`
- GUI: WinForms + tray
- Code head immediately before this handoff-doc commit: `71a93e5d529225adfd0e1b5125a4302d81c58da5` (`test: stabilize proxy setup timing guard`)
- Its parent production framing commit: `f9db53f074d6740296e46452077622099b6f64ff` (`fix: enforce plain HTTP request framing`)

## Exact known CI state before final handoff docs

### `71a93e5d...`

- build #271 / run `32979967766`: **FAILED** in self-tests.
- Compilation succeeded with 0 warnings / 0 errors.
- Failure: `ProxySetupTimingSelfTests` text-span parser median `5859 ns/op` vs immediate predecessor `3350 ns/op` = `1.75x`, limit `1.25x`.
- This commit changed only benchmark warmup/sample size (`2048` warmup, `32768` ops/round); the 1.25x policy threshold was not weakened.
- handoff #83 / run `32979967788`: **SUCCESS**, source handoff archive uploaded.

### `f9db53f...`

- build #270 / run `32979190505`: **FAILED** at the same setup-timing guard (`5322` vs `3206 ns/op`, `1.66x`, limit `1.25x`).
- Compilation and all earlier self-tests succeeded.
- New HTTP framing suite was later in the runner and therefore did not execute before the timing failure.

Do not describe the current code baseline as green until a later exact-head build proves it.

## Product / immutable architecture

ProxyToAnyConnect exposes one or more local HTTP/HTTPS forward proxy listeners. Every proxy routes only through its selected L2TP runtime. Domain selection is external (for example PAC). There is no DIRECT fallback.

Hard invariants:

- always GUI; `X` hides to tray; only explicit Exit terminates the process;
- multiple independent proxy instances with bind IPv4/port/timeouts/max concurrency/state/RX/TX and independent Pause/Resume;
- shared/dedicated L2TP lease model;
- first lease dials/verifies, last lease disconnects;
- ExistingWindowsProfile and CustomEphemeral private `.pbk` modes;
- DPAPI-only storage for password/PSK;
- keepalive Off / PPP internal server IPv4 / CustomIPv4;
- JSONL append-only logs with retention and no secrets/body/tunnel contents;
- HTTPS CONNECT only, no MITM;
- proxy traffic never DIRECT;
- all outbound sockets bind source L2TP IPv4 + `IP_UNICAST_IF`;
- proxied DNS is custom L2TP-bound DNS, never `System.Net.Dns`;
- split-tunnel existing-profile preflight and default-route before/after/continuous guard;
- lifecycle `Disconnected -> Dialing -> Verifying -> Ready`, with context hidden until Ready;
- L2TP-bound HTTPS verification and expected-public-IP equality when configured as IPv4 literal;
- VPN loss cancels dependent active sessions;
- accepted proxy sessions drain before the higher-level runtime may release its L2TP lease;
- performance/latency/throughput and bounded whole-process memory are equal first-class constraints.

## Major implemented subsystems

### GUI/config/runtime

- WinForms main form + tray lifecycle.
- Proxy and L2TP add/edit/remove settings flows.
- Multiple configured `Proxies[]` and `VpnConnections[]`.
- Per-proxy runtime state and metrics.
- L2TP latest status/reason GUI field backed by bounded `VpnLatestStatusRegistry`.
- Selective reconfigure: only changed VPNs and their dependent proxies are recreated; independent groups retain exact runtime object identity.
- Canonical enabled listener collision validation uses parsed `IPAddress`, so textual aliases like `127.1` and `127.0.0.1` cannot bind the same endpoint unnoticed.

### L2TP/RAS

- Existing Windows L2TP profile enumeration and split-tunnel validation.
- Current-user/all-user phonebook handling.
- Custom ephemeral private phonebook creation/cleanup without persistent Windows Settings profile.
- Windows DPAPI protection of custom secrets.
- Native RAS device/entry/PSK smoke tests on Windows runner.
- RAS client IPv4, PPP server IPv4, interface index and DNS discovery.
- Per-RAS-session monitor CTS/task ownership: disconnect cancels and joins the exact old monitor; stale monitor cannot hang up a replacement handle.
- Default-route snapshot guard before/after dial and continuous while Ready.
- Keepalive/reconnect architecture and bounded reconnect status diagnostics.

### VPN context/network fail-closed

- `VpnContext` lifetime/ref-count ownership so active outbound sessions retain a disconnected context until they close; final release disposes CTS deterministically.
- Outbound TCP socket source address + interface binding.
- L2TP-bound DNS UDP with TCP fallback, CNAME handling and bounded TTL cache scoped to current VPN context.
- HTTPS verification through L2TP-bound sockets.

### Proxy server

- Plain HTTP forwarding and bidirectional CONNECT.
- `ArrayPool<byte>` transfer buffers; no full tunnel buffering.
- Per-proxy `maxConcurrentConnections` admission before Accept; Windows backlog supplies backpressure.
- Accepted-session drain before `RunAsync` returns.
- Incremental header terminator search starting no earlier than 3 bytes before newly appended data, avoiding repeated whole-prefix rescans under fragmented headers.
- Hop-by-hop/proxy header handling.

### HTTP framing hardening now in code — issue #14

Commit `f9db53f...` changed plain HTTP framing to fail closed:

- framing parsed before outbound connection;
- exactly one valid non-negative decimal `Content-Length` accepted;
- duplicate/conflicting/comma-list CL rejected;
- any `Transfer-Encoding` rejected (including TE+CL) until decode/re-encode exists;
- no CL => zero body;
- header-read remainder cannot exceed declared body length;
- body forwarding is bounded to exactly CL bytes;
- later pipelined/smuggled bytes are not forwarded to the origin;
- early client EOF before CL completes fails the session;
- valid CL is preserved in rewritten origin header;
- CONNECT path remains opaque and unchanged.

New `ProxyHttpFramingSelfTests` covers parser framing, pre-outbound rejection, exact-body forwarding, smuggling/trailing-byte boundary and early EOF scenarios. `CombinedTestRunner` invokes it, but current timing gate fails earlier, so exact Windows execution of this new suite still needs to be reached.

## Performance/memory work already completed

- pooled data-path buffers;
- bounded header storage and incremental delimiter scan;
- reduced parser/origin header allocations with self-tests;
- bounded DNS cache and optimized DNS parsing/materialization paths;
- bounded latest-status registry;
- stable in-place GUI row updates;
- process memory-health latest snapshot only;
- no in-memory log history;
- deterministic observer/monitor/context cleanup;
- production never forces GC.

Important measured regression evidence from previous green runs includes:

- 250 selective reconfigure cycles preserved independent-group runtime identities;
- retained replaced `ProxyInstanceRuntime`: 0/250;
- retained replaced `VpnLeaseManager`: 0/250;
- proxy lifecycle stress and shutdown-drain tests passed;
- CONNECT throughput numbers are diagnostic only and are not hard product thresholds.

## Latest audit finding — issue #15

`ProxyInstanceRuntime.StartAsync` currently has an ownership hole during startup failure/cancellation after `ProxyServer.RunAsync` has been created and instance fields assigned.

Current sequence includes:

1. acquire VPN lease;
2. create run CTS and proxy server;
3. start `runTask`;
4. assign `_lease`, `_runCancellation`, `_runTask`;
5. await `WaitUntilListeningAsync(cancellationToken)`.

If step 5 throws/cancels, current catch can cancel/dispose local resources and dispose the lease without first awaiting exact `runTask` completion and without clearing already assigned ownership fields. Risks:

- stale disposed `_lease` / `_runCancellation` fields;
- still-cleaning `_runTask` retained in runtime;
- release of last L2TP lease before listener/session drain completes;
- retry/observer interaction and potential double-cleanup complexity.

Issue #15 acceptance requires startup as a transactional generation ownership unit: cancel run -> await exact run drain -> clear matching fields -> dispose CTS -> release exact lease once; cancellation propagation, safe retry, idempotent Pause/Dispose and no unobserved observer task.

Planned test seam: internal injectable lease acquisition and proxy-server lifetime abstraction (`RunAsync`/`WaitUntilListeningAsync`) only at orchestration layer. Production constructor continues using the existing real VPN/DNS/socket/proxy chain; no hot-path or routing semantics should change.

## Current roadmap issue state at snapshot

Open:

- #2 real Windows 11 + real L2TP E2E;
- #4 multi-proxy/shared-dedicated acceptance;
- #5 settings/selective reload real-environment acceptance;
- #6 custom ephemeral real endpoint validation;
- #7 keepalive/reconnect real endpoint validation;
- #11 performance/memory ongoing;
- #13 long-run memory/resource stability ongoing;
- #14 HTTP framing/request-smuggling hardening — code present, exact acceptance blocked by timing gate;
- #15 transactional startup ownership — audit/acceptance recorded, implementation pending.

Closed:

- #1, #3, #8, #9, #10, #12.

## First work in the next chat

1. Fetch live head/actions/issues and confirm no commit appeared after this handoff.
2. Inspect `ProxySetupTimingSelfTests` and current `ParsedProxyRequest.Parse` together. Do not merely widen the 1.25x limit.
3. Determine why current parser measures 1.66–1.75x slower than its immediate-predecessor benchmark after HTTP framing bookkeeping. Fix production code or benchmark comparability as justified by source-level evidence.
4. Run exact-head Windows CI until the suite reaches and executes `ProxyHttpFramingSelfTests`; resolve any framing correctness/performance failures.
5. Update/close #14 only after exact-head semantic and performance gates pass.
6. Implement #15 transactional startup ownership with deterministic failure/cancel/drain/retry/single-release tests.
7. Continue broader #11/#13 hardening and real Windows #2/#4/#5/#6/#7 acceptance work.

## Handoff archive

`.github/workflows/handoff.yml` packages the exact checked-out commit into `ProxyToAnyConnect-handoff-<sha>` and includes `src`, `tests`, `docs`, `.github`, README, solution and `HANDOFF_BUILD_INFO.txt`. Start the new chat with `docs/handoff/NEW_CHAT_PROMPT.md` from the latest handoff artifact or live `main`.
