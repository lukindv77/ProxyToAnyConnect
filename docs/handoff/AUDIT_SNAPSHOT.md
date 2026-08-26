# ProxyToAnyConnect — technical audit snapshot

This document preserves the important engineering/audit findings reached during the current development conversation. It is not a replacement for code review of the current `main`; it records why several architecture decisions exist and which risks have already been found/fixed.

## 1. Routing / fail-closed audit

### Finding: source-IP binding alone was not considered sufficient

An early 3proxy-style design using only `external=<VPN IPv4>` was rejected as the final architecture because Windows route selection could still be ambiguous and it did not provide a strong application-level fail-closed contract.

Current design uses **both**:

- local source IPv4 `Bind()` = dynamically assigned L2TP client IPv4;
- `IP_UNICAST_IF` = selected L2TP interface index.

This is per-socket and therefore does not alter routing for unrelated applications.

### Finding: `RasDial` can violate the unrelated-app routing requirement

A Windows VPN entry with remote default gateway/full tunnel can modify system routing as soon as it is dialed. This was treated as a critical pre-dial risk.

Mitigations implemented:

- existing profile must be L2TP;
- existing profile must be split tunnel before `RasDial`;
- default IPv4 route snapshot captured before dial;
- snapshot compared immediately after dial;
- continuous default-route monitor while VPN is Ready;
- mismatch is fail-closed and leads to hangup.

