# ProxyToAnyConnect — current handoff state

> Updated 2026-08-28. Live GitHub is authoritative over this snapshot.

## Accepted production checkpoint before this handoff-doc commit

- Production `main`: `ddbdc95e3b9e7080a31c2b631da1c1f187a1f1a3`.
- Tree: `4f11a13a1ac0d1839b86671dc0b7ccae7eed0d40`.
- PR #51 cleanly accepted #49/#50 from four production/test files only.
- Exact-main build #580 / run `33132200561`: success through evidence smokes, restore/build, aggregate self-tests, self-contained win-x64 publish, binary integrity manifest, ZIP and artifact upload.
- Windows artifact id `9670700014`, digest `sha256:83e91fbda614aeb804fcdecfc05bf589247582143c609a453be76f5e92acd76e`.
- Exact-main handoff #375 / run `33132200498`: success.
- Handoff artifact id `9670678196`, digest `sha256:6091de65429ce10c5275a7a7ba27739b0d49cc4b635f182a27ea8f72cbb812d5`.

This documentation commit moves `main`; always verify the new exact head and its Actions before coding.

## Architecture already accepted

- Windows 11 x64, .NET 10 WinForms/tray, multiple independent HTTP/HTTPS forward proxies.
- No DIRECT fallback: outbound source `Bind()` + `IP_UNICAST_IF`, L2TP-bound custom DNS, split-tunnel/default-route guards.
- `Disconnected -> Dialing -> Verifying -> Ready`; no usable VPN context before real L2TP-bound HTTPS verification.
- Shared/dedicated leases, reconnect/keepalive ownership, exact accepted-session drain before higher ownership releases the VPN lease.
- ExistingWindowsProfile + private CustomEphemeral PBK; DPAPI-protected secrets and immediate managed password/PSK carrier cleanup after native handoff.
- Canonical/reparse-safe/exact-leaf/non-recursive CustomEphemeral orphan cleanup.
- Strict HTTP framing/request-line/authority grammar and fail-closed routing.
- Pooled 32 KiB transfer path, bounded concurrency/memory, no production forced GC.

## Latest accepted audit block

- #45 strict HTTP request-line separators: closed completed.
- #47 soak serialized-timestamp duration integrity: closed completed.
- #6 re-audit found existing cleanup/secret-carrier hardening sufficient; no duplicate production patch.
- #49 byte-exact verification origin-form request-target: closed completed.
- #50 canonical IDNA/A-label verification authority with explicit post-IDNA LDH grammar across DNS/TLS/HTTP: closed completed.

#49/#50 validation lineage is retained for audit only: `dev/issue49-probe-target`, run `33130832271`, source commit `1684718295944ecdb28216ae02c32365ff7b2b0c`. Production acceptance was reconstructed cleanly and merged through PR #51; do not merge the dev transport history.

## Real acceptance boundary

#2/#4/#5/#6/#7 still require real Windows 11 + real L2TP/operator evidence. #13 still requires a representative 12–24 h exact-binary soak. #11 remains a permanent latency/throughput/memory requirement. Hosted Actions smoke does not replace those boundaries.

## Continuation

Continue broad deterministic audits and coherent engineering blocks. For every new concrete finding: issue-first acceptance criteria, code/tests, permanent Windows PR CI, merge/rebase, exact-main build + handoff. Do not churn already-proven #45/#47/#49/#50 or audited #6 boundaries without a new finding.
