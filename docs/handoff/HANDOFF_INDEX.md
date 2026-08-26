# Handoff package index — 2026-08-26

This repository state is prepared for continuation in a new ChatGPT conversation without restarting the design discussion.

Start with `docs/handoff/NEW_CHAT_PROMPT.md`.

Read next:

- `docs/handoff/CURRENT_STATE.md`
- `docs/handoff/AUDIT_SNAPSHOT.md`
- `docs/handoff/ISSUES_SNAPSHOT.md`
- `docs/handoff/MANIFEST.md`
- `docs/requirements.md`
- `docs/architecture.md`
- `docs/memory-stability.md`
- `docs/windows-integration-test.md`

The GitHub Actions `handoff` workflow creates `ProxyToAnyConnect-handoff-<sha>` from the exact commit and includes this prompt/documentation plus source/tests/workflows and `HANDOFF_BUILD_INFO.txt`.

Important handoff condition: the code baseline immediately before this package is **not green**. Build #271 compiled successfully but failed `ProxySetupTimingSelfTests` at 1.75x vs the 1.25x limit. Do not lose or hide this blocker in the new chat. Issue #14 code is present but its new framing suite has not yet run to completion on Windows CI; issue #15 is the next confirmed lifecycle audit bug after #14 is validated.
