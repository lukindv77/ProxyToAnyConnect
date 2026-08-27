# Active development — 2026-08-27

Live protected `main` is authoritative. Exact-head GitHub Actions is required before a source block is called accepted. The repository is public; the `main` ruleset restricts updates/deletions/force-pushes and permits bypass only through repository administration and the OpenAI ChatGPT Codex Connector used for this development stream.

## Last fully accepted checkpoint

`a922ba0a8406515b36edb88a6ade749e171f42e5` — `test: cover bounded RAS hangup drain timeout`.

Windows build #417 (`33038130831`) completed successfully on `windows-latest`:

- PowerShell tool AST validation: PASS;
- hosted `Baseline -> Ready -> Final` evidence lifecycle smoke: PASS;
- restore/build: PASS;
- aggregate self-tests: PASS;
- self-contained win-x64 publish: PASS;
- ZIP and artifact upload: PASS.

Artifact `9632821353`, GitHub artifact digest:
`sha256:2b8d8095b53f26ddc7c6019f3d0246bae102b9846167087433dccc3ddf29f4e2`.

Issues #14 (HTTP framing/request-smuggling boundary) and #15 (transactional proxy startup ownership) are closed as completed and must not be treated as pending work.

## Runtime/lifecycle blocks completed before the current head

Major deterministic ownership/fail-closed hardening now includes:

- asynchronous RAS dial with exact `HRASCONN` ownership and caller cancellation;
- callback delegate roots retained until Connected or proven `ERROR_INVALID_HANDLE` teardown;
- failed RAS teardown keeps residual exact handle retryable instead of losing native ownership;
- one RAS hangup/status-drain attempt is bounded to 10 seconds; timeout does not falsely declare the handle terminal;
- RAS monitor, VPN maintenance, runtime host and coordinator teardown continue draining owners even if cancellation callbacks throw;
- first cleanup failure remains primary while secondary cleanup defects are attached diagnostically;
- `ProxyInstanceRuntime` start and pause/shutdown are transactional: exact run task drains before VPN lease release;
- initial desired enabled starts remain pending after transient/cancelled startup and can reconcile on the same config;
- same-config reconfigure detects missing runtime topology and recreates missing proxy/VPN generations;
- shared/dedicated VPN lease ownership, last-lease disconnect, shared fail-closed invalidation and reconnect cooldown are deterministic;
- reconnect maintenance waits out known cooldown instead of repeatedly invoking `ConnectAsync` and emitting failure/log churn;
- Windows profile enumeration owns and terminates its PowerShell helper process tree on cancellation;
- CustomEphemeral private RAS sessions use live exclusive ownership locks and recover only proven stale managed session directories;
- managed dial password and PSK carriers are cleared immediately after their native handoff boundaries;
- accepted-client HTTP transport has one terminal socket owner and strict request-framing boundaries;
- logging replacement is fail-soft/transactional, and process/static diagnostic owners are explicitly shut down.

## Current unaccepted source head — binary evidence and settings durability

The following commits are newer than the last fully accepted checkpoint and require a new exact-head Windows verdict:

- `f6e35e7...` — integration process evidence captures SHA-256 of the running `ProxyToAnyConnect.exe` without persisting its path;
- `0e492b7...` — aggregate acceptance can require an expected executable SHA-256 and process lifecycle;
- `f3e0cbe...` — CI publishes `ProxyToAnyConnect.exe.sha256` plus `build-identity.json` and smoke-tests exact-binary validation;
- `962c754...` — configuration saving uses a unique sibling temp file, a final cancellation publication boundary and best-effort owned-temp cleanup;
- `470756d...` / `460b365...` — deterministic configuration-persistence regressions and aggregate runner wiring;
- `fc7d06b...` — fixes empty process-array materialization exposed by Windows build #423;
- `4255902...` — documents exact executable identity as part of release-grade Windows acceptance.

Build #423 on `460b365...` failed before .NET only because PowerShell function output converted an empty process array into `$null`; StrictMode then rejected `.Count`. AST validation and all three raw stage captures passed. `fc7d06b...` fixes that exact shape by materializing each process set through `@(...)` and passing scalar counts to stage summaries.

## Release-critical real Windows acceptance (#2, #4, #5, #6, #7)

Synthetic/hosted CI cannot close these product acceptance boundaries. The next real Windows 11 x64 run must use a real L2TP endpoint and the exact self-contained CI artifact.

Required high-level evidence:

1. Baseline before application start: no ProxyToAnyConnect process, route/profile fingerprints captured.
2. Ready: exactly one process whose executable SHA-256 matches the artifact `build-identity.json`; HTTP/HTTPS proxy egress uses the expected L2TP public IPv4; direct host egress remains independent; default routes remain unchanged.
3. Multi-proxy: shared lease remains connected while any dependent proxy runs; last release disconnects; dedicated unrelated group is isolated.
4. Keepalive: internal-server and CustomIPv4 targets, threshold invalidation, active tunnel cancellation, hangup/cooldown/reconnect/full verification, and no reconnect after last lease.
5. CustomEphemeral: no persistent Windows VPN profile, private PBK cleanup on normal exit, stale-session recovery after abnormal termination, no plaintext credential material in config/logs.
6. Final after explicit application Exit: no process remains; route/profile state returns to baseline as required.
7. Aggregate `Complete-WindowsIntegrationEvidence.ps1` must pass with `-ExpectedExecutableSha256` and `-RequireProcessLifecycle` in addition to real proxy probes.

## Performance and long-run stability (#11, #13)

Permanent constraints remain:

- no production forced GC;
- no unbounded queues/history/task registries;
- 32 KiB pooled transfer buffers remain unchanged;
- no optimization may weaken source-IP bind, `IP_UNICAST_IF`, custom DNS or fail-closed routing;
- cleanup hardening must not add work to steady-state packet/buffer forwarding;
- hosted timing/allocation gates remain required;
- real 12–24+ hour soak should track working set/private bytes, managed heap, handles, threads, reconnect cycles, Pause/Resume and selective reconfigure.

## Immediate order

1. Obtain exact-head Windows CI after `fc7d06b...` / `4255902...`; fix any evidence/build/self-test failure before declaring the new blocks accepted.
2. Finish settings desired-state consistency: after durable save succeeds, the GUI must show the persisted desired config even if runtime apply fails; do not silently present the previous in-memory settings as if the write rolled back.
3. Update issues #2/#5/#11/#13 with exact commits and CI evidence.
4. Run release-grade Windows 11 + real L2TP acceptance using the artifact executable SHA-256 contract.
5. Continue residual-resource/reconfigure soak hardening while preserving hot-path latency constraints.
