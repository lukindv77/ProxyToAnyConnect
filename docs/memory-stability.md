# Long-run memory stability

`ProxyToAnyConnect` is designed to run continuously for days or weeks. Memory and native-resource stability are therefore architectural requirements, not post-release tuning.

## Core invariant

Under a stable workload the process may fluctuate because of GC, socket buffers, Windows networking and active traffic, but repeated proxy sessions, L2TP reconnects, Pause/Resume operations and selective configuration reloads must not cause monotonic retained-memory or handle growth.

Production code must never force a full GC to hide retention problems.

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

Unchanged proxy/L2TP groups stay running and must not be duplicated during a selective reload.

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
- bounded DNS cache capacity/TTL/context reset.

Machine-specific absolute working-set thresholds should not be used as hard CI pass/fail gates. Tests should primarily verify ownership, bounded counts, collectability and absence of monotonic retained object graphs.
