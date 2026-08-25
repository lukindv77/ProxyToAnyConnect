# ProxyToAnyConnect

Local HTTP/HTTPS proxy for Windows 11 x64 that establishes and monitors a configured L2TP connection and routes proxy traffic only through that connection.

## Core invariants

1. The application establishes the configured L2TP connection itself.
2. The application discovers the IPv4 address assigned to that L2TP connection at runtime.
3. Proxy outbound connections have no DIRECT fallback path.
4. Establishing L2TP must not replace the normal Windows default route used by other applications.
5. Other applications continue to use their ordinary network path unless they explicitly use this proxy.
6. HTTPS is supported with the standard HTTP `CONNECT` tunnel. TLS is not intercepted or decrypted.

## Initial platform

- Windows 11 x64
- C# / .NET 8 (`net8.0-windows`)
- HTTP proxy listener: loopback only
- HTTP and HTTPS `CONNECT`
- IPv4 first
- Windows RAS API for L2TP lifecycle and state

## Planned flow

```text
Chrome / other client
        |
        | HTTP proxy 127.0.0.1:18080
        v
ProxyToAnyConnect
        |
        | outbound socket bound to L2TP context
        v
Windows L2TP
        |
        v
Internet
```

If L2TP is unavailable, proxy requests fail. The application must never retry through the normal Wi-Fi/Ethernet route.

See [`docs/architecture.md`](docs/architecture.md) for the current architecture.
