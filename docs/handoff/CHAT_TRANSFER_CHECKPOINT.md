# Chat transfer checkpoint — 2026-08-28

Canonical startup prompt: `docs/handoff/NEW_CHAT_PROMPT.md`.

Accepted production code baseline before this docs commit:
- main `ddbdc95e3b9e7080a31c2b631da1c1f187a1f1a3`;
- tree `4f11a13a1ac0d1839b86671dc0b7ccae7eed0d40`;
- exact build #580 / `33132200561` green through artifact publication;
- exact handoff #375 / `33132200498` green.

#49/#50 are closed completed after clean PR #51. Their earlier dev branch `dev/issue49-probe-target` and source commit `1684718295944ecdb28216ae02c32365ff7b2b0c` are validation lineage only, not a branch to merge wholesale.

Open external boundaries remain #2/#4/#5/#6/#7/#13; #11 remains a permanent performance/memory architecture requirement.

This handoff-doc commit moves `main`. The next chat must fetch live main/tree and exact-head `build`/`handoff` first, then read the handoff docs and current issue comments before continuing a broad deterministic audit block.
