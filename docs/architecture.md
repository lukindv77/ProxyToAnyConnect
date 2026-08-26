# ProxyToAnyConnect architecture

## Goal

ProxyToAnyConnect is a Windows 11 x64 WinForms application that exposes one or more local HTTP/HTTPS forward-proxy listeners. Each proxy instance references one configured L2TP connection and sends its outbound traffic exclusively through the verified runtime context of that L2TP connection.

The application is not a system-wide VPN client. Applications that do not use a ProxyToAnyConnect listener must continue to use their normal Windows network path. There is no DIRECT fallback inside ProxyToAnyConnect.

`docs/requirements.md` is the product source of truth. This document describes the current implementation architecture.

## Runtime topology

Persistent configuration contains two independent catalogs:

- `Proxies[]` — listener/runtime settings;
- `VpnConnections[]` — L2TP settings.

Each proxy has a stable ID, bind IPv4/port, timeouts, `maxConcurrentConnections`, enabled state and one referenced L2TP ID.

Each L2TP configuration is either:

- **shared** — multiple running proxies may lease the same verified RAS session;
- **dedicated** — only one proxy may reference/use it.

`ProxyRuntimeCoordinator` owns the configured proxy and L2TP runtime objects. `ProxyInstanceRuntime` owns one listener lifetime and one active VPN lease. `VpnLeaseManager` owns the L2TP runtime, DNS cache, aggregate metrics and maintenance/reconnect lifetime.

The first active proxy lease establishes/verifies the L2TP connection. Additional shared leases reuse it. Releasing the last lease disconnects RAS and clears connection-scoped DNS state. Pause/Resume operates independently per proxy.

Selective configuration reload replaces only changed proxy/L2TP groups. Unchanged independent groups remain running.

## GUI lifecycle

`ProxyToAnyConnect.exe` is always a WinForms GUI application.

- Closing the main form with `X` hides it to the notification area.
- Minimizing may hide it to the notification area.
- The process exits only through explicit **Exit** from the application or tray menu.
- Explicit exit disposes proxy runtimes first, drains accepted sessions, releases VPN leases, disconnects managed RAS sessions, removes ephemeral resources and then closes the GUI/tray lifetime.

## L2TP connection modes

### Existing Windows profile

`WindowsVpnProfileInspector` enumerates and inspects Current User and All Users Windows VPN profiles. Before dialing, an existing profile must be L2TP and split tunnel. The correct private/global phonebook scope is passed to RAS when needed.

The current profile enumeration/inspection implementation uses `Get-VpnConnection` through a bounded PowerShell child process. Default-route inspection itself is native IP Helper code.

### Custom ephemeral L2TP

`EphemeralRasPhonebook` creates an isolated private phonebook under `%TEMP%/ProxyToAnyConnect/ras/<session>/session.pbk`. It creates an L2TP-only RAS entry with the configured server, IPsec mode and PPP authentication/encryption options.

Custom secrets are persisted only in Windows user-bound DPAPI-protected form. The private phonebook belongs to one concrete RAS session and is disposed after failed dial/verification, disconnect, fail-closed teardown or application exit. It does not create a persistent Windows Settings VPN profile.

## Fail-closed VPN lifecycle

Each configured L2TP runtime follows:

```text
Disconnected
    |
    v
Dialing
    |
    +-- existing profile: L2TP + split-tunnel preflight
    +-- custom: prepare private ephemeral phonebook
    +-- capture host IPv4 default-route baseline
    +-- RasDial
    |
    v
Verifying
    |
    +-- discover PPP client/server IPv4
    +-- map client IPv4 to interface index + VPN DNS
    +-- default routes must still match baseline
    +-- L2TP-bound HTTPS probe must succeed
    +-- fixed expected public IPv4 must match when configured
    |
    v
Ready
    |
    +-- monitor RAS/PPP projection
    +-- monitor default-route baseline
    +-- optional source-bound keepalive
    |
    +-- any invariant failure
            -> cancel VpnContext lifetime
            -> terminate dependent proxy sessions
            -> RasHangUp exact session
            -> reconnect cooldown
            -> reconnect only while active leases remain
```

A provisional RAS connection is not exposed to proxy traffic. `VpnContext` is published only after verification succeeds and state becomes `Ready`.

## Routing invariant

ProxyToAnyConnect must not intentionally modify the host default Internet route.

Before and immediately after each `RasDial`, `WindowsDefaultRouteInspector` captures the active IPv4 default-route set using the native IP Helper route table API. A mismatch rejects the new connection before it becomes usable.

The pre-dial route set remains the baseline while the L2TP session is `Ready`. Any later mismatch is treated conservatively as fail-closed: the exact `VpnContext` is invalidated and the exact RAS handle is hung up.

