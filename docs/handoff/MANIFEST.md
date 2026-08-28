# Handoff manifest — 2026-08-28

The `handoff` workflow archives the full handoff-relevant repository surface from exact `main`: `src/`, `tests/`, `tools/`, `docs/`, `.github/`, README, solution, `.gitignore`, and root `START_NEW_CHAT_PROMPT.md`. It also writes exact commit/ref/workflow metadata, recent commits and `START_HERE.txt`.

Canonical prompt: `docs/handoff/NEW_CHAT_PROMPT.md`.

Last accepted production baseline before this docs commit: `2e56f8f76efda9047ec83f3cd0e58aee395de322`.

Dev-green #49/#50 source is intentionally **not overlaid** on production main; it remains in `dev/issue49-probe-target` at source commit `1684718295944ecdb28216ae02c32365ff7b2b0c` until clean permanent acceptance.

Always use live GitHub plus the handoff artifact whose embedded commit matches the current head being discussed.
