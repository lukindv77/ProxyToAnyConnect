# ProxyToAnyConnect — current handoff state

> Updated 2026-09-13. Live GitHub is authoritative over this snapshot.

## Current repository checkpoint

- Current `main` at handoff preparation start: `1d2c93e83c983ad2cf373dd1f7c3e9c47e359508`.
- Tree at that commit: `7e9b26766abbf10ccb5322c6d9b5b266bce6a8d3`.
- Last production-changing commit: `f0763ec9337a0758c45a0add65e27d4b8f689482` (#92), production tree `e6928be6d0134330cf8b7637e475e69ff159cdd5`.
- Exact production-tree build #624 / run `33165692687`: success; artifact `9683511938`, digest `sha256:38bde53b760ceeb045d985058d7c5627f7275d189422c70fb277a45b5cab8247`.
- Exact production-tree handoff #397 / run `33165692716`: success; artifact `9683470928`, digest `sha256:2bd3366dd24a68e3dd3911438055ade9e9e51f13d235b0bb7f0b1c832e45368c`.
- Two maintenance commits after #92 temporarily added and then removed a one-time Actions-storage cleanup workflow, restoring the exact #92 production tree before the new transition docs were added.

The handoff-doc changes move `main`; the next chat must fetch live `main` and require build + handoff on that exact docs head before treating the transport checkpoint as accepted.

## Accepted architecture/invariants

- Windows 11 x64, .NET 10 WinForms/tray; multiple independent HTTP/HTTPS forward proxies.
- Absolutely no DIRECT fallback: outbound source `Bind()` + `IP_UNICAST_IF`, custom L2TP-bound DNS, split-tunnel/default-route guards.
- VPN lifecycle `Disconnected -> Dialing -> Verifying -> Ready`; no usable context before real L2TP-bound HTTPS verification.
- Shared/dedicated leases and exact accepted-session drain before higher ownership releases a VPN lease.
- ExistingWindowsProfile + private CustomEphemeral PBK; DPAPI-protected secrets, unmanaged plaintext zero-before-free, fixed-width RAS limits and prompt managed secret-carrier release.
- Strict HTTP request/authority/framing/response-commit grammar and strict verification response framing.
- DNS exact query/owner binding, canonical authority identity, monotonic TTL, bounded cache and L2TP-only transport.
- Pooled 32 KiB transfer path, bounded concurrency/memory, no production forced GC, unchanged 1.25x security/performance policy.
- Terminal cleanup preserves failed exact native owners for retry without reopening disposed runtime state.

## Latest completed deterministic hardening

#88, #89 and #92 are closed completed. #92 rejects non-QUERY DNS OPCODE and malformed exact-owner A/IN RDATA. RAS x64 ABI/layout audit run `33164715623` also passed with Windows SDK and managed layouts matching all checked values; no production interop change was required.

## Active deterministic work

- #94 `Validate complete DNS message sections before accepting routing evidence` is open. Branch `dev/issue94-dns-complete-message` head `525216f0c8b1470638d989affbffd6d3e0b89e17`; run `33165671844` failed in validation transport before build because the transform regex could not find the generic DNS-section helper anchor.
- #95 `Fail closed on ambiguous PPP IPv4 to Windows interface mapping` is open. Branch `dev/issue95-unique-interface` head `4e8f5e4d3a2625b76730d917b7fc293a4dc01476`; run `33165867074` applied the transform but Release compile failed with CS0019 at `VpnInterfaceResolver.cs:52` because `List<VpnInterfaceInfo> ?? VpnInterfaceInfo[]` has incompatible operand types.

Immediate continuation: fix #94 transform transport without altering policy; fix only the #95 compile-type composition; rerun full Windows validation; then clean reconstruction on exact live main, permanent PR CI, rebase merge, exact-main build + handoff and issue lineage/closure.

## Real acceptance boundary

Open live issues are exactly #2/#4/#5/#6/#7/#11/#13/#94/#95. #2/#4/#5/#6/#7 require genuine Windows 11 + real L2TP/operator evidence. #13 requires representative 12–24 h exact-binary soak evidence. #11 remains the permanent latency/throughput/process-memory architecture contract. Hosted Actions smoke must never be reported as those real-world acceptance results.
