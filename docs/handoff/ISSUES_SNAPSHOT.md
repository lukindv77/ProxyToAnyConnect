# ProxyToAnyConnect — GitHub issues snapshot

Snapshot taken during the new-chat handoff preparation on 2026-08-26. **The next chat must still query GitHub because issue state/comments may change after this snapshot.**

## Closed

- **#1 — Enforce split-tunnel RAS profile before dialing** — closed/completed. Existing-profile L2TP/split-tunnel preflight, default-route before/after guard, provisional verification and L2TP socket binding were implemented.
- **#3 — GUI lifecycle: tray-first WinForms application with explicit Exit only** — closed/completed.
- **#8 — restore green CI after GUI/multi-proxy refactor** — closed/completed.
- **#9 — Daily append-only L2TP logs with monthly folders and retention** — closed/completed. GUI-configurable root/retention, `YYYY-MM/YYYY-MM-DD.jsonl`, append-only writes and cleanup are implemented/tested.

## Open

### #2 — Windows 11 integration test with real L2TP endpoint

**Highest external-validation priority.**

Requires real Windows 11 x64 + real L2TP endpoints. Acceptance includes:

- RAS client/server IP and interface detection;
- unchanged host default routes;
- verification/expected public egress;
- HTTP and CONNECT traffic;
- unrelated apps stay on ordinary route;
- fail-closed active tunnel loss;
- shared and dedicated multi-proxy lease scenarios;
- Pause last/shared lease behavior;
- keepalive failure/reconnect;
- custom ephemeral connection and cleanup;
- structured test logs/snapshots.

CI cannot substitute for this issue.

### #4 — Multi-proxy runtime with shared/dedicated L2TP leases and Pause/Resume

Open. Much of the implementation is already present:

- multiple proxy runtimes;
- shared/dedicated lease manager;
- independent Pause/Resume;
- last-lease disconnect;
- fail-closed shared-group behavior architecture;
- selective runtime reconfiguration groundwork.

Before closing, inspect current code against every acceptance item, especially unique enabled bind endpoint validation and independent-group behavior under real failure/reconfigure.

### #5 — Settings UI for proxy instances, Windows L2TP selection and timeouts

Open. Substantially implemented, including proxy/L2TP add/edit/delete dialogs, interactive Windows L2TP enumeration and controlled/selective runtime reload.

Remaining acceptance must be checked on current head, especially:

- selecting bind IPv4 from actual local interfaces rather than free text where required;
- completeness of all timeout fields;
- unsafe-combination validation;
- L2TP last status/error visibility;
- selective reload isolation.

### #6 — Custom ephemeral L2TP with protected credentials and private temporary RAS phonebook

Open.

Implemented:

- ExistingWindowsProfile vs CustomEphemeral model;
- DPAPI protected password/PSK;
- private temporary `.pbk` builder;
- Windows native smoke test creates L2TP entry + PSK + cleanup;
- integration with `RasConnectionManager` common `RasDial -> Verifying -> Ready` path;
- cleanup ownership architecture.

Still requires real external L2TP endpoint/Windows 11 validation before closure, including actual auth/encryption combinations and persistent-profile absence/cleanup after real hangup.

### #7 — L2TP keepalive with internal-server/custom IPv4 targets and automatic reconnect

Open.

Implementation is present for Off / PPP internal server IPv4 / CustomIPv4, source-bound ICMP, interval/timeout/failure threshold, rolling RTT, fail-closed teardown and maintenance reconnect while active leases exist.

Still requires real L2TP validation and final GUI diagnostic/status acceptance.

### #10 — Runtime proxy/L2TP traffic counters and 5-minute ping metrics in GUI

Open.

Implemented:

- per-proxy live RX/TX from data pumps;
- aggregate L2TP payload RX/TX;
- rolling successful keepalive RTT average over 5 minutes;
- Proxies and L2TP GUI metrics columns;
- regression tests.

The issue body explicitly says the remaining item before closing is a dedicated L2TP GUI status/details field for latest keepalive/verification/error information. This overlaps #12.

### #11 — Performance and memory: low-latency proxy path and efficient process-wide memory use

Open/ongoing architectural goal.

Important invariant: scope is the entire process. Implemented work includes pooled proxy/DNS buffers, reduced header copies, bounded session admission, bounded shared DNS cache, low-churn GUI refresh, bounded background/runtime state and performance regressions.

Newest permanent requirement: **memory optimization must not increase proxy processing/forwarding latency, jitter or reduce throughput beyond repeatable measurement noise.** See `docs/requirements.md` and `docs/memory-stability.md`.

### #12 — L2TP runtime diagnostics: meaningful GUI status and last fail-closed reason

Open.

Backend work already present:

- bounded latest-status registry;
- one latest status per VPN ID;
- max 256 entries;
- replaces status in place;
- stale status removed when VPN runtime is disposed;
- self-test coverage.

Acceptance still requires checking/finishing the L2TP grid `Status / reason` column and exposing verification, keepalive, reconnect cooldown and fail-closed reasons without retaining event history in memory.

### #13 — Long-run memory stability: deterministic ownership and process memory health

Open/ongoing audit goal.

Implemented/audited items include:

- deterministic `VpnContext` lifetime/ref counting;
- collectability tests;
- latest-only process memory-health snapshot;
- bounded status/cache/metrics state;
- proxy shutdown drain;
- tracked/joined proxy runtime observer;
- runtime/lease semaphore-disposal race hardening;
- per-RAS-session monitor CTS/task ownership and stale-handle protection;
- stress tests for proxy lifecycle and monitor/context cleanup.

Permanent acceptance constraint: memory hardening must preserve or improve repeatable latency/throughput; memory-only regressions to proxy data path are rejected.

## Current open issue set at snapshot

`#2, #4, #5, #6, #7, #10, #11, #12, #13`

## Current closed issue set at snapshot

`#1, #3, #8, #9`
