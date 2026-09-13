# Issue snapshot — 2026-09-13

Live GitHub issue state/comments are authoritative; refresh them at chat start.

## Open live issues

There are nine open issues:
- #2 — real Windows 11 + real L2TP E2E/fail-closed acceptance.
- #4 — real shared/dedicated multi-proxy lease behavior.
- #5 — real GUI/operator/profile/selective live acceptance.
- #6 — real CustomEphemeral auth/PSK/certificate/cleanup acceptance.
- #7 — real keepalive failure -> invalidation -> hangup -> cooldown -> reconnect acceptance.
- #11 — permanent low-latency/throughput/process-memory architecture requirement.
- #13 — representative 12–24 h exact-binary soak and resource-trend review.
- #94 — validate complete DNS NSCOUNT/ARCOUNT sections and exact message exhaustion before accepting routing evidence.
- #95 — fail closed when PPP local IPv4 maps ambiguously to multiple Windows interfaces.

## Active deterministic details

#94 branch `dev/issue94-dns-complete-message` head `525216f0c8b1470638d989affbffd6d3e0b89e17`. Run `33165671844` failed in transform transport before build because the generic DNS-section helper regex anchor matched zero times. Correct the anchor; do not weaken the acceptance policy.

#95 branch `dev/issue95-unique-interface` head `4e8f5e4d3a2625b76730d917b7fc293a4dc01476`. Run `33165867074` applied the intended two-file transform but failed Release compile with CS0019 at `VpnInterfaceResolver.cs:52` due to `List<VpnInterfaceInfo> ?? VpnInterfaceInfo[]`. Fix the type composition only, then rerun full aggregate.

## Latest completed deterministic hardening

#88, #89 and #92 are closed completed. #92 production commit is `f0763ec9337a0758c45a0add65e27d4b8f689482`, tree `e6928be6d0134330cf8b7637e475e69ff159cdd5`, with exact build #624 / `33165692687` and handoff #397 / `33165692716` green. Earlier completed hardening includes #52/#53/#54/#58/#59/#62/#63/#66/#67/#70/#71/#73/#75/#77/#79/#80/#85 and the earlier HTTP/DNS/RAS work; use live issue comments for exact lineage.

RAS x64 SDK-vs-managed layout audit also passed in run `33164715623`; no production issue/change was required.

Do not infer closure of #2/#4/#5/#6/#7/#13 from hosted Actions. Their remaining acceptance requires genuine external evidence.