The split-tunnel preflight issue (#1) was completed/closed.

### Finding: verification must occur before publishing VPN context

`RasDial` success is not sufficient proof that proxy traffic will take the expected external path.

The state machine therefore separates physical RAS connection from availability to proxy code:

`Disconnected -> Dialing -> Verifying -> Ready`

`Current`/usable context is published only at Ready. The verifier uses the same L2TP-bound socket mechanics as the proxy.

### Public egress rule

If configured public egress is an IPv4 literal, the external probe must observe that exact IPv4.

If the configured public address is a DNS name, only validation steps inherently dependent on a fixed expected public IPv4 are skipped. The L2TP-bound connectivity probe and other route/binding checks remain required.

## 2. DNS audit

### System DNS rejected for proxy destinations

`System.Net.Dns` would not provide a sufficiently strong guarantee that name resolution leaves through the selected L2TP. The proxy uses a custom DNS resolver bound to the VPN context.

Implemented:

- IPv4 A queries;
- UDP via source-IP + interface binding;
- DNS truncation handling;
- DNS-over-TCP fallback through the same L2TP;
- CNAME following and loop protection;
- bounded per-L2TP cache shared by proxies on the same shared L2TP;
- DNS TTL expiration;
- context identity reset: old DNS answers are not reused after reconnect to a new `VpnContext`.

### Memory/performance finding

The original UDP receive path allocated a ~65 KiB array per query. It was reduced to a pooled smaller receive buffer with TCP fallback when necessary. This was done specifically to lower allocation pressure without weakening the L2TP routing invariant.

## 3. RAS / P/Invoke audit

### ABI issues were tested on Windows, not assumed from successful compilation

Important lesson: successful .NET compilation does not validate native RAS structure layout.

A real GitHub Windows runner smoke test initially produced Win32 error 632 (incorrect structure size) in `RasEnumDevices`.

The marshaling was corrected (including RAS structure packing/value-type array handling) and the native ephemeral phonebook test then passed on Windows.

### Private ephemeral L2TP approach

Windows RAS dialing still needs a phonebook entry. To satisfy the requirement that custom L2TP not create a persistent Windows Settings VPN profile, the project uses a separate temporary private `.pbk`.

The native Windows smoke test verifies:

- find L2TP RAS device;
- create private phonebook;
- create L2TP entry;
- set PSK credentials;
- construct dial parameters;
- remove entry/private directory.

This path is now integrated with common `RasConnectionManager`, but a real external custom L2TP endpoint remains an integration gap.

### Existing-profile scope finding

Current User and All User VPN entries are different phonebook scopes. The code explicitly handles AllUserConnection/global phonebook where needed rather than assuming `phoneBook=null` is always sufficient.

## 4. RAS/session lifecycle audit

### Finding: stale monitor lifetime across Disconnect/reconnect

Earlier `RasConnectionManager` monitor tasks were tied mainly to application shutdown, so an old session monitor could outlive a normal `DisconnectAsync`. A rapid resume/reconnect could leave old monitor state alive while a new RAS session existed.

Fix implemented:

- one dedicated monitor CTS/task per RAS session;
- before a new connection, completed/old monitor state is joined/disposed;
- explicit Disconnect removes current context/handle, cancels and joins the session monitor, then releases session resources;
- old monitor can hang up only if its RAS handle is still atomically the current handle;
- old monitor cannot arm fail-closed behavior against a replacement session after explicit disconnect.

This change passed Windows Actions run #181 before final handoff docs.

## 5. Proxy lifecycle / concurrency audit

### Unbounded session tasks rejected

Per-proxy `maxConcurrentConnections` was added. Admission acquires a bounded slot before Accept, so when all slots are in use the application stops accepting into user space and relies on the Windows TCP backlog instead of creating unlimited Tasks/CTS/buffers.

### Finding: cancellation alone was not deterministic shutdown

For Pause/reconfigure/Exit, merely cancelling session tokens did not prove all accepted sessions had completed cleanup before releasing a VPN lease.

Current shutdown drain reuses the bounded semaphore:

- stop accepting/cancel sessions;
- then acquire the full `maxConcurrentConnections` permit count;
- this can complete only after every active session has returned its permit;
- only after `ProxyServer.RunAsync` finishes can higher-level runtime release the L2TP lease.

No separate unbounded Task registry is required.

### Finding: fire-and-forget proxy completion observer race

`ProxyInstanceRuntime` previously launched a completion observer fire-and-forget. During Dispose/reconfigure, the owner could dispose coordination state while the observer was still pending.

Fix:

- observer task is tracked;
- Pause/Dispose joins it outside the runtime gate;
- no forgotten observer is left holding old runtime state;
- semaphore-disposal races were avoided.

## 6. Memory-stability audit

Project requirement is **whole-process memory stability**, not only memory per connection.

### `VpnContext` ownership

A context can be referenced by manager + live outbound sessions. Immediate CTS disposal on manager disconnect could be too early; never disposing it could retain resources.

Implemented ref-count ownership:

- manager owns one reference;
- each live outbound connection retains/releases a context reference;
- `MarkDisconnected` cancels lifetime and releases manager ownership;
- final reference release deterministically disposes the context CTS.

Self-tests repeatedly create and release many contexts and use forced GC only in test code to verify collectability.

### Bounded runtime structures

Explicitly bounded/current-state-only structures include:

- concurrent proxy sessions;
- L2TP DNS cache;
- 5-minute ping window;
- GUI rows per configured entity;
- latest process-memory snapshot;
- latest L2TP status registry (max 256, one latest record per VPN ID);
- no in-memory log history.

Stale L2TP latest-status entries are removed when the corresponding runtime is disposed.

### L2TP maintenance task lifecycle

An earlier version left an L2TP maintenance `PeriodicTimer` task alive after its last lease had disappeared. It was changed so maintenance exists only while active proxy consumers exist.

### GUI allocation churn

An earlier GUI implementation cleared/recreated all DataGridView rows once per second. It was changed to stable-ID in-place updates to reduce long-run GUI allocation/GC churn.

### Process memory diagnostics

A monitor captures current:

- managed heap;
- cumulative allocations;
- working set;
- private bytes;
- GC collection counts;
- handles;
- threads.

Only the latest immutable snapshot is retained. Periodic observations are written to append-only disk log instead of kept as an in-memory time series.

## 7. Memory vs latency audit rule

Memory optimization is explicitly **not allowed to make the proxy slower**.

Rejected classes of memory optimization include, if they measurably worsen the data path:

- global locks;
- synchronous waits;
- extra copy/serialization stages;
- per-packet/per-buffer objects;
- forced GC/working-set trimming;
- reducing transfer-buffer sizes merely to lower working set when it increases syscalls or reduces throughput.

Memory-only changes must preserve repeatable latency/tail-latency/jitter and throughput within measurement noise. If footprint and latency conflict, choose bounded/predictable memory with the faster data path rather than the absolute minimum working set.

## 8. Proxy hot-path performance work already done

- `ArrayPool<byte>` for steady-state data pumps;
- no full tunnel buffering;
- request header acquisition uses pooled/growing buffers rather than `MemoryStream` plus redundant full copies;
- atomic byte counters;
- shared per-L2TP DNS cache avoids duplicate resolver state for proxies using one shared VPN;
- multi-megabyte CONNECT regression test exercises pooled-buffer reuse and data integrity;
- no hard throughput threshold in noisy CI, but observed throughput can be logged diagnostically and regressions should use repeatable before/after measurements.

## 9. Logging audit

Daily JSONL logs are append-only. Earlier concern about keeping a file open made ordinary readers fail under Windows file-sharing combinations in tests. Current design opens append/write/share, writes one record, flushes and closes.

Properties:

- no read-modify-write of the log;
- current file can be inspected/copied while application runs;
- retention cleanup scheduled at most daily by log date;
- cleanup/log failures must never alter fail-closed routing;
- secrets and tunnel bodies are excluded.

## 10. Current validation boundary

What GitHub CI can validate well:

- C#/.NET 10 build;
- Win32 API ABI smoke tests available on Windows runner;
- native route APIs;
- private RAS phonebook creation/cleanup;
- DPAPI;
- loopback proxy behavior;
- DNS parser/cache;
- ownership/collectability/stress;
- self-contained packaging.

What it cannot validate without a real endpoint/environment:

- successful authentication to the user's actual L2TP server;
- real Windows 11 route/interface behavior for that VPN endpoint/profile;
- public egress verification against the real expected IP/domain;
- actual PPP server internal IP availability for keepalive;
- real PSK/certificate/auth/encryption combinations;
- network behavior across real L2TP reconnect/failure events.

Therefore a green CI does **not** close the real Windows 11 E2E issue.

## 11. Handoff audit checklist for the next chat

Before continuing implementation:

1. Fetch latest `main` and latest GitHub Actions status.
2. Confirm whether the handoff workflow/artifact is green.
3. Inspect open issues and their newest comments.
4. Check whether Issue #12's latest-status backend has already been wired into the L2TP GUI table in commits after this snapshot.
5. Check current `RasConnectionManager` implementation against this document; do not accidentally remove per-session monitor ownership.
6. Run/inspect tests after any lifecycle change.
7. Preserve the explicit latency-neutral memory optimization rule.
