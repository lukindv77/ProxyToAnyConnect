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
- DNS resolution for proxied hostnames is performed through sockets bound to the current L2TP IPv4.
- Every proxy-originated TCP connection is bound to the current L2TP IPv4 before `connect()`.
- If the L2TP context disappears, its lifetime token is cancelled and active proxy tunnels are terminated.
- If no L2TP context exists, an incoming proxy request may trigger a new `RasDial`; if dialing fails, the request fails closed.

## Routing invariant

ProxyToAnyConnect must not intentionally change the Windows default route.

The Windows RAS profile used by the application must be configured for split tunneling ("Use default gateway on remote network" disabled). This is necessary because `RasDial` honors the routing policy stored in the Windows VPN profile.

A future preflight guard must inspect/verify this profile property before dialing and reject a full-tunnel profile. Until that guard is implemented, correct split-tunnel configuration of the named Windows RAS profile is an explicit deployment prerequisite.

## Data flow

```text
Browser / client
      |
      |  HTTP proxy 127.0.0.1:18080
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
      +-- require live VpnContext
      +-- otherwise RasConnectionManager.ConnectAsync()
      +-- resolve hostname using L2TP-bound DNS socket
      +-- create TCP socket
      +-- bind(source = L2TP assigned IPv4)
      +-- connect(target IPv4)
      v
Windows L2TP/RAS interface
      |
      v
Internet
```

## Current components

### `RasConnectionManager`

Owns the RAS connection handle. It obtains stored connection parameters for the configured Windows RAS entry, calls `RasDialW`, obtains the client IPv4 using `RasGetProjectionInfoW(RASP_PppIp)`, creates a `VpnContext`, and monitors that projection for loss/change.

### `VpnContext`

Contains the current L2TP entry name, assigned IPv4, Windows network-interface name/index, VPN DNS server list, and a cancellation token representing the lifetime of this exact VPN context.

### `L2tpDnsResolver`

Performs IPv4 A-record DNS queries using UDP sockets explicitly bound to the current L2TP IPv4. It does not call the Windows system hostname resolver for proxied hostnames.

### `L2tpSocketFactory`

The only intended factory for outbound proxy TCP sockets. It has no DIRECT mode. It binds each socket to the current L2TP IPv4 before connecting.

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

## Next hardening work

1. Verify the Windows VPN/RAS profile is split-tunnel before `RasDial`.
2. Add explicit `IP_UNICAST_IF`/interface binding in addition to source-IP binding.
3. Add automatic tests for HTTP parsing, CONNECT behavior and DNS packet parsing.
4. Add integration tests on Windows with a test RAS/VPN environment.
5. Add structured logs and a Windows Service host mode.
