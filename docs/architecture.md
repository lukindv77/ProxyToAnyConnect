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
- If no L2TP context exists, an incoming proxy request may trigger a new `RasDial`; if dialing fails, the request fails closed.

## Routing invariant

ProxyToAnyConnect must not intentionally change the Windows default route.

Before every new `RasDial`, `WindowsVpnProfileInspector` queries the named Windows VPN profile and rejects it unless:

- `TunnelType` is `L2tp`;
- `SplitTunneling` is `true`.

The inspection uses the Windows `VpnClient` PowerShell provider (`Get-VpnConnection`) as the current supported profile API. If the profile is full-tunnel, the application refuses to dial it. This prevents ProxyToAnyConnect itself from activating a profile that would intentionally replace the normal default route for other applications.

A Windows integration test must still verify the actual route table before and after `RasDial` on the target environment.

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
      +-- otherwise validate VPN profile and RasDial
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

### `RasConnectionManager`

Owns the RAS connection handle. It validates the profile, obtains stored connection parameters for the configured Windows RAS entry, calls `RasDialW`, obtains the client IPv4 using `RasGetProjectionInfoW(RASP_PppIp)`, creates a `VpnContext`, and monitors that projection for loss/change.

### `VpnContext`

Contains the current L2TP entry name, assigned IPv4, Windows network-interface name/index, VPN DNS server list, and a cancellation token representing the lifetime of this exact VPN context.

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

The solution contains `ProxyToAnyConnect.SelfTests`, a dependency-free .NET 10 console test project. GitHub Actions builds the solution on `windows-latest` and runs the self-tests.

Current self-tests cover:

- accepting L2TP + split-tunnel profiles;
- rejecting full-tunnel profiles;
- rejecting non-L2TP profiles;
- rejecting an invalid zero interface index.

## Next hardening work

1. Add Windows integration verification of the route table before and after `RasDial`.
2. Add tests for HTTP parsing and HTTPS `CONNECT` behavior.
3. Add tests for DNS packet parsing and CNAME handling.
4. Add structured logs and a Windows Service host mode.
5. Add a reproducible installer/publish workflow for Windows 11 x64.
