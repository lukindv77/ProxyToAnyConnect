# Handoff manifest — 2026-09-13

The `handoff` workflow archives the full handoff-relevant repository surface from exact `main`: `src/`, `tests/`, `tools/`, `docs/`, `.github/`, README, solution, `.gitignore`, and root `START_NEW_CHAT_PROMPT.md`. It also writes exact commit/ref/workflow metadata, recent commits and `START_HERE.txt`.

Canonical prompt: `docs/handoff/NEW_CHAT_PROMPT.md`.

Current transition record: `docs/handoff/SESSION_2026-09-13.md`.

Last production-changing baseline before these handoff docs: `f0763ec9337a0758c45a0add65e27d4b8f689482`, tree `e6928be6d0134330cf8b7637e475e69ff159cdd5`, with exact build #624 / run `33165692687` and handoff #397 / run `33165692716` green.

Active deterministic development is intentionally **not overlaid** on production main. Resume #94 from `dev/issue94-dns-complete-message` and #95 from `dev/issue95-unique-interface`; each must obtain a green dev validation and then be reconstructed cleanly on the exact live main before permanent acceptance.

Always use live GitHub plus the handoff artifact whose embedded commit matches the exact head being discussed. Never use hosted smoke as a substitute for the real Windows/L2TP or 12–24 h soak evidence required by external acceptance issues.