Every production outbound proxy TCP socket is constrained by both:

1. `Bind()` to the IPv4 dynamically assigned to the selected L2TP session;
2. Winsock `IP_UNICAST_IF` set to that L2TP interface index.

There is no alternate unbound/DIRECT production socket path.

## Active path verification

`VpnConnectivityVerifier` performs a real HTTPS request before `Ready` using the same L2TP routing constraints as proxy traffic:

1. resolve the probe host with `L2tpDnsResolver`;
2. create an IPv4 TCP socket;
3. set `IP_UNICAST_IF` to the L2TP interface;
4. bind the source to the RAS-assigned L2TP IPv4;
5. connect to the resolved target;
6. perform TLS using the configured probe hostname;
7. request the externally visible source IPv4.

If `verification.publicAddress` is an IPv4 literal, the observed public IPv4 must match it. If it is a DNS name, only the equality check that requires a fixed expected IPv4 is skipped; the route guard, source/interface binding, L2TP DNS and real HTTPS probe remain mandatory.

Successful verification is projected into the bounded latest-status diagnostics together with the probe/egress and local IPv4/interface information.

## DNS behavior

`L2tpDnsResolver` never uses `System.Net.Dns` for proxied destination hostnames.

Every UDP/TCP DNS transport socket uses the same source IPv4 and `IP_UNICAST_IF` constraints as the proxy TCP path. The resolver supports:

- IPv4 A queries;
- IDN conversion;
- multiple DNS servers supplied by the active L2TP interface;
- DNS compression pointers;
- CNAME following with loop/hop protection;
- UDP first;
- TCP fallback for `TC=1` or conservative transport truncation;
- cancellation with the concrete `VpnContext` lifetime.

One bounded `L2tpDnsCache` belongs to each L2TP runtime and is shared by proxies leasing that runtime. Capacity is fixed (currently 512 names), DNS TTL is honored, TTL zero is not cached, and a new `VpnContext` clears old answers.

There is no system-resolver or DIRECT DNS fallback.

## Proxy data path

`ProxyServer` listens on the configured local IPv4/port. Configuration validation requires enabled proxy listener endpoints to be unique.

HTTPS uses ordinary HTTP `CONNECT`; ProxyToAnyConnect does not terminate or inspect destination TLS. Plain HTTP proxy-form requests are rewritten to origin form and proxy-only/connection-scoped headers are stripped.

The production `IProxyOutboundConnectionFactory` is `L2tpSocketFactory`. It obtains/retains the exact verified `VpnContext`, resolves through the L2TP DNS runtime, creates a source/interface-bound socket and connects without a DIRECT alternative.

Steady-state transfer characteristics:

- bidirectional asynchronous copy with backpressure;
- 32 KiB `ArrayPool<byte>` transfer buffers returned in `finally`;
- no full tunnel/request/response buffering;
- bounded header acquisition rather than `MemoryStream` plus redundant full copies;
- low-cost atomic RX/TX counters;
- no logging/GUI serialization on the byte-transfer loop.

`maxConcurrentConnections` bounds live user-space sessions per proxy. A slot is acquired before `AcceptTcpClientAsync`; when all slots are occupied, additional connections remain in the Windows TCP backlog instead of causing unbounded managed Task/CTS/buffer creation.

On Pause/reconfigure/Exit, listener cancellation is followed by deterministic session drain. `ProxyServer.RunAsync` acquires the full bounded session semaphore before returning, which proves all accepted sessions have released their slots. The higher-level runtime releases its VPN lease only after that drain completes.

## Keepalive and reconnect

Per-L2TP keepalive modes are:

- `Off`;
- `VpnServerInternalIPv4` — PPP server IPv4 from RAS projection;
- `CustomIPv4` — explicitly configured target.

`IcmpBoundPing` uses the L2TP-assigned local IPv4 as the ICMP source. Successful RTT samples feed a rolling five-minute average. Consecutive failures are counted against the configured threshold; reaching the threshold throws into the common fail-closed monitor path.

`VpnLeaseManager` owns a maintenance task only while one or more active proxy leases exist. If the L2TP is unavailable, maintenance retries after the configured cooldown and runs the full dial/verify sequence. If the last lease disappears, maintenance stops and no reconnect is retained.

## Lifetime and memory architecture

Long-run stability is an explicit architecture requirement; see `docs/memory-stability.md`.

Important ownership rules implemented today include:

