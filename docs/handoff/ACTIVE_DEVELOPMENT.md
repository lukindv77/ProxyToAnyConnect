# Active development — 2026-08-27

Live protected `main` is authoritative. Exact-head GitHub Actions is required before a source block is called accepted. Product invariants in `docs/requirements.md` remain mandatory: GUI/tray application, multiple proxy listeners, shared/dedicated L2TP leases, ExistingWindowsProfile + CustomEphemeral, DPAPI-only secrets, no DIRECT fallback, outbound source IPv4 + `IP_UNICAST_IF`, L2TP-bound DNS, split-tunnel/default-route guards, fail-closed lifecycle, CONNECT without MITM, 32 KiB transfer buffers, bounded memory/latency, no production forced GC, and accepted-session drain before VPN lease release.

## Last fully accepted checkpoint

`b545487e224514f72325e4424c736109f8561f53` — `docs: define Windows long-run soak evidence procedure`.

Windows build #453, run `33041167245`, job `98414864141`, completed successfully on `windows-latest`:

- PowerShell AST validation: PASS;
- hosted `Baseline -> Ready -> Final` integration evidence smoke: PASS;
- exact executable/process identity smoke: PASS;
- Windows soak collector/validator smoke: PASS;
- restore/build: PASS with 0 warnings / 0 errors;
- aggregate self-tests: PASS;
- self-contained win-x64 publish: PASS;
- binary identity manifest / ZIP / artifact upload: PASS.

Artifact `9633936004`, GitHub artifact digest:
`sha256:45dc87dc6a04d4397211c075d6502af943b9420dc2febc6e2fbb5bed6421e133`.

The substantive source/tooling checkpoint immediately below it is `07c452e951b9d28300e89a067f84acb7d381aec5`, independently green in build #452, run `33041071447`, job `98414572426`. #453 re-ran the same source plus the soak-procedure documentation and remained green.

Issues #14 (HTTP framing/request-smuggling boundary) and #15 (transactional proxy startup ownership) are closed as completed. Do not reopen or treat them as pending without a new regression.

## Runtime and native ownership blocks already accepted

Major deterministic fail-closed/lifetime work includes:

- asynchronous RAS dial with exact `HRASCONN` ownership and caller cancellation;
- callback roots retained until Connected or proven native teardown;
- failed RAS cleanup retains retryable exact-handle ownership instead of silently losing it;
- RAS manager monitor/fail-closed paths, VPN maintenance, runtime host and coordinator teardown continue draining owners even when cancellation callbacks throw;
- first cleanup failure remains primary while secondary cleanup defects are retained diagnostically;
- native ICMP keepalive uses asynchronous completion and deterministic native-operation cleanup;
- `ProxyInstanceRuntime` start is transactional and rejected attempts drain the exact run task before lease release;
- Pause/Dispose drains accepted sessions and exact run ownership before releasing the VPN lease;
- interrupted enabled starts remain pending and same-config reconciliation repairs missing runtime topology;
- shared/dedicated leases, last-release disconnect, shared fail-closed invalidation and reconnect cooldown are deterministic;
- Windows VPN profile enumeration owns/cancels its helper process tree;
- CustomEphemeral private RAS phonebooks use exclusive ownership locks, stale-session recovery and deterministic PBK/session cleanup;
- managed password/PSK carriers are cleared after native handoff boundaries;
- accepted-client close preserves complete responses with bounded unread-tail discard;
- HTTP request framing is fail-closed against body/tail smuggling;
- DNS caches, rolling ping/status diagnostics, logging state and process-memory monitoring are explicitly bounded.

## Exact-binary Windows integration evidence (#2)

Release artifacts now contain:

- `ProxyToAnyConnect.exe`;
- `ProxyToAnyConnect.exe.sha256`;
- `build-identity.json` with Git commit and executable SHA-256.

Ready integration evidence captures SHA-256 of the running `ProxyToAnyConnect.exe` without persisting its path. Aggregate completion can require both `-ExpectedExecutableSha256` and `-RequireProcessLifecycle`, enforcing the release-grade process sequence `Baseline=0 -> Ready=1 exact binary -> Final=0` in addition to route/profile/proxy assertions.

Hosted CI exercises this exact-binary branch synthetically. Real Windows 11 + real L2TP evidence is still required before #2 closes.

## Configuration durability and GUI desired state (#5)

Accepted current behavior:

- `AppOptions.SaveAsync` writes through a unique sibling temporary generation, flushes before publication, performs a final cancellation boundary and best-effort owned-temp cleanup;
- failed/cancelled save does not damage the previously published config generation;
- ExistingWindowsProfile and the complete CustomEphemeral config schema round-trip in deterministic persistence tests;
- durable publication is the GUI desired-state linearization point: after save succeeds, the new persisted `AppOptions` remains authoritative even when live runtime reconciliation fails;
- the runtime grid projection is `saved desired ∪ actual runtime`:
  - desired proxy/VPN missing from the current runtime remains visible as Pending/Error instead of disappearing or appearing Running;
  - residual runtime removed from desired config remains visible as cleanup drift and is non-actionable;
