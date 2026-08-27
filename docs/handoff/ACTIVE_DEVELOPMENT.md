# Active development — 2026-08-27

Live protected `main` is authoritative. Exact-head GitHub Actions is required before a source block is called accepted. Product invariants in `docs/requirements.md` remain mandatory: GUI/tray application, multiple proxy listeners, shared/dedicated L2TP leases, ExistingWindowsProfile + CustomEphemeral, DPAPI-only secrets, no DIRECT fallback, outbound source IPv4 + `IP_UNICAST_IF`, L2TP-bound DNS, split-tunnel/default-route guards, fail-closed lifecycle, CONNECT without MITM, 32 KiB transfer buffers, bounded memory/latency, no production forced GC, and accepted-session drain before VPN lease release.

## Last fully accepted substantive source checkpoint

`4b100f3bb6c744b08918ce122ab75982fa263740` — `evidence: support per-proxy and direct egress expectations`.

Windows build #534, run `33051353263`, completed successfully on `windows-latest`. Evidence smoke, restore/build, aggregate self-tests, self-contained win-x64 publish, binary identity manifest, ZIP and artifact upload all passed.

Artifact `9637762202`, digest:
`sha256:be01041fefa07c4fe4dd39f4a02e5c038b9e729b97049a7da4880d685aedf239`.

Issues #14 (HTTP framing/request-smuggling boundary) and #15 (transactional proxy startup ownership) are closed as completed.

## Accepted runtime / ownership state

- Async RAS dial uses exact `HRASCONN` ownership; callback roots are removed only after Connected or proven `ERROR_INVALID_HANDLE` teardown.
- One RAS hangup/drain attempt is bounded; timeout never falsely marks the handle terminal and intentionally retains callback ownership/exact handle for retry.
- RAS manager, VPN maintenance, host, coordinator, proxy runtime, GUI queue and memory monitor continue independent cleanup after cancellation callback faults while preserving the first failure.
- Native callback-root ownership has process-wide current count + monotonic high-watermark diagnostics. Large sequential and concurrent churn returns current count to baseline.
- Process memory snapshots include PID/start time, managed heap/allocation, working/private bytes, GC counts, native callback-root current/high watermark, handles and threads; only bounded current state is retained.
- Proxy start is transactional: listener readiness is the publication boundary; rejected/cancelled generations drain exact run ownership before lease release.
- Pause/Dispose drains accepted sessions and exact run task before higher ownership releases the VPN lease.
- Shared/dedicated lease semantics, last-release disconnect, fail-closed shared invalidation and reconnect cooldown are deterministic.
- Enabled startup and selective restart candidates execute concurrently when independent. A failed group remains pending while an unrelated group can reach Running.
- Cleanup owners execute concurrently inside each dependency phase. All proxy owners complete before VPN-manager phase; independent VPN managers then drain concurrently.
- Cleanup errors are consumed in input order so primary/secondary diagnostics do not depend on scheduler completion order.

## GUI / persisted desired state (#5)

- Add/Edit/Remove/Logging and Start/Pause all use the strict FIFO GUI generation queue.
- Durable save is the persisted desired-state publication boundary; runtime apply failure never lies about what will load next start.
- Invalid legacy configuration can be repaired over multiple editor operations in an in-memory staged draft. No invalid partial generation reaches disk/runtime; final repair publishes the complete accumulated generation.
- Logging and runtime are independent consumers of the same durable generation; a logging edit that fixes the final invalid field also reconciles earlier staged proxy/VPN repairs.
- Caller cancellation remains control flow even if one persisted consumer also faults.
- Runtime grids project `desired ∪ actual`: desired-but-missing owners remain visible as Pending/Error and residual actual owners remain visible as cleanup drift.
- Explicit Exit stops admission, closes the owned modal editor, cancels/drains the config queue, waits the exact L2TP profile-helper task/process tree, then disposes runtime and process-memory monitoring.
- ExistingWindowsProfile enumeration owns its PowerShell process tree; CustomEphemeral settings keep DPAPI-only persisted secrets and clear managed UI/native carriers after handoff.

#5 remains open for real operator-facing Windows 11 GUI acceptance with actual profiles and live selective runtime effects.

## Evidence / soak (#2, #13)

Integration evidence supports Baseline -> Ready -> Final route/profile/interface/process captures, proxy/direct probes, exact running-executable SHA-256 and release-grade process lifecycle checks. CI artifacts include `ProxyToAnyConnect.exe.sha256` and `build-identity.json`.

Latest collector work at `4b100f3...` adds:

- backward-compatible default expected proxy public IPv4;
- optional expected public IPv4 per proxy endpoint for heterogeneous shared/dedicated groups;
- optional expected direct-host public IPv4 to prove ordinary host traffic remains independent of the proxied L2TP route.

The next development pass must verify this new contract is enforced end-to-end by `Invoke/Test/Complete-WindowsIntegrationEvidence.ps1` and hosted positive/negative smoke, not merely captured by the collector.

Soak evidence is portable and manifest-protected. Collector/validator bind to exact PID, process start time and executable SHA, reject PID reuse, stream external working/private/handle/thread samples with bounded validator memory, and correlate `process.memory.startup/periodic` JSONL records with the same process lifetime. Hosted smoke proves mechanics only; it is not real #13 acceptance.

## Release-critical real Windows acceptance (#2, #4, #5, #6, #7)

Use the exact self-contained CI artifact on Windows 11 x64 with real L2TP endpoint(s):

1. Baseline: no app process, route/profile fingerprints recorded.
2. Ready: exact executable SHA, default routes unchanged, selected L2TP verified, per-proxy expected public egress proven, ordinary direct host egress independently proven.
3. Multi-proxy: distinct listeners; shared lease survives one proxy pause and disconnects after last release; dedicated/unrelated group stays independent.
4. Pause/Resume and selective edits affect only intended groups; failed independent start/teardown does not serialize unrelated groups.
5. Keepalive: PPP-server and CustomIPv4 modes; threshold -> context invalidation -> tunnel cancellation -> hangup -> cooldown -> full verification/reconnect while leases remain; no reconnect after last lease.
6. CustomEphemeral: no persistent Windows Settings profile, private PBK cleanup on normal exit, stale-session recovery after abnormal termination, no plaintext password/PSK in config/logs.
7. Final: no process remains, temporary resources cleaned, route/profile state satisfies baseline contract.
8. #13: 12–24 h representative traffic/reconnect/reconfigure soak with native-root current/high-watermark and managed/external memory evidence.

## Handoff packaging state

`docs/handoff/NEW_CHAT_PROMPT.md`, `CURRENT_STATE.md`, `AUDIT_SNAPSHOT.md`, `ISSUES_SNAPSHOT.md`, `FINAL_CI_STATUS.md`, `HANDOFF_INDEX.md` and `MANIFEST.md` are refreshed for the new chat. `.github/workflows/handoff.yml` now archives `src/tests/tools/docs/.github`, README/solution, exact build info, the last 120 commits and `START_HERE.txt`, with 90-day retention.

## Immediate order

1. Verify the final handoff-document head with exact-head `build` + `handoff` Actions and use the latest handoff artifact whose embedded SHA matches that head.
2. Complete/verify per-proxy and direct expected-egress assertions across the whole evidence toolchain and positive/negative hosted smoke.
3. Continue deterministic long-run resource ownership/stress without adding work to the transfer hot path.
4. Execute real Windows 11 + L2TP endpoint acceptance for #2/#4/#5/#6/#7 and the documented 12–24 h soak for #13 when the endpoint/environment is available.
