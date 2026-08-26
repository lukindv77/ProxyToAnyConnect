# ProxyToAnyConnect — Current Product Requirements

This document is the current source of truth for product behavior. Later implementation decisions must preserve these invariants unless this document is intentionally changed.

## Platform and application lifecycle

- Windows 11 x64.
- C# / .NET 10 (`net10.0-windows`).
- `ProxyToAnyConnect.exe` always starts and operates as a GUI application.
- The main window can be minimized to the notification area (system tray).
- Clicking the window close button (`X`) must hide/minimize the application to the tray; it must not terminate the process.
- The process can be terminated only by an explicit **Exit** command:
  - from the tray context menu; or
  - from the application menu.
- On explicit Exit, proxy listeners are stopped, active proxy sessions are cancelled, managed L2TP sessions are disconnected, ephemeral resources are removed, and the process terminates cleanly.

## Multi-proxy model

The application supports multiple independent proxy instances at the same time.

Each proxy instance has at least:

- stable ID and display name;
- enabled/disabled persistent setting;
- runtime state (`Running`, `Paused`, `Error`, plus transitional states as needed);
- inbound bind IPv4 address;
- inbound TCP port;
- HTTP header/read timeout;
- outbound connect timeout;
- DNS timeout;
- reference to one configured L2TP connection.

Every enabled proxy must have a unique inbound `IPv4:port` endpoint.

### Pause / Resume

- Every proxy can be paused independently.
- Pausing a proxy stops only that proxy listener and cancels its active proxy sessions.
- The paused proxy releases its L2TP runtime lease.
- Resuming starts the listener again and reacquires its L2TP lease.
- A pause of one proxy must not stop unrelated proxies.

## L2TP connection catalog

L2TP connections are independent configured entities. A proxy references an L2TP connection by ID.

An L2TP connection can be:

- **shared** — multiple proxy instances may use the same runtime L2TP session;
- **dedicated** — at most one proxy may reference/use that L2TP connection.

### Runtime lease semantics

A running proxy holds a runtime lease on its selected L2TP connection.

- First active lease causes the L2TP connection to be established and verified.
- Additional proxies using a shared L2TP reuse the same verified connection.
- Releasing a lease does not disconnect the L2TP connection while another running proxy still holds a lease.
- When the last active proxy lease is released, the corresponding L2TP connection must be disconnected.
- Therefore, if a proxy is paused and no other active proxy uses its L2TP connection, that L2TP session is immediately torn down.

## Existing Windows L2TP mode

A configured L2TP connection may use an existing Windows VPN profile.

The settings UI must provide an interactive list of available Windows L2TP profiles.

Before dialing an existing profile, the application must validate that it is compatible with fail-closed operation, including split-tunnel requirements. Establishing the VPN must not replace or modify the normal default Internet route used by unrelated applications.

## Custom ephemeral L2TP mode

A configured L2TP connection may instead be a **custom ephemeral L2TP** connection.

The settings UI must expose the parameters required to establish it, including at least:

- server IP address or DNS name;
- username;
- password;
- optional domain;
- option to use current Windows credentials where supported;
- IPsec authentication mode:
  - pre-shared key (PSK), or
  - machine certificate;
- encryption mode;
- allowed PPP authentication protocols (PAP / CHAP / MS-CHAPv2);
- relevant connection/verification/monitoring timeouts.

Secrets such as password and PSK must not be stored as plaintext in configuration. They must be protected with Windows user-bound DPAPI or an equivalently strong Windows-native secret mechanism.

### Ephemeral profile invariant

Custom L2TP must not create a persistent VPN profile visible in Windows Settings.

The implementation may use a temporary private RAS phonebook entry because RAS dialing requires an entry, but:

- the temporary phonebook is private to ProxyToAnyConnect;
- it is created only for the lifetime of the connection/runtime;
- it is removed after disconnect / application exit;
- no persistent Windows VPN profile is registered.

## Fail-closed routing invariant

Proxy traffic must never fall back to the ordinary Internet path.

For every outbound proxy socket:

- source IPv4 is bound to the IPv4 assigned to the selected L2TP session;
- `IP_UNICAST_IF` selects the corresponding Windows interface;
- DNS resolution is performed through the L2TP context, not normal system DNS;
- no alternate DIRECT socket path exists.

Unrelated applications must continue to use their normal network path and must not be affected by ProxyToAnyConnect establishing L2TP.

## L2TP verification before use

A newly established L2TP session is not available to proxy traffic until verification finishes successfully.

Required lifecycle:

`Disconnected -> Dialing -> Verifying -> Ready`

Verification includes:

- determine assigned L2TP client IPv4;
- determine L2TP interface index and VPN DNS servers;
- ensure host IPv4 default routes remain unchanged;
- perform a real HTTPS probe using an L2TP-bound socket;
- when configured public address is an IPv4 literal, compare the observed public IPv4 with the configured expected IPv4;
- when configured public address is a DNS name, skip only checks that inherently require a fixed expected public IPv4; all other L2TP-bound verification remains mandatory.

Until `Ready`, proxy traffic through that L2TP connection is blocked.

## Continuous monitoring

While an L2TP session is `Ready`, continuously monitor:

- RAS/PPP session health;
- assigned IPv4 stability;
- host default-route invariants;
- keepalive according to the configured policy.

A monitor failure must fail closed:

- invalidate/cancel the VPN context;
- terminate active proxy tunnels using that L2TP connection;
- hang up the invalid RAS connection;
- if active proxy leases still exist, reconnect and re-run complete verification;
- if there are no active leases, remain disconnected.

## Keepalive

Each L2TP connection has independent keepalive settings.

Supported modes:

1. `Off`
2. `VpnServerInternalIPv4`
   - use the internal PPP server IPv4 returned by the established RAS projection;
3. `CustomIPv4`
   - use an arbitrary explicitly configured IPv4 target.

Keepalive settings include at least:

- mode;
- custom target IPv4 when `CustomIPv4` is selected;
- probe interval;
- probe timeout;
- consecutive failure threshold.

The keepalive probe must be forced through the selected L2TP session. It must never use a DIRECT fallback.

When the consecutive failure threshold is reached:

1. mark the active L2TP context unavailable;
2. terminate active proxy sessions using it;
3. hang up the RAS connection;
4. apply reconnect cooldown;
5. if one or more running proxies still hold leases, reconnect and perform full verification before traffic resumes.

## GUI settings

The GUI must allow configuring and managing:

### Proxy instances

- add / remove proxy;
- name;
- bind IPv4 selected from local interfaces;
- bind TCP port;
- associated L2TP connection;
- header/read timeout;
- outbound connect timeout;
- DNS timeout;
- start/resume;
- pause;
- current runtime state and last error.

### L2TP connections

- add / remove connection;
- name;
- shared vs dedicated;
- existing Windows profile vs custom ephemeral mode;
- interactive Windows L2TP profile selection;
- all custom L2TP parameters described above;
- verification settings;
- monitor intervals;
- reconnect cooldown;
- keepalive mode and settings;
- current connection state, assigned IPv4, interface index and verification result.

## Security and privacy

- No TLS MITM. HTTPS works through standard HTTP `CONNECT` tunnels.
- Do not log passwords, PSKs, HTTP bodies, or HTTPS tunnel contents.
- Structured diagnostic logs may contain state transitions, listener endpoints, VPN IDs/names, assigned non-secret IP information, verification outcomes and fail-closed reasons.
- Configuration validation must reject ambiguous or unsafe combinations before starting traffic.