- all mutating GUI configuration commands are serialized as strict FIFO generations across the whole editor -> durable save -> desired publication -> runtime apply transaction;
- a log-only save captures requested log values but merges them with the freshest persisted proxy/VPN topology when its serialized turn begins, preventing stale full-config overwrite;
- failed/cancelled GUI generations release the serialization tail so later generations cannot wedge;
- explicit Exit stops accepting new config generations, cancels active/queued generations, drains the exact queue tail, then disposes the runtime host and process-memory monitor;
- queue shutdown retains cancellation-callback cleanup defects while still draining ownership;
- application shutdown continues independent cleanup owners after an earlier cleanup fault.

Build #452 PASS lines include:

- `GUI runtime projection exposes desired-missing and residual actual topology without false Running state`;
- `GUI configuration commands serialize strict generations, recover after failure/cancellation and drain shutdown ownership through callback faults`;
- `application shutdown drains GUI configuration generations before runtime and continues independent cleanup after faults`.

#5 remains open because operator-facing Windows 11 GUI acceptance with actual VPN profile enumeration, editing and live selective runtime effects is still required.

### Remaining GUI shutdown UX edge

Ownership is safe, but an explicit Exit issued while a custom settings editor is currently inside modal `ShowDialog` can wait for that dialog to return before the serialized command observes queue cancellation. A future UX hardening pass may explicitly cancel/close the owned configuration editor when Exit begins. Do not weaken the current drain-before-runtime-destroy ordering to make Exit appear faster.

## Long-run stability and soak evidence (#11, #13)

Permanent constraints remain:

- no production forced GC or working-set trimming;
- no unbounded queues/history/task registries;
- no memory optimization may add synchronization/copies/serialization to the steady-state byte-transfer loop;
- 32 KiB transfer buffers remain unchanged unless repeatable latency/throughput evidence justifies an intentional requirements change;
- source-IP bind, `IP_UNICAST_IF`, custom DNS and fail-closed routing must never be weakened for memory reasons.

New tooling:

- `tools/Invoke-WindowsSoakEvidence.ps1` attaches to one exact PID, verifies process name/start time and executable SHA-256, rejects process exit/PID reuse, and writes append-only working-set/private-bytes/handle/thread samples while retaining only scalar aggregates;
- `tools/Test-WindowsSoakEvidence.ps1` validates schema, manifest byte lengths/SHA-256, exact binary/process identity, sample sequence/timestamps and requested minimum sample/duration;
- `docs/windows-soak-evidence.md` defines 12h/24h release procedure and correlation with application `process.memory.startup` / `process.memory.periodic` JSONL records for managed heap, allocation and GC counts;
- hosted CI smoke-runs the collector/validator against the current `pwsh` process only to validate tooling mechanics. It is explicitly not a substitute for the real multi-hour Windows 11/L2TP soak.

Build #452 soak smoke collected three exact-process samples over ~1.99 seconds and validated the bundle. The same run preserved the existing allocation/timing guards, 250 selective-reconfigure bounded-retention cycles, 250 listener/session lifecycle cycles and pooled CONNECT throughput (~671.6 MiB/s in that runner).

## Release-critical real Windows acceptance (#2, #4, #5, #6, #7)

Synthetic/hosted CI cannot close these product acceptance boundaries. Use the exact self-contained CI artifact on Windows 11 x64 with a real L2TP endpoint.

Required high-level evidence:

1. Baseline before application start: no ProxyToAnyConnect process; route/profile fingerprints captured.
2. Ready: exactly one running process whose executable SHA-256 equals `build-identity.json`; HTTP/HTTPS proxy egress uses the expected L2TP path; direct host egress remains independent; default routes remain unchanged.
3. Multi-proxy: shared lease remains connected while any dependent proxy runs; last release disconnects; dedicated/unrelated groups remain isolated.
4. Pause/Resume and selective config edits affect only intended proxy/L2TP groups and the GUI continues to expose desired/runtime/error state correctly.
5. Keepalive: `VpnServerInternalIPv4` and `CustomIPv4`, threshold invalidation, active tunnel cancellation, exact RAS teardown, reconnect cooldown and full verification before traffic resumes; no reconnect after last lease.
6. CustomEphemeral: no persistent Windows VPN profile, private PBK cleanup on normal Exit, stale-session recovery after abnormal termination, no plaintext secrets in config/logs.
7. Final after explicit Exit: no process remains; managed VPN/temporary resources are gone; route/profile state returns to baseline as required.
8. `Complete-WindowsIntegrationEvidence.ps1` passes with exact executable SHA and process lifecycle requirements plus real external/proxy probes.
9. For #13, run representative 12-24h traffic/reconnect/reconfigure soak using the new exact-binary soak tools and retain matching application memory-health/lifecycle logs.

## Immediate order

1. Use artifact `9633936004` (or a later fully green exact-head artifact) for real Windows 11 + L2TP integration acceptance across #2/#4/#5/#6/#7.
2. Run the documented 12-24h exact-binary soak for #13 under representative CONNECT/HTTP, reconnect, shared/dedicated, Pause/Resume and selective-reconfigure workload.
3. Harden explicit Exit UX so an open custom configuration editor is cancelled/closed without weakening exact config-queue drain ordering.
4. Continue real keepalive failure/reconnect acceptance and CustomEphemeral endpoint validation; deterministic hosted coverage does not replace the endpoint run.
5. Preserve performance gates and investigate any memory/resource growth through ownership/collectability first; never mask it with production GC or hot-path cleanup.
