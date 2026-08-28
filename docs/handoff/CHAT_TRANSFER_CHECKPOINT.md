# Chat transfer checkpoint — 2026-08-28

Canonical startup prompt: `docs/handoff/NEW_CHAT_PROMPT.md`.

Accepted production baseline before this docs commit: `2e56f8f76efda9047ec83f3cd0e58aee395de322`, exact build `33097542082` green, handoff `33097542206` green.

Latest dev-green work: #49/#50 branch `dev/issue49-probe-target`, run `33130832271` green, validated four-file source commit `1684718295944ecdb28216ae02c32365ff7b2b0c`. Not yet production-accepted.

The handoff commit moves main. New chat must fetch live main and exact-head Actions first, then read `SESSION_2026-08-28.md` and live issue comments.
