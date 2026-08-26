# Active development — 2026-08-26

Live `main` remains authoritative. This file records the currently active multi-block development stream; exact-head GitHub Actions is required before an item is called accepted.

## Current source head before this note

- `9df03fedd74e52efe17110250057284cd1627520` — `fix: make accepted client close bounded and response-safe`.
- Timing methodology fix: `ceb591cf5bf62c43dc08a4611744acf445fa7286`.
- Framing response-before-close test: `d1f9edd7dbbaaeba2ebd858b9565507f159754e4`.

## Exact CI evidence that drove the current work

Build #276 on `a0d5e71a905d2fe18e66747233182db62fd2416d`:

- restore/build succeeded with 0 warnings and 0 errors;
- corrected proxy setup timing guard passed with unchanged 1.25x policy: parser paired `0.93x`, origin paired `0.93x`;
- CONNECT setup passed;
- `ProxyHttpFramingSelfTests` still failed on Windows with WSAECONNRESET 10054 while `ReadFramedResponseAsync` was still reading the response header. This proves the defect is not merely a clean-EOF expectation after a complete response.

The Windows close-path fix in `9df03f...` changes only accepted-client teardown. When unread bytes are already queued locally, it performs send-side shutdown, discards only already-buffered client bytes locally with a hard 64 KiB cap, then closes. It never forwards those bytes to origin, never waits for more attacker data, and does not alter L2TP DNS/socket routing, CONNECT pumps, parser semantics or 32 KiB transfer buffers.

Issue #14 remains open until exact-head Windows CI passes the timing and HTTP-framing suites.

## Transactional startup block (#15)

A deterministic orchestration-only patch is being prepared. The production network chain remains:

`VpnLeaseManager -> L2tpDnsResolver -> L2tpSocketFactory -> ProxyServer`.

The stronger ownership model is to keep a start attempt's lease, CTS and run task local until listener readiness succeeds. A rejected attempt therefore never publishes stale runtime ownership. Required cleanup is:

`cancel exact run CTS -> await exact run task drain -> dispose CTS -> release exact lease once`.

Caller cancellation must remain cancellation and leave the runtime retryable. Listener/readiness failure must leave coherent Error state. Successful Running behavior, Pause/Dispose idempotence, shared-L2TP accounting and observer behavior must remain unchanged.

Planned deterministic tests cover:

- caller cancellation after run creation;
- readiness/listener failure;
- blocked run drain proving the VPN lease is not released early;
- stale-field absence after rejection;
- safe retry to Running;
- Pause twice / Dispose after pause without double lease release;
- repeated rejected-start collectability cycles, with forced GC only in tests.

## Performance / memory block (#11, #13)

The lifecycle work is also a long-run retention boundary:

- no production forced GC;
- no unbounded client close drain;
- no session-history/task registry;
- no transfer-buffer reduction or extra steady-state data-path copy;
- rejected start attempts must not retain lease/CTS/run/observer/server objects;
- ProcessMemoryHealthMonitor remains latest-snapshot-only in memory with periodic append-only JSONL for history.

## Product/runtime acceptance blocks (#4, #5, #6, #7, #2)

Source audit confirms the major UI/runtime mechanisms are already present: independent proxy Start/Pause controls and state snapshots, shared/dedicated lease accounting, selectable local bind IPv4, interactive Windows L2TP profile list/refresh, CustomEphemeral configuration, and keepalive modes. After #14/#15 lifecycle correctness is green, these issues should be driven by their remaining exact Windows/real-endpoint acceptance rather than reimplementing existing mechanisms.

## Immediate order

1. Obtain exact-head Windows CI for the bounded close-path fix and finish/close #14 only on green evidence.
2. Land transactional startup ownership plus deterministic lifecycle/collectability tests for #15/#13.
3. Run exact-head full Windows CI and fix any later suite exposed after framing/startup gates.
4. Continue performance/memory hardening (#11/#13) and close implementation-complete product blocks only when their remaining Windows acceptance is evidenced.
5. Keep issue threads and handoff docs synchronized after each material source/CI result.
