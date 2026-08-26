# Handoff package index — 2026-08-26

Start a new conversation with `docs/handoff/NEW_CHAT_PROMPT.md`.

Then read `CURRENT_STATE.md`, `AUDIT_SNAPSHOT.md`, `ISSUES_SNAPSHOT.md`, `MANIFEST.md`, `docs/requirements.md`, `docs/architecture.md`, `docs/memory-stability.md` and `docs/windows-integration-test.md`.

Current exact known code verdict before this final status-doc commit: build #272 on `b3fbe1f96c0ffa7d031cb72b81793ec6ea9c2858` compiles and passes the paired setup timing guard, then fails the new HTTP framing suite with Windows SocketException 10054 in `ExactContentLengthBoundsClientToOriginBytesAsync`. Issue #14 remains open. Issue #15 is the next confirmed lifecycle implementation after #14.

The GitHub Actions `handoff` workflow packages the exact commit into `ProxyToAnyConnect-handoff-<sha>` including the prompt, source, tests, docs, workflows and `HANDOFF_BUILD_INFO.txt`. Always use the latest artifact corresponding to the current `main` head.
