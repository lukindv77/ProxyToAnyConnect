# ProxyToAnyConnect architecture

## Goal

ProxyToAnyConnect is a Windows 11 x64 local HTTP/HTTPS proxy. It owns the lifecycle of one configured Windows L2TP/RAS connection and sends proxy-originated traffic only through that connection.

The application is intentionally not a system-wide VPN client. Applications that do not use the local proxy must continue to use their normal Windows network path.

## Fixed requirements

- C# / .NET 10 (`net10.0-windows`).
- The application calls Windows RAS itself to establish the configured L2TP entry.
- The IPv4 assigned by the L2TP server is discovered at runtime through RAS PPP projection information.
- The RAS IPv4 is mapped to the corresponding Windows network interface to obtain its interface index and VPN DNS servers.
- The proxy listens on loopback only.
- HTTP proxying is supported.
- HTTPS is supported through the standard HTTP `CONNECT` method; TLS is end-to-end and is not intercepted.
- There is no DIRECT fallback inside the proxy.
- DNS resolution for proxied hostnames is performed through sockets bound to the current L2TP IPv4 and interface.
- Every proxy-originated TCP connection is bound to the current L2TP IPv4 and explicitly constrained to the current L2TP interface before `connect()`.
- If the L2TP context disappears, its lifetime token is cancelled and active proxy tunnels are terminated.
- If no verified L2TP context exists, an incoming proxy request may trigger a new `RasDial`; if dialing or verification fails, the request fails closed.
- The host IPv4 default-route set is continuously monitored while the VPN is `Ready`; a change invalidates the current VPN context and tears down the RAS connection.

## VPN lifecycle

```text
Disconnected
    |
    v
Dialing
    |
    v
Verifying
    |
    +-- profile must be L2TP + split tunnel
    +-- IPv4 default-route snapshot must remain unchanged
    +-- HTTPS probe must succeed through L2TP-bound socket
    +-- if expected public address is IPv4, observed public IPv4 must match
    |
    v
Ready
    |
    +-- RAS/PPP IPv4 is monitored continuously
    +-- host IPv4 default routes are monitored continuously
    |
    +-- any invariant violation -> Disconnected + RasHangUp
```

`VpnContext` is published to the proxy layer only in `Ready`. A physically established RAS connection in `Dialing` or `Verifying` is not usable by proxy traffic.

## Routing invariant

ProxyToAnyConnect must not intentionally change the Windows default route.

Before every new `RasDial`, `WindowsVpnProfileInspector` queries the named Windows VPN profile and rejects it unless:

- `TunnelType` is `L2tp`;
- `SplitTunneling` is `true`.

The application also captures the active Windows IPv4 default-route set immediately before and after `RasDial`. If that set changes, the newly created L2TP connection is immediately torn down and never reaches `Ready`.

The pre-dial snapshot becomes the `Ready` baseline. While the VPN is active, the application periodically captures the default-route set again using `l2tp.routeMonitorIntervalMilliseconds` (default: 5000 ms). Any difference from the pre-dial baseline invalidates the current `VpnContext`, cancels active proxy tunnels and calls `RasHangUp` for that exact RAS connection.

This monitoring is intentionally conservative: an unrelated host default-route change also forces a fail-closed reconnect instead of allowing the proxy to continue under an unverified routing state.

## Active path verification

`VpnConnectivityVerifier` performs a real HTTPS request before `Ready` using the same routing constraints as future proxy traffic:

1. resolve the configured probe host through the L2TP DNS servers;
2. create an IPv4 TCP socket;
3. apply `IP_UNICAST_IF = L2TP InterfaceIndex`;
4. bind the socket source to the IPv4 assigned by the current RAS session;
5. connect to the probe endpoint;
6. perform TLS with the configured probe host as SNI/certificate target;
7. request the externally visible source IPv4.

`l2tp.verification.publicAddress` means the expected public identity of traffic **after it exits through L2TP**, not the public address of the L2TP server itself.

If `publicAddress` is an IPv4 address, the verifier requires the probe to report exactly that IPv4. A mismatch means verification fails, RAS is disconnected, and the proxy remains fail-closed.

If `publicAddress` is a DNS name, checks that require a fixed expected IPv4 are deliberately skipped. The route-table, L2TP source-address, L2TP interface and real HTTPS probe checks remain mandatory.

The verifier never creates a DIRECT control connection. Its own traffic is also constrained to L2TP.

## Continuous health monitoring

After `Ready`, two independent monitors run in parallel:

- `monitorIntervalMilliseconds` (default 1000 ms): confirms the same RAS PPP IPv4 is still projected for the connection;
- `routeMonitorIntervalMilliseconds` (default 5000 ms): confirms the host IPv4 default-route set still exactly matches the pre-dial baseline.

