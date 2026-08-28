# ProxyToAnyConnect — current handoff state

> Updated 2026-08-28. Live GitHub is authoritative over this snapshot.

## Accepted production code checkpoint before this handoff-doc commit

- Production `main`: `5811900dfbf7488bd8ac53af20348c462681eeef`.
- Tree: `e44bf16408da3abade0c0f4d04708e6fd5ccd4ac`.
- Exact-main build #616 / run `33152272544`: success through Windows evidence smokes, restore/build, aggregate self-tests, self-contained win-x64 publish, integrity manifest, ZIP and artifact upload.
- Windows artifact `9678213447`, digest `sha256:bd31b7f143d11c56cfc6794e55760e156341ca07bdd8fcbb52691d5010e9c1e7`.
- Exact-main handoff #393 / run `33152272516`: success.
- Handoff artifact `9678172387`, digest `sha256:bef544b5997914274001b50fce35684dcdd633d44c6230de654c0769db0a77c9`.

This documentation commit intentionally moves `main`; the exact docs-head build/handoff after merge becomes the new transport checkpoint.

## Architecture/invariants already accepted

- Windows 11 x64, .NET 10 WinForms/tray; multiple independent HTTP/HTTPS forward proxies.
- Absolutely no DIRECT fallback: outbound source `Bind()` + `IP_UNICAST_IF`, custom L2TP-bound DNS, split-tunnel/default-route guards.
- VPN lifecycle `Disconnected -> Dialing -> Verifying -> Ready`; no usable context before real L2TP-bound HTTPS verification.
- Shared/dedicated leases; exact accepted-session drain before higher ownership releases a VPN lease.
- ExistingWindowsProfile + private CustomEphemeral PBK; DPAPI-protected secrets, unmanaged plaintext zero-before-free, fixed-width RAS field limits, and prompt managed secret-carrier release.
- Canonical/reparse-safe/non-recursive filesystem cleanup and log retention ownership.
- Strict HTTP request-line/header/framing/authority grammar, one canonical routing authority, fail-closed response commitment, client-header 408 and outbound 504 semantics.
- DNS authority/response binding, monotonic TTL expiry, bounded cache, L2TP-only transport.
- Pooled 32 KiB transfer path, bounded concurrency/memory, no production forced GC, unchanged 1.25x security/performance policy.
- Terminal runtime cleanup retains only failed exact owners for retry; application exit performs at most one immediate exact runtime-host retry after all independent first-pass owners were attempted.

## Recently accepted deterministic hardening

Closed completed findings include #52, #53, #54, #58, #59, #62, #63, #66, #67, #70, #71, #73, #75, #77, #79, #80 and #85. Their issue comments are the detailed lineage source of truth.

Latest production examples:
- #79 outbound deadline: merged `92fa6c8e94a2cb466e3e27780547d35a2d587ec5`; exact build #612 + handoff #391 green.
- #80 client-header timeout: merged `e5c817aeebc3b29c8e19fa07423eba8221cd47d7`; exact build #614 + handoff #392 green.
- #85 terminal cleanup ownership: merged `5811900dfbf7488bd8ac53af20348c462681eeef`; exact build #616 + handoff #393 green.

## Real acceptance boundary

Open live issues are #2/#4/#5/#6/#7/#11/#13. #2/#4/#5/#6/#7 require genuine Windows 11 + real L2TP/operator evidence. #13 requires a representative 12–24 h exact-binary soak with traffic/lifecycle churn and correlated managed/native process-resource series. #11 remains the permanent latency/throughput/process-memory architecture contract. Hosted Actions smoke must never be reported as those real-world acceptance results.

## Continuation

Continue broad deterministic audits and coherent engineering blocks. New concrete finding: issue-first acceptance -> code/tests -> permanent Windows PR CI -> merge/rebase -> exact-main build + handoff -> issue lineage. Do not churn accepted boundaries without a new reproducible defect, and never weaken routing/security/performance invariants to make CI pass.
