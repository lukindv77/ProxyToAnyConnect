# ProxyToAnyConnect

Local HTTP/HTTPS proxy for Windows 11 x64 that establishes and monitors a configured L2TP connection and routes proxy traffic only through that verified connection.

## Core invariants

1. The application establishes the configured L2TP connection itself.
2. The application discovers the IPv4 address assigned to that L2TP connection at runtime.
3. Proxy outbound connections have no DIRECT fallback path.
4. Establishing L2TP must not replace the normal Windows default route used by other applications.
5. Other applications continue to use their ordinary network path unless they explicitly use this proxy.
6. HTTPS is supported with the standard HTTP `CONNECT` tunnel. TLS is not intercepted or decrypted.
7. A newly established L2TP connection is not exposed to proxy traffic until active path verification succeeds.

## Platform

- Windows 11 x64
- C# / .NET 10 (`net10.0-windows`)
- HTTP proxy listener: loopback only
- HTTP and HTTPS `CONNECT`
- IPv4 first
- Windows RAS API for L2TP lifecycle and state

## Connection state

```text
Disconnected -> Dialing -> Verifying -> Ready
```

Only `Ready` is usable by proxy traffic.

Verification currently checks:

- Windows VPN profile is L2TP with split tunneling enabled;
- the active IPv4 default-route set is unchanged by `RasDial`;
- an HTTPS probe can be completed using a socket bound to the L2TP source IPv4 and `IP_UNICAST_IF`;
- when a fixed public IPv4 is configured, the externally observed public IPv4 matches it.

If the configured public address is a DNS name instead of an IPv4 address, checks that depend on a fixed expected IPv4 are skipped. The remaining L2TP-bound route and active probe checks are still required.

`l2tp.verification.publicAddress` means the public identity seen by Internet services for traffic exiting through L2TP. It is **not** the public address of the L2TP server endpoint.

## Traffic flow

```text
Chrome / other client
        |
        | HTTP proxy 127.0.0.1:18080
        v
ProxyToAnyConnect
        |
        | verified L2TP-bound socket only
        v
Windows L2TP
        |
        v
Internet
```

If L2TP is unavailable, cannot be verified, changes the Windows default route, or fails the public-IP check, proxy requests fail. The application never retries proxy traffic through the normal Wi-Fi/Ethernet route.

## Required configuration before first run

Edit `src/ProxyToAnyConnect/appsettings.json` (or the deployed copy) and set:

```json
{
  "l2tp": {
    "entryName": "ProxyToAnyConnect-L2TP",
    "verification": {
      "publicAddress": "YOUR_L2TP_PUBLIC_IPV4_OR_DNS_NAME",
      "probeHost": "api.ipify.org",
      "probePort": 443,
      "probePath": "/"
    }
  }
}
```

The Windows VPN entry must already exist and have its credentials stored by Windows. Credentials are not stored in this repository.

See [`docs/architecture.md`](docs/architecture.md) for the current architecture.
