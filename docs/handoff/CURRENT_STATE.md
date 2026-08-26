# ProxyToAnyConnect — current handoff state

> Prepared 2026-08-26 for continuation in a new ChatGPT conversation. Live GitHub `main` is authoritative. The next chat must fetch the current head, issue comments and exact-head Actions before changing code.

## Snapshot identity

- Repository: `lukindv77/ProxyToAnyConnect` (private)
- Branch: `main`
- Platform/runtime: Windows 11 x64, C# / .NET 10 `net10.0-windows`, WinForms + tray
- Production HTTP-framing commit: `f9db53f074d6740296e46452077622099b6f64ff`
- Timing-test commit: `71a93e5d529225adfd0e1b5125a4302d81c58da5`
- First refreshed handoff docs commit: `b3fbe1f96c0ffa7d031cb72b81793ec6ea9c2858`

## Exact known CI at the refreshed handoff commit

### handoff #84 / run `32982263807`

**SUCCESS.** GitHub Actions artifact:

`ProxyToAnyConnect-handoff-b3fbe1f96c0ffa7d031cb72b81793ec6ea9c2858`

Artifact id `9611924335`, size 211286 bytes, SHA-256:

`5b9307c6a184f3a6bf4ddc47b60af6569ea4a3611940f7cb7d9b527eaa72aa6b`

### build #272 / run `32982263806`

Compilation succeeded with 0 warnings / 0 errors and the suite progressed through the setup timing gate.

Important passes before failure:

- selective reconfigure/cancellation/lifetime tests;
- 250 selective reconfigure cycles: proxy retained 0, L2TP retained 0;
- incremental header terminator scan;
- parser/origin allocation guard;
- **proxy setup paired timing guard PASS**: parser `1999 vs 2042 ns/op = 0.98x`, origin `973 vs 1222 ns/op = 0.80x`;
- CONNECT syntax-only parser setup guard.

Current failure:

`ProxyHttpFramingSelfTests.ExactContentLengthBoundsClientToOriginBytesAsync`

`ReadToEndAsync` receives `IOException` with inner Windows `SocketException 10054` (connection forcibly closed by remote host) while reading the proxy response.

Therefore the current code baseline is **not green**, but the old timing blocker is not the immediate problem anymore. The suite now reaches #14 framing tests.

## Immutable architecture/product rules

- Always GUI; form `X` hides to tray; process exit only explicit Exit.
- Multiple independent proxy listeners with bind IPv4/port/timeouts/max concurrency/state/RX/TX and Pause/Resume.
- Shared/dedicated L2TP lease model; first lease dial+verify, last release disconnect.
- Existing Windows profile + CustomEphemeral private `.pbk` modes.
- Password/PSK protected by user-bound Windows DPAPI only.
- Keepalive Off / PPP server internal IPv4 / CustomIPv4 with fail-closed threshold/reconnect.
- JSONL append-only daily logs with retention and no secrets/body/tunnel contents.
- No DIRECT fallback.
- Outbound TCP uses source L2TP IPv4 `Bind()` + `IP_UNICAST_IF`.
- Proxied DNS is custom L2TP-bound DNS, never `System.Net.Dns`.
- Existing profile must be L2TP + split tunnel; default IPv4 route guarded before/after dial and continuously.
- Lifecycle `Disconnected -> Dialing -> Verifying -> Ready`; no usable context before Ready.
- Real L2TP-bound HTTPS verification; fixed expected public IPv4 must match.
- VPN loss cancels dependent active sessions.
- HTTPS via CONNECT, no MITM.
- `ProxyServer.RunAsync` drains accepted sessions before higher runtime may release L2TP lease.
- Performance/latency/throughput and bounded whole-process memory are first-class constraints; no production forced GC.

## Major implemented blocks

