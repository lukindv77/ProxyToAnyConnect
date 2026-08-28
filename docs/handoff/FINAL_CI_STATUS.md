# CI status — handoff checkpoint

## Last fully accepted production SHA before this handoff-doc commit

`ddbdc95e3b9e7080a31c2b631da1c1f187a1f1a3` (tree `4f11a13a1ac0d1839b86671dc0b7ccae7eed0d40`)

#49/#50 acceptance:
- dev validation run `33130832271`: success; source commit `1684718295944ecdb28216ae02c32365ff7b2b0c`;
- clean PR #51 head `c67a29a0c82a5eb6f5bdee4e20ece39c426ac652`, exactly four changed production/test files;
- permanent PR build #579 / run `33131957422`: identical-head attempt 2 success after attempt 1 exposed only previously documented DNS setup microbenchmark runner variance; the 1.25x policy and production code were not weakened;
- rebase merge produced `ddbdc95e3b9e7080a31c2b631da1c1f187a1f1a3`.

Exact-main CI on `ddbdc95e3b9e7080a31c2b631da1c1f187a1f1a3`:
- build #580 / run `33132200561`: success including evidence smokes, restore/build, aggregate self-tests, self-contained win-x64 publish, binary integrity manifest, ZIP and artifact upload;
- Windows artifact id `9670700014`, digest `sha256:83e91fbda614aeb804fcdecfc05bf589247582143c609a453be76f5e92acd76e`;
- handoff #375 / run `33132200498`: success;
- handoff artifact id `9670678196`, digest `sha256:6091de65429ce10c5275a7a7ba27739b0d49cc4b635f182a27ea8f72cbb812d5`.

This docs-only handoff commit moves `main`. A new chat must fetch the resulting exact head and require its own `build` + `handoff` verdict rather than calling it green from the older code SHA.
