# Active development — 2026-09-13

## Current accepted production baseline

Last production-changing commit: `f0763ec9337a0758c45a0add65e27d4b8f689482`, tree `e6928be6d0134330cf8b7637e475e69ff159cdd5`.

Exact acceptance for that production tree:
- build #624 / run `33165692687`: success; artifact `9683511938`, digest `sha256:38bde53b760ceeb045d985058d7c5627f7275d189422c70fb277a45b5cab8247`;
- handoff #397 / run `33165692716`: success; artifact `9683470928`, digest `sha256:2bd3366dd24a68e3dd3911438055ade9e9e51f13d235b0bb7f0b1c832e45368c`.

Transition documentation now moves `main`; always refresh exact live head before code.

## Last completed engineering sequence

- #88 — verification HTTP grammar hardening, closed completed.
- #89 — reject ambiguous exact-owner CNAME/A and multiple-CNAME RRsets, closed completed.
- #92 — reject non-QUERY DNS OPCODE and malformed exact-owner A/IN RDATA, closed completed in production `f0763ec9337a0758c45a0add65e27d4b8f689482`.
- RAS x64 native-layout audit: Windows SDK C++ and managed probe matched all 12 checked size/offset values; run `33164715623` green, so no native production change was justified.

## Current engineering priority

### #94 — complete DNS message sections

Issue is open. Development branch `dev/issue94-dns-complete-message` head `525216f0c8b1470638d989affbffd6d3e0b89e17`.

Run `33165671844` failed before source build: the transform expected exactly one `generic DNS section parser helper` anchor and found zero. Exact-base/blob guards had passed. Treat this as validation transport only. Fix the anchor against actual post-#92 source, preserve policy/tests, rerun Release build + full aggregate.

### #95 — unique PPP IPv4 interface ownership

Issue is open. Development branch `dev/issue95-unique-interface` head `4e8f5e4d3a2625b76730d917b7fc293a4dc01476`.

Run `33165867074` applied/staged the intended source/tests successfully, then Release compile failed with CS0019 in `VpnInterfaceResolver.cs:52`: null-coalescing operands were `List<VpnInterfaceInfo>` and `VpnInterfaceInfo[]`. Fix only the type composition while preserving exactly-one-candidate semantics and zero/multiple fail-closed tests; rerun the full aggregate.

For each after a green dev run: collect validated source blobs -> reconstruct clean production commit on exact live main -> verify expected source/test surface only -> permanent Windows PR CI -> rebase merge -> exact-main build + handoff -> lineage comment -> close completed.

## Continue broad audit after #94/#95

Highest-value independent blocks remain proxy/session shutdown and response commitment, RAS generation/callback/projection ownership, DNS TCP/cache/deadline framing, and process-wide bounded state under #11. Create a new issue only for a concrete reproducible defect.

## External acceptance remains separate

Open live issues: #2/#4/#5/#6/#7/#11/#13/#94/#95. Do not close #2/#4/#5/#6/#7 without real Windows/L2TP/operator evidence. Do not close #13 without representative 12–24 h exact-binary soak evidence. #11 remains permanently open as the latency/throughput/memory architecture requirement.