- WinForms/tray lifecycle and settings UI.
- Multi-proxy runtime, shared/dedicated `VpnLeaseManager`, independent Pause/Resume.
- Existing Windows L2TP profile enumeration/validation and current/all-user handling.
- Custom ephemeral RAS phonebook + DPAPI secrets + Windows native phonebook/PSK/cleanup smoke test.
- RAS client IPv4, PPP server IPv4, interface index and DNS discovery.
- Source address + interface socket binding.
- Split-tunnel/default-route guards.
- L2TP-bound HTTPS verification.
- L2TP-bound DNS UDP/TCP fallback/CNAME/bounded TTL cache.
- Plain HTTP forward proxy + CONNECT.
- `ArrayPool<byte>` transfer buffers and bounded session admission.
- Deterministic accepted-session shutdown drain.
- Traffic counters and rolling ping metrics.
- Append-only logs and retention.
- Bounded latest L2TP status registry + GUI status/reason.
- Deterministic `VpnContext` ownership.
- Per-RAS-session monitor CTS/task ownership; stale monitor cannot hang up replacement handle.
- Selective reconfigure exact identity preservation for unrelated groups.
- Runtime start/reconfigure cancellation reconciliation.
- Process memory health latest snapshot and lifetime stress tests.

## Recent hardening results

- listener collision validation uses parsed `IPAddress`, so equivalent textual IPv4 representations cannot bypass uniqueness;
- 250 selective-reconfigure cycles preserve unrelated object identity;
- recorded retained replacements: `ProxyInstanceRuntime` 0/250, `VpnLeaseManager` 0/250;
- HTTP header delimiter search is incremental/boundary-safe;
- current setup timing self-test uses paired alternating measurement and passes on build #272.

## Issue #14 — HTTP framing/request-smuggling

Code implemented in `f9db53f...`:

- strict single non-negative decimal `Content-Length`;
- reject duplicates/conflicts/comma lists;
- reject any Transfer-Encoding/TE+CL;
- no CL => body length zero;
- reject header-read remainder beyond CL before outbound connect;
- forward exactly CL bytes;
- never forward trailing/pipelined/smuggled bytes on same origin connection;
- early EOF fails;
- valid CL preserved;
- CONNECT unchanged.

New `ProxyHttpFramingSelfTests` is wired into `CombinedTestRunner`.

Current exact Windows failure occurs in the exact-CL smuggling-boundary scenario while the test client reads the proxy response and sees socket reset 10054. Likely hypotheses to verify, not assume:

- closing the proxy client while malicious trailing bytes remain unread may cause Windows to send RST even after a valid origin response;
- proxy may instead be closing too early before complete response;
- test may incorrectly require clean EOF rather than reading/verifying a known response before reset.

The invariant that bytes after CL never reach origin must not be weakened. #14 remains open until the test and behavior are made deterministic and exact-head Windows CI is green through framing coverage.

## Issue #15 — transactional proxy startup ownership

Confirmed pending lifecycle bug: `ProxyInstanceRuntime.StartAsync` can publish `_lease`, `_runCancellation`, `_runTask`, then fail/cancel in `WaitUntilListeningAsync` and cleanup without first awaiting exact run-task drain or clearing already-published fields.

Required failure order:

`cancel exact run CTS -> await exact runTask drain -> clear same-generation fields -> dispose CTS -> release exact lease once`.

Preserve caller cancellation, safe retry, Pause/Dispose idempotence, no double release/unobserved observers and successful Running behavior.

Planned test seam is orchestration-only: injectable lease acquisition + server lifetime (`RunAsync`, `WaitUntilListeningAsync`). Production networking chain remains unchanged.

## Issue map at snapshot

Open: `#2, #4, #5, #6, #7, #11, #13, #14, #15`

Closed: `#1, #3, #8, #9, #10, #12`

#2 remains the mandatory real Windows 11 + real L2TP E2E gap. #4/#5/#6/#7 retain real-environment acceptance. #11 and #13 remain ongoing performance/memory/lifetime programs.

## Immediate continuation order

1. Fetch live current head and exact Actions; confirm no later commits.
2. Reproduce/audit build #272 framing failure in `ExactContentLengthBoundsClientToOriginBytesAsync`.
3. Preserve exact Content-Length smuggling boundary; determine reset-vs-response semantics and make test/production behavior deterministic.
4. Get exact-head Windows CI through `ProxyHttpFramingSelfTests`; update/close #14 only after semantic + performance gates pass.
5. Implement #15 transactional startup ownership with deterministic fail/cancel/drain/retry/single-release tests.
6. Continue broad #11/#13 and real Windows acceptance work.
