# Long-run memory stability

`ProxyToAnyConnect` is designed to run continuously for days or weeks. Memory and native-resource stability are therefore architectural requirements, not post-release tuning.

## Core invariant

Under a stable workload the process may fluctuate because of GC, socket buffers, Windows networking and active traffic, but repeated proxy sessions, L2TP reconnects, Pause/Resume operations and selective configuration reloads must not cause monotonic retained-memory or handle growth.

**Memory optimization must not increase proxy data-path latency, latency jitter, or reduce sustained throughput.** This requirement has the same architectural priority as bounded memory use. A smaller working set is not an improvement if it makes request handling or byte forwarding slower.

Production code must never force a full GC to hide retention problems.

## Latency-preserving memory optimization

Memory-hardening changes must preserve the fast path from accepted client bytes to the selected L2TP-bound outbound socket.

The following rules apply:

- do not introduce global locks, synchronous waits or blocking coordination on the proxy transfer hot path to reduce memory;
- do not add extra byte-array/string copies, serialization, object materialization or per-buffer/per-packet allocations as a memory-saving technique;
- do not shrink transfer buffers merely to reduce working set when that increases socket/system-call frequency or reduces throughput;
- prefer pooling/reuse only when it reduces allocation/GC pressure without increasing contention or retaining an excessive pool working set;
- prefer bounded ownership and deterministic cleanup over aggressive reclamation techniques that interrupt active traffic;
- forced GC, working-set trimming and similar latency-spiking techniques are forbidden in production;
- diagnostics, cleanup, retention and memory monitoring must run outside the byte-transfer critical path;
- when two designs are functionally equivalent, prefer the design with bounded/predictable memory and lower forwarding latency rather than the design with the minimum possible memory footprint.

A memory optimization is rejected if repeatable benchmarks show a proxy processing/forwarding latency regression, increased latency jitter, or reduced sustained throughput beyond measurement noise and the regression is caused solely by the memory optimization. Any intentional exception requires an explicit project-requirements change rather than being accepted implicitly during implementation.

## Ownership rules

Every disposable/native resource must have one explicit lifecycle owner:

- `ProxyApplicationContext` owns application-lifetime diagnostics and tray resources.
- `ProxyRuntimeHost` / `ProxyRuntimeCoordinator` own configured runtime objects.
- `ProxyInstanceRuntime` owns a proxy listener lifetime and its active L2TP lease.
- `VpnLeaseManager` owns the shared/dedicated L2TP manager while the configuration exists.
- `RasConnectionManager` owns the current RAS session and the manager reference to its `VpnContext`.
- each live outbound L2TP connection holds one explicit reference to its `VpnContext`.
- the last context reference deterministically disposes its `CancellationTokenSource`.
- custom ephemeral L2TP phonebooks are owned by exactly one RAS session and removed when that session ends.
- pooled transfer/DNS buffers are returned in `finally` blocks.

A resource must not rely on finalization as its normal cleanup path.

## Bounded state

The following structures must remain explicitly bounded:

- proxy concurrent sessions (`maxConcurrentConnections` per proxy);
- L2TP-scoped DNS cache (fixed capacity + DNS TTL);
- rolling ping metrics (five-minute window only);
- GUI rows (one per configured proxy/L2TP);
- process-memory diagnostics (latest in-memory snapshot only);
- log state (current append operation only; historical data lives on disk).

No in-memory event log, completed-session history, reconnect history or unbounded task registry is permitted.

## Reconnect and fail-closed lifecycle

When an L2TP context becomes invalid:

1. its lifetime token is cancelled;
2. the manager-owned context reference is released exactly once;
3. active proxy sessions close and release their connection references;
4. the context CTS is disposed immediately after the final reference is released;
5. the old context becomes unreachable and collectible before/while a later reconnect creates a new context.

A reconnect must never retain the previous `VpnContext`, temporary phonebook, monitor timer or completed monitor task through a growing collection.

## Pause / Resume and reconfiguration

Pause and selective reconfiguration must release, as applicable:

- listener sockets;
- active session cancellation sources/tasks;
- L2TP leases;
- unused L2TP maintenance timers/tasks;
- replaced proxy/L2TP runtime objects;
- DNS cache contents tied to an obsolete VPN context;
- temporary RAS resources.

Unchanged proxy/L2TP groups stay running and must not be duplicated or recreated during a selective reload. A proxy-only edit must leave unchanged L2TP runtime objects intact. An L2TP edit must replace only that L2TP runtime and its dependent proxy runtimes; independent groups retain the same runtime objects and therefore keep their existing listener/VPN lifetimes.

Cleanup performed by Pause/Resume or reconfiguration must not add avoidable work to active byte-transfer loops. Teardown may wait for deterministic session cancellation/drain, but ordinary forwarding must remain free of cleanup-oriented synchronization.

## GUI and diagnostics

GUI refresh must update stable rows in place and must not recreate the full view every timer tick.

Process memory health diagnostics retain only the latest immutable snapshot in memory and periodically append scalar measurements to the existing JSONL log. Current diagnostics include:

- managed heap bytes;
- total allocated bytes since process start;
- working set;
- private bytes;
- Gen0/Gen1/Gen2 collection counts;
- Windows handle count;
- process thread count.

The tray command `Состояние памяти...` captures an on-demand snapshot without retaining history.

## Regression strategy

Self-tests may force GC only to verify collectability. Production code must not.

Regression coverage should include:

- deterministic `VpnContext` reference release with many active session references;
- thousands of released contexts becoming collectible after forced test GC;
- memory monitor timer/task disposal and collectability;
- repeated proxy session admission/teardown;
- repeated Pause/Resume and selective reconfigure cycles;
- long CONNECT transfers using pooled buffers;
- bounded DNS cache capacity/TTL/context reset;
- repeatable latency/throughput checks around memory-sensitive changes.

The selective-reconfiguration regression exercises two independent disabled proxy/L2TP groups without requiring a real RAS endpoint. It verifies exact runtime-object identity for the unaffected group, verifies that changed proxy/VPN runtimes are replaced, and runs 250 repeated reconfiguration cycles while retaining only weak references to replaced `ProxyInstanceRuntime` and `VpnLeaseManager` instances. After test-only forced collection, retained replaced runtimes must be bounded by a small fixed async/JIT-root allowance rather than scale with the number of cycles. This guards both isolation and long-run object-graph retention without adding production instrumentation or data-path work.

For performance-sensitive memory changes, compare before/after behavior under the same workload. At minimum, watch connection/request processing latency, sustained CONNECT throughput and allocation/GC behavior. Where practical, record p50/p95/p99 latency so an apparent average improvement cannot hide increased tail latency.

Machine-specific absolute working-set thresholds should not be used as hard CI pass/fail gates. Tests should primarily verify ownership, bounded counts, collectability and absence of monotonic retained object graphs, while performance checks must reject memory changes that measurably worsen the proxy data path beyond normal benchmark noise.