- `VpnContext` uses manager + per-outbound-session reference ownership; its CTS is disposed after the final consumer releases it;
- each RAS session owns one monitor CTS/task and an optional ephemeral phonebook;
- old RAS monitors are cancelled/joined before replacement and cannot hang up a newer handle;
- proxy runtime completion observers are tracked/joined;
- proxy accepted sessions are bounded and deterministically drained;
- L2TP maintenance exists only with active leases;
- DNS cache, ping window and latest-status diagnostics are bounded;
- GUI rows are stable-ID rows updated in place;
- process memory diagnostics retain only the latest snapshot;
- production code does not force GC or working-set trimming.

Memory hardening must not add measurable proxy latency/jitter or reduce sustained throughput beyond measurement noise.

## Runtime diagnostics

Structured operational logs are append-only JSONL at `<log-root>/YYYY-MM/YYYY-MM-DD.jsonl`; history is not retained in memory.

`VpnLatestStatusRegistry` keeps at most one structured latest-status snapshot per configured L2TP (hard maximum 256 entries). The snapshot preserves current/latest diagnostics without event history:

- transient dial/verification activity;
- latest successful verification summary;
- latest keepalive RTT or failure count/threshold;
- reconnect/cooldown state;
- last fail-closed/rejection reason.

The L2TP GUI table reads these snapshots outside the proxy byte-transfer path and also shows state, assigned IPv4/interface index, lease count, aggregate RX/TX and five-minute average ping. Stale status entries are removed when an L2TP runtime is disposed.

## Main components

### `ProxyRuntimeHost` / `ProxyRuntimeCoordinator`
Own the current validated configuration runtime, start enabled proxies, expose snapshots and perform selective reconfiguration.

### `ProxyInstanceRuntime`
Owns one proxy listener lifecycle, metrics, cancellation/drain and its L2TP lease.

### `VpnLeaseManager`
Owns shared/dedicated lease semantics, one `RasConnectionManager`, one L2TP-scoped DNS cache, aggregate metrics and bounded reconnect maintenance.

### `RasConnectionManager`
Owns one current RAS session for a configured L2TP, including dial preparation, PPP projection, verification, route/RAS/keepalive monitors, reconnect cooldown, per-session monitor lifetime and ephemeral phonebook ownership.

### `VpnContext`
Represents one exact verified VPN session: assigned client IPv4, optional PPP server IPv4, interface/index, VPN DNS servers and lifetime cancellation/reference ownership.

### `VpnConnectivityVerifier`
Performs mandatory L2TP-bound HTTPS path verification before `Ready`.

### `L2tpDnsResolver` / `L2tpDnsCache`
Provide fail-closed L2TP-bound name resolution and bounded per-L2TP caching.

### `L2tpSocketFactory`
Only production outbound proxy connection factory; creates source/interface-bound TCP connections and has no DIRECT mode.

### `ProxyServer`
Implements plain HTTP forwarding and bidirectional HTTPS `CONNECT` with bounded admission and deterministic shutdown drain.

### `VpnLatestStatusRegistry`
Projects selected VPN lifecycle log events plus latest successful keepalive into one bounded current diagnostic snapshot per L2TP.

## Explicitly out of scope

- TLS MITM/decryption;
- SOCKS;
- IPv6 proxying in the current milestone;
- domain-selection rules (browser/PAC owns them);
- WFP/Windows Firewall as a baseline requirement;
- VPN load balancing or fallback between L2TP connections;
- plaintext VPN secrets in configuration.

## Automated checks

The dependency-free `.NET 10` `ProxyToAnyConnect.SelfTests` project runs on `windows-latest`. Current coverage includes, among other checks:

- L2TP/split-tunnel profile rules and native default-route inspection;
- fixed-public-IP vs DNS-name verification behavior;
- L2TP-bound DNS parser, CNAME, UDP truncation/TCP fallback, TTL/capacity/context reset;
- loopback HTTP forwarding and bidirectional CONNECT;
- VPN-lifetime cancellation of active CONNECT sessions;
- bounded proxy admission and multi-megabyte CONNECT transfer;
- deterministic proxy shutdown drain;
- append-only logging/retention;
- traffic counters and rolling ping;
- DPAPI secret protection;
- native private ephemeral RAS phonebook + PSK create/cleanup smoke test;
- `VpnContext` lifetime/collectability;
- bounded latest L2TP status projection;
- process-memory monitor lifetime;
- repeated proxy lifecycle stress.

The build workflow also publishes a self-contained `win-x64` single-file package and ZIP artifact. CI does not substitute for testing a real external L2TP endpoint.

## Real Windows integration boundary

The reproducible real-environment procedure is maintained in [`windows-integration-test.md`](windows-integration-test.md).

The largest remaining validation boundary is Windows 11 + real L2TP endpoints for both existing-profile and custom-ephemeral modes, including actual authentication, PPP projection, keepalive/reconnect, shared/dedicated multi-proxy isolation and fail-closed behavior during real network loss.
