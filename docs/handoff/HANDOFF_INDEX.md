# Handoff package index — 2026-08-26

Use `docs/handoff/NEW_CHAT_PROMPT.md` as the first message in the new conversation.

Read immediately after it:

- `docs/handoff/CURRENT_STATE.md`
- `docs/handoff/FINAL_CI_STATUS.md`
- `docs/handoff/AUDIT_SNAPSHOT.md`
- `docs/handoff/ISSUES_SNAPSHOT.md`
- `docs/handoff/MANIFEST.md`
- `docs/requirements.md`
- `docs/architecture.md`
- `docs/memory-stability.md`
- `docs/windows-integration-test.md`

Critical handoff facts:

- issue #14 strict HTTP framing code is present in `f9db53f...`;
- build #272 on a docs-only head passed the paired setup timing gate then exposed framing SocketException 10054;
- build #273 on the next docs-only head, with unchanged production/test code, failed the same timing gate at 1.79x, proving current hosted-runner measurement instability;
- next chat must first make the timing gate reproducible/honest without simply relaxing the 1.25x policy, then resolve the framing close/reset behavior while preserving exact Content-Length smuggling boundary;
- issue #15 transactional startup ownership is the next confirmed lifecycle implementation block after #14 validation.

`.github/workflows/handoff.yml` packages the exact current commit into GitHub Actions artifact `ProxyToAnyConnect-handoff-<sha>` with source, tests, docs, workflows and `HANDOFF_BUILD_INFO.txt`. Always use the latest artifact for current `main` head.
