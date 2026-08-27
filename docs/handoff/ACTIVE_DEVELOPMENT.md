# Active development — 2026-08-27

Live protected `main` is authoritative. Exact-head GitHub Actions is required before a source block is called accepted. Product invariants in `docs/requirements.md` remain mandatory: GUI/tray application, multiple proxy listeners, shared/dedicated L2TP leases, ExistingWindowsProfile + CustomEphemeral, DPAPI-only secrets, no DIRECT fallback, outbound source IPv4 + `IP_UNICAST_IF`, L2TP-bound DNS, split-tunnel/default-route guards, fail-closed lifecycle, CONNECT without MITM, 32 KiB transfer buffers, bounded memory/latency, no production forced GC, and accepted-session drain before VPN lease release.

## Last fully accepted source checkpoint

`fb0d2743b815204fe87eb7ba972513894b41f445` — `test: prove independent coordinator starts overlap`.

Windows build #531, run `33051006335`, job `98446224154`, completed successfully on `windows-latest`. Evidence smoke, restore/build, aggregate self-tests, self-contained win-x64 publish, binary identity manifest, ZIP and artifact upload all passed.

Artifact `9637611201`, digest:
`sha256:8042f82e1343f2f5133d85bbf7e576942cd2bd20857d0f286445404292ba5c55`.

Issues #14 (HTTP framing/request-smuggling boundary) and #15 (transactional proxy startup ownership) are closed as completed.

## Accepted runtime / ownership state

- Async RAS dial uses exact `HRASCONN` ownership; callback roots are removed only after Connected or proven `ERROR_INVALID_HANDLE` teardown.
- One RAS hangup/drain attempt is bounded; timeout never falsely marks the handle terminal and intentionally retains callback ownership for retry/process safety.
- RAS manager, VPN maintenance, host, coordinator, proxy runtime, GUI queue and memory monitor continue independent cleanup after cancellation callback faults while preserving the first failure.
- Native callback-root ownership now has process-wide current count + monotonic high-watermark diagnostics. Large sequential and concurrent churn returns current count to its pre-test baseline.
- Process memory snapshots include PID/start time, managed heap/allocation, working/private bytes, GC counts, native callback-root current/high watermark, handles and threads; only the latest snapshot is retained in memory.
- Proxy start is transactional: listener readiness is the publication boundary; rejected/cancelled generations drain exact run ownership before lease release.
- Pause/Dispose drains accepted sessions and exact run task before higher ownership releases the VPN lease.
- Shared/dedicated lease semantics, last-release disconnect, fail-closed shared invalidation and reconnect cooldown are deterministic.
- Enabled startup and selective restart candidates now execute concurrently when their proxy generations are independent. A failed group remains pending while an unrelated group can reach Running.
- Cleanup owners execute concurrently inside each dependency phase. All proxy owners complete before the VPN-manager phase begins; independent VPN managers then drain concurrently. Shared VPN serialization remains inside its single `VpnLeaseManager`.
- Cleanup errors are consumed in input order so primary/secondary diagnostics do not depend on scheduler completion order.

## GUI / persisted desired state (#5)

- All Add/Edit/Remove/Logging and Start/Pause actions use the strict FIFO GUI generation queue.
- Durable save is the persisted desired-state publication boundary; runtime apply failure never lies about what will load next start.
- Invalid legacy configuration can be repaired over multiple editor operations in an in-memory staged draft. No invalid partial generation reaches disk/runtime; the final repair publishes the complete accumulated generation.
- Logging and runtime are independent consumers of the same durable generation; a logging edit that completes the last invalid field also reconciles earlier staged proxy/VPN repairs.
- Caller cancellation remains control flow even if one persisted consumer also faults.
- Runtime grids project `desired ∪ actual`: desired-but-missing owners remain visible as Pending/Error and residual actual owners remain visible as cleanup drift.
- Explicit Exit stops admission, closes the owned modal editor, cancels/drains the config queue, waits the exact L2TP profile-helper task/process tree, then disposes runtime and process-memory monitoring.
- ExistingWindowsProfile profile enumeration owns its PowerShell process tree; CustomEphemeral settings keep DPAPI-only persisted secrets and clear managed UI/native carriers after handoff.

#5 remains open for real operator-facing Windows 11 GUI acceptance with actual profiles and live selective runtime effects.

## Evidence / soak (#2, #13)

Integration evidence supports Baseline -> Ready -> Final route/profile/interface/process captures, proxy/direct probes, exact running-executable SHA-256 and release-grade process lifecycle checks. CI artifacts include `ProxyToAnyConnect.exe.sha256` and `build-identity.json`.

Soak evidence is portable and manifest-protected. Collector/validator bind to exact PID, process start time and executable SHA, reject PID reuse, stream external working/private/handle/thread samples with bounded validator memory, and correlate `process.memory.startup/periodic` JSONL records with the same process lifetime. Hosted smoke proves mechanics only; it is not the real #13 acceptance.

Next evidence improvement: per-proxy expected public IPv4 assertions for shared/dedicated multi-proxy runs plus an explicit expected direct public IPv4. The current legacy `ExpectedVpnPublicIPv4` assumes every proxy endpoint has the same egress and is insufficient for heterogeneous dedicated groups.

## Release-critical real Windows acceptance (#2, #4, #5, #6, #7)

Use the exact self-contained CI artifact on Windows 11 x64 with real L2TP endpoint(s):

1. Baseline: no app process, route/profile fingerprints recorded.
2. Ready: exact executable SHA, default routes unchanged, selected L2TP verified, HTTP and HTTPS CONNECT proxy egress through L2TP, ordinary direct host egress independent.
3. Multi-proxy: distinct listeners; shared lease survives one proxy pause and disconnects after last release; dedicated/unrelated group stays independent.
4. Pause/Resume and selective edits affect only intended groups; failed independent start/teardown does not serialize unrelated groups.
5. Keepalive: PPP-server and CustomIPv4 modes; threshold -> context invalidation -> tunnel cancellation -> hangup -> cooldown -> full verification/reconnect while leases remain; no reconnect after last lease.
6. CustomEphemeral: no persistent Windows Settings profile, private PBK cleanup on normal exit, stale-session recovery after abnormal termination, no plaintext password/PSK in config/logs.
7. Final: no process remains, temporary resources cleaned, route/profile state satisfies baseline contract.
8. #13: 12–24h representative traffic/reconnect/reconfigure soak with native-root current/high-watermark and managed/external memory evidence.

## Immediate order

1. Extend #2 evidence for different expected egress IP per proxy endpoint and explicit direct-path expected IP, with hosted positive/negative smoke.
2. Run exact-head Windows CI for that evidence block and move the authoritative artifact forward only after full publish/upload success.
3. Continue deterministic long-run resource ownership/stress without adding work to the transfer hot path.
4. Execute real Windows 11 + L2TP endpoint acceptance for #2/#4/#5/#6/#7 and the documented 12–24h soak for #13 when the endpoint/environment is available.
