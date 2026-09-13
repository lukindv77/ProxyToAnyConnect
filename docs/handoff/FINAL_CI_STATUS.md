# CI status — 2026-09-13 handoff checkpoint

## Last fully accepted production-changing SHA

`f0763ec9337a0758c45a0add65e27d4b8f689482` (tree `e6928be6d0134330cf8b7637e475e69ff159cdd5`).

Exact-main acceptance for that production tree:
- build #624 / run `33165692687`: success through Windows evidence smokes, restore/build, full aggregate self-tests, self-contained win-x64 publish, integrity manifest, ZIP and artifact upload;
- Windows artifact `9683511938`, digest `sha256:38bde53b760ceeb045d985058d7c5627f7275d189422c70fb277a45b5cab8247`;
- handoff #397 / run `33165692716`: success;
- handoff artifact `9683470928`, digest `sha256:2bd3366dd24a68e3dd3911438055ade9e9e51f13d235b0bb7f0b1c832e45368c`.

No performance/security threshold was widened. The proxy transfer hot path remains pooled 32 KiB and production has no forced GC.

## Later repository-only commits

After `f0763ec...`, `d6100bf75ae46c2c7a3e45af0a077c7955ede179` temporarily added a one-time Actions storage cleanup workflow and `4a653627e0aa0a22967a077821470c133980c675` removed it with `[skip ci]`, restoring the same production tree `e6928be6...`.

The 2026-09-13 transition docs then moved `main` again. Therefore a new chat must fetch the exact live docs head and its own `build` + `handoff` results; never call that newer commit green solely because the production tree had older successful CI.

## Active dev CI failures to resume

- #94 run `33165671844`: failure in `Apply and verify issue 94 transform` before build; transform anchor `generic DNS section parser helper` matched zero times. No behavioral compile/test verdict yet.
- #95 run `33165867074`: transform stage passed; Release build failed with CS0019 at `VpnInterfaceResolver.cs:52` because `List<VpnInterfaceInfo> ?? VpnInterfaceInfo[]` operands are incompatible. Aggregate tests were skipped.

Both require a corrected dev run with full Release build + aggregate before any clean production reconstruction or PR.
