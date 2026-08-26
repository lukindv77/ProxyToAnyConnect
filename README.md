# ProxyToAnyConnect

Windows 11 x64 GUI application that exposes one or more local HTTP/HTTPS proxy listeners and routes each proxy exclusively through its selected verified L2TP connection.

> **Current development contract:** [`docs/requirements.md`](docs/requirements.md) is the source of truth for product behavior and acceptance criteria.

## Target platform

- Windows 11 x64
- C# / .NET 10 (`net10.0-windows`)
- WinForms GUI + system tray
- IPv4 first
- Windows RAS / Winsock / IP Helper APIs

## Core invariants

1. Proxy traffic has no DIRECT fallback path.
2. Every outbound proxy socket is bound to the IPv4/interface of its selected verified L2TP session.
3. Establishing L2TP must not replace or modify the normal default Internet route used by unrelated applications.
4. DNS for proxied traffic is resolved through the selected L2TP context.
5. HTTPS uses normal HTTP `CONNECT`; ProxyToAnyConnect does not intercept or decrypt TLS.
6. L2TP is not usable by proxy traffic until active verification reaches `Ready`.
7. Loss of a verified L2TP context cancels dependent active proxy tunnels fail-closed.
8. Memory/resource use must remain bounded during long-running operation, and memory optimization must not increase proxy forwarding latency/jitter or reduce sustained throughput.

## GUI lifecycle

`ProxyToAnyConnect.exe` always runs as a GUI application.

- The main window may be hidden/minimized to the Windows notification area.
- Clicking the window close button (`X`) hides it to tray; it does **not** terminate the process.
- The process exits only through an explicit **Exit** command in the application menu or tray context menu.

## Multi-proxy model

The runtime supports multiple independent proxy instances.

Each proxy has its own:

- name and stable ID;
- bind IPv4;
- bind TCP port;
- header/read timeout;
- outbound connect timeout;
- DNS timeout;
- `maxConcurrentConnections`;
- selected L2TP connection;
- runtime state, RX/TX and last error.

Each proxy can be independently **Running** or **Paused**.

Pausing a proxy stops only its listener and active sessions. Resuming starts it again.

## Shared and dedicated L2TP

L2TP connections are independent catalog entities referenced by proxy instances.

- **Shared L2TP** may be used by multiple Running proxies through one verified RAS session.
- **Dedicated L2TP** is assigned to one proxy.

A Running proxy holds a runtime lease on its selected L2TP connection:

```text
first active lease
        -> Dialing -> Verifying -> Ready

additional shared proxy
        -> reuse same Ready L2TP

last active lease released
        -> RasHangUp
```

Therefore, if a proxy is paused and no other active proxy uses its L2TP connection, that L2TP session is disconnected automatically.

## L2TP connection modes

### Existing Windows profile

The GUI provides interactive selection of existing Windows L2TP profiles. Existing profiles are validated for L2TP + split-tunnel/fail-closed compatibility before dialing.

### Custom ephemeral L2TP

A connection may instead be configured directly in ProxyToAnyConnect with server/authentication/IPsec/PPP settings.

The custom connection does not become a persistent Windows VPN profile. The implementation creates a temporary private RAS phonebook entry only for the concrete runtime/session and removes it after disconnect/failure/exit.

Passwords and PSKs are not stored as plaintext; Windows user-bound DPAPI protects persisted secrets.

## Verification and monitoring

A usable L2TP connection follows:

```text
Disconnected -> Dialing -> Verifying -> Ready
```

Verification includes assigned RAS IPv4/interface discovery, preservation of the host default-route set, an L2TP-bound HTTPS probe, and public IPv4 equality when a fixed expected public IPv4 is configured.

If the configured public address is a DNS name instead of an IPv4 literal, checks that inherently require a fixed expected IPv4 are skipped; the remaining L2TP-bound verification is still mandatory.

The GUI L2TP table exposes lifecycle state, assigned IPv4/interface index, active proxy leases, aggregate RX/TX, rolling keepalive ping and bounded latest status/reason detail including verification, keepalive failures, reconnect cooldown and the last fail-closed reason.

## Keepalive

Each L2TP connection has its own keepalive policy:

- `Off`
- `VpnServerInternalIPv4` — probe the internal PPP server IPv4 returned by RAS
- `CustomIPv4` — probe an explicitly configured IPv4

Settings include probe interval, probe timeout and consecutive failure threshold.

After the failure threshold is reached:

```text
invalidate context
   -> cancel dependent proxy tunnels
   -> RasHangUp
   -> reconnect cooldown
   -> if active proxy leases exist:
         Dialing -> Verifying -> Ready
      else:
         remain Disconnected
```

The keepalive probe itself uses the selected L2TP source IPv4 and has no DIRECT fallback.

## Current implementation status

The current `main` is the WinForms multi-proxy architecture described above. Implemented pieces include shared/dedicated L2TP lease management, independent Pause/Resume, existing-profile and custom-ephemeral RAS lifecycles, L2TP-bound DNS, source IPv4 + `IP_UNICAST_IF` socket routing, active verification, default-route guards, keepalive/reconnect, bounded runtime diagnostics, deterministic proxy shutdown drain, append-only logging and process memory/resource health diagnostics.

The largest remaining validation boundary is real Windows 11 testing against external L2TP endpoint(s). GitHub Issues are the live roadmap and acceptance tracker.

## Roadmap issues

Open roadmap work currently includes:

- #2 — Windows 11 integration test with real L2TP endpoint
- #4 — multi-proxy/shared-dedicated acceptance and real isolation validation
- #5 — settings UI/validation and real Windows interaction validation
- #6 — real custom ephemeral L2TP endpoint validation
- #7 — real keepalive/reconnect validation
- #11 — ongoing process-wide performance/memory optimization
- #13 — ongoing long-run memory/resource stability audit

Completed roadmap milestones include #1, #3, #8, #9, #10 and #12. Always query GitHub for the current issue state rather than relying on this README snapshot.

## New-chat handoff

The repository contains a reproducible handoff for continuing development in a new ChatGPT conversation without relying on the old chat history:

- [`docs/handoff/NEW_CHAT_PROMPT.md`](docs/handoff/NEW_CHAT_PROMPT.md) — copy/paste startup prompt for the new chat;
- [`docs/handoff/CURRENT_STATE.md`](docs/handoff/CURRENT_STATE.md) — implementation/progress snapshot;
- [`docs/handoff/AUDIT_SNAPSHOT.md`](docs/handoff/AUDIT_SNAPSHOT.md) — preserved technical audit findings and reasons behind important architecture decisions;
- [`docs/handoff/ISSUES_SNAPSHOT.md`](docs/handoff/ISSUES_SNAPSHOT.md) — issue-state snapshot at handoff time;
- [`docs/handoff/MANIFEST.md`](docs/handoff/MANIFEST.md) — files/state that must be checked at handoff.

The `handoff` GitHub Actions workflow packages the exact checked-out commit into a `ProxyToAnyConnect-handoff-<sha>` ZIP artifact. A new chat must still query current `main`, open issues and latest CI because GitHub remains authoritative over a snapshot.

## Documentation

- [`docs/requirements.md`](docs/requirements.md) — current product requirements and runtime semantics
- [`docs/architecture.md`](docs/architecture.md) — current implementation architecture
- [`docs/memory-stability.md`](docs/memory-stability.md) — long-run memory/resource stability and latency-preserving optimization rules
- [`docs/windows-integration-test.md`](docs/windows-integration-test.md) — current real Windows 11 / L2TP integration procedure
