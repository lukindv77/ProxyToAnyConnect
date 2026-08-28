# ProxyToAnyConnect — current handoff state

> Updated 2026-08-28. Live GitHub is authoritative over this snapshot.

## Accepted production checkpoint before this handoff-doc commit

- `main`: `2e56f8f76efda9047ec83f3cd0e58aee395de322`.
- Exact-main build #577 / `33097542082`: success, including evidence smokes, aggregate self-tests and Windows self-contained artifact publication.
- Exact-main handoff #373 / `33097542206`: success.
- PR #48 accepted #47 soak-duration integrity and #45 strict request-line separators.

The handoff commit moves `main`; verify the new exact head and its Actions before coding.

## Architecture already accepted

- Windows 11 x64, .NET 10 WinForms/tray, multiple independent HTTP/HTTPS forward proxies.
- No DIRECT fallback: outbound TCP source bind + `IP_UNICAST_IF`, L2TP-bound custom DNS, split-tunnel/default-route guards.
- L2TP lifecycle `Disconnected -> Dialing -> Verifying -> Ready`; fail-closed cancellation of dependent sessions.
- Shared/dedicated VPN leases, reconnect/keepalive ownership, exact accepted-session drain before lease release.
- CustomEphemeral private PBK with lock-first ownership and reparse-safe exact-leaf orphan recovery.
- Transactional configuration/runtime reconciliation, bounded diagnostics/logging, process-memory ownership tests.
- Strict HTTP framing, authority/Host/routing grammar and strict request-line separators.

## Latest audit status

- #6 current orphan-cleanup and secret-carrier hardening is already implemented/tested; no duplicate patch required.
- #49 and #50 are open verification-wire findings.
- `dev/issue49-probe-target` has Windows-green validated source commit `1684718295944ecdb28216ae02c32365ff7b2b0c` from run `33130832271`.
- That source commit changes only four expected production/test files. It is not yet production-accepted; clean reconstruction + permanent PR + exact-main build/handoff are still required.

## Real acceptance boundary

#2/#4/#5/#6/#7 still require real Windows 11 + L2TP/operator evidence. #13 still requires representative 12–24 h exact-binary soak. #11 remains a permanent latency/throughput/memory requirement. Hosted smoke does not replace these.

See `SESSION_2026-08-28.md` and `NEW_CHAT_PROMPT.md` for exact continuation details.
