# Chat transfer checkpoint — 2026-08-27

This file marks the handoff boundary requested by the user before moving development to a new ChatGPT conversation.

## Authoritative continuation rule

1. Live protected GitHub `main` is authoritative.
2. Start the new chat with `docs/handoff/NEW_CHAT_PROMPT.md`.
3. Read `CURRENT_STATE.md`, `AUDIT_SNAPSHOT.md`, `ACTIVE_DEVELOPMENT.md`, `FINAL_CI_STATUS.md`, `ISSUES_SNAPSHOT.md` and `MANIFEST.md` before changing code.
4. Fetch current open issues/comments and exact-head `build` + `handoff` Actions.
5. If the archive SHA differs from live `main`, inspect commits after the archive before coding.

## Last substantive green baseline before handoff-only commits

- commit: `4b100f3bb6c744b08918ce122ab75982fa263740`
- Windows build: #534 / run `33051353263` — success
- build artifact: `9637762202`
- build artifact digest: `sha256:be01041fefa07c4fe4dd39f4a02e5c038b9e729b97049a7da4880d685aedf239`
- handoff #340 for the same SHA — success

The final handoff-document SHA is intentionally newer. Its `handoff` workflow artifact contains the exact SHA in `HANDOFF_BUILD_INFO.txt`.

## Archive contract

Artifact name: `ProxyToAnyConnect-handoff-<sha>`.

It contains the current source, tests, Windows evidence/soak tools, architecture/requirements/handoff documents, workflows, recent 120-commit log and exact archive identity. Retention is 90 days.

## Immediate engineering continuation

- Verify the latest per-proxy expected proxy egress + direct-host expected egress work end-to-end across Invoke/Test/Complete evidence scripts and hosted positive/negative smoke.
- Preserve all fail-closed, source/interface binding, L2TP-only DNS, route guard, transactional startup/drain and native callback-root safety invariants.
- Continue broad deterministic #11/#13 hardening where a real endpoint is not required.
- Real release acceptance still requires Windows 11 + real L2TP endpoint for #2/#4/#5/#6/#7 plus a representative 12–24 h exact-binary #13 soak.
