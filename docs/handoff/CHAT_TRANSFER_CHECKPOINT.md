# Chat transfer checkpoint — 2026-09-13

Canonical startup prompt: `docs/handoff/NEW_CHAT_PROMPT.md`.

## Production checkpoint

Last production-changing main: `f0763ec9337a0758c45a0add65e27d4b8f689482`, tree `e6928be6d0134330cf8b7637e475e69ff159cdd5`.

Exact production-tree acceptance:
- build #624 / `33165692687` green; artifact `9683511938`, digest `sha256:38bde53b760ceeb045d985058d7c5627f7275d189422c70fb277a45b5cab8247`;
- handoff #397 / `33165692716` green; artifact `9683470928`, digest `sha256:2bd3366dd24a68e3dd3911438055ade9e9e51f13d235b0bb7f0b1c832e45368c`.

#88/#89/#92 are completed. RAS native layout audit run `33164715623` is green and found no managed/SDK ABI mismatch.

## Work to resume first

1. #94: branch `dev/issue94-dns-complete-message`, head `525216f0c8b1470638d989affbffd6d3e0b89e17`; run `33165671844` failed before build because transform anchor `generic DNS section parser helper` matched zero times. Repair validation transport only, preserving complete-message acceptance/tests.
2. #95: branch `dev/issue95-unique-interface`, head `4e8f5e4d3a2625b76730d917b7fc293a4dc01476`; transform passed, Release build in run `33165867074` failed CS0019 at `VpnInterfaceResolver.cs:52` (`List<VpnInterfaceInfo> ?? VpnInterfaceInfo[]`). Fix the type composition only and rerun full aggregate.
3. For each green dev result: collect validated blobs, clean reconstruction on exact then-current main, permanent PR CI, rebase merge, exact-main build + handoff, issue lineage, close completed.

Open live issues: #2/#4/#5/#6/#7/#11/#13/#94/#95.

Transition docs themselves move `main`. A new chat must fetch live main/tree and exact-head `build`/`handoff` before continuing. Never fabricate real Windows/L2TP or 12–24 h soak evidence.