The first monitor failure wins. The current `VpnContext` is marked disconnected, its lifetime cancellation token is triggered, active HTTP/HTTPS proxy streams are terminated, and the exact RAS handle is hung up. No DIRECT retry exists.

## Diagnostic mode

The executable supports:

```text
ProxyToAnyConnect.exe [appsettings.json] --verify-only
```

This mode performs the complete `Dialing -> Verifying -> Ready` sequence, prints the assigned L2TP IPv4, interface index, DNS servers and active probe result, then exits without starting the proxy listener. It is intended for the first Windows 11 integration test and troubleshooting of VPN/profile/routing configuration independently of Chrome.

## Data flow

```text
Browser / client
      |
      | HTTP proxy 127.0.0.1:18080
      v
ProxyServer
      |
      +-- HTTP request forwarding
      |
      +-- HTTPS CONNECT tunnel
      |
      v
L2tpSocketFactory
      |
      +-- require Ready VpnContext
      +-- otherwise validate profile / RasDial / Verify
      +-- resolve hostname using L2TP-bound DNS socket
      +-- create TCP socket
      +-- bind(source = L2TP assigned IPv4)
      +-- IP_UNICAST_IF = L2TP InterfaceIndex
      +-- connect(target IPv4)
      v
Windows L2TP/RAS interface
      |
      v
Internet
```

## Current components

### `WindowsVpnProfileInspector`
Reads the configured Windows VPN profile before dialing. It fails closed unless the profile is L2TP with split tunneling enabled.

### `WindowsDefaultRouteInspector`
Captures the active IPv4 default-route set before and after `RasDial`, and continues checking the pre-dial baseline while the VPN is `Ready`.

### `RasConnectionManager`
Owns the RAS connection handle and lifecycle state. It validates the profile, snapshots default routes, calls `RasDialW`, obtains the client IPv4 using `RasGetProjectionInfoW(RASP_PppIp)`, constructs a provisional `VpnContext`, runs active connectivity verification, publishes the context only after all checks pass, and supervises continuous RAS/route monitoring.

### `VpnContext`
Contains the L2TP entry name, assigned IPv4, Windows network-interface name/index, VPN DNS server list, and a cancellation token representing the lifetime of this exact VPN context.

### `VpnConnectivityVerifier`
Performs the mandatory HTTPS path probe before `Ready`. If a fixed expected public IPv4 is configured, it also checks the externally observed IPv4.

### `WindowsSocketInterfaceBinder`
Applies Winsock `IP_UNICAST_IF` to an IPv4 socket using the current L2TP `InterfaceIndex`. This is socket-local and does not alter the Windows default route.

### `L2tpDnsResolver`
Performs IPv4 A-record DNS queries using UDP sockets explicitly bound to the current L2TP IPv4 and `InterfaceIndex`. It does not call the Windows system hostname resolver for proxied hostnames.

### `L2tpSocketFactory`
The only intended factory for outbound proxy TCP sockets. It has no DIRECT mode. Each socket is source-bound to the current L2TP IPv4 and receives `IP_UNICAST_IF` for the L2TP interface before connecting.

### `ProxyServer`
Listens only on loopback. Implements HTTP forwarding and HTTPS `CONNECT`. Active tunnels are linked to the `VpnContext` lifetime token.

## Explicitly out of scope for the first milestone

- TLS MITM/decryption.
- SOCKS.
- IPv6.
- Domain-selection rules (those belong to the browser/PAC layer).
- WFP/Windows Firewall kill switch.
- Multi-VPN load balancing or fallback.
- Embedding VPN credentials in repository configuration.

## Automated checks

The solution contains `ProxyToAnyConnect.SelfTests`, a dependency-free .NET 10 console test project. GitHub Actions builds the solution on `windows-latest`, runs self-tests, publishes a self-contained Windows x64 package and uploads it as a workflow artifact.

Current self-tests cover:

- accepting L2TP + split-tunnel profiles;
- rejecting full-tunnel profiles;
- rejecting non-L2TP profiles;
- accepting unchanged default routes;
- rejecting changed default routes;
- rejecting an invalid zero interface index;
- enabling fixed-public-IPv4 equality checking when `publicAddress` is IPv4;
- skipping that IP-equality check when `publicAddress` is a DNS name.

## Next hardening work

1. Add an integration test against a real Windows L2TP environment.
2. Harden DNS resolution (TCP fallback for truncated replies and explicit CNAME coverage).
3. Add tests for HTTP parsing and HTTPS `CONNECT` behavior.
4. Add structured logs and a Windows Service host mode.
5. Add a reproducible installer in addition to the existing self-contained ZIP publish.
