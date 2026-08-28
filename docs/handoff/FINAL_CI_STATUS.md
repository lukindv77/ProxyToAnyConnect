# CI status — handoff checkpoint

## Last fully accepted production SHA before handoff-doc commit

`2e56f8f76efda9047ec83f3cd0e58aee395de322`

- permanent PR #48 build run `33097205158`: success;
- exact-main build #577 / run `33097542082`: success;
- exact-main handoff #373 / run `33097542206`: success;
- handoff artifact id `9657003054`, digest `sha256:a7fcf633740e12b2fa2dcde388567b7038ea48b4686a725986e0c517c40394f0`.

The docs/archive handoff commit moves main, so the next chat must fetch the new exact head and require its own build/handoff verdict rather than relying on these older exact-head runs.

## #49/#50 dev validation

- run `33130832271`: success across exact source transforms, full Windows aggregate self-tests and source publish;
- validated source commit `1684718295944ecdb28216ae02c32365ff7b2b0c`;
- source commit changes exactly four expected production/test files and does not include dev workflow/transport files.

This is dev validation, not production acceptance. #49/#50 remain open until clean permanent PR and exact-main CI.
