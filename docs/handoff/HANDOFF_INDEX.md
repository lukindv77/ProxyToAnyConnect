# Handoff package index — 2026-08-27

Use `docs/handoff/NEW_CHAT_PROMPT.md` as the first message in the new conversation.

## Read immediately

1. `docs/handoff/NEW_CHAT_PROMPT.md`
2. `docs/handoff/CURRENT_STATE.md`
3. `docs/handoff/AUDIT_SNAPSHOT.md`
4. `docs/handoff/ACTIVE_DEVELOPMENT.md`
5. `docs/handoff/FINAL_CI_STATUS.md`
6. `docs/handoff/ISSUES_SNAPSHOT.md`
7. `docs/handoff/MANIFEST.md`
8. `docs/requirements.md`
9. `docs/architecture.md`
10. `docs/memory-stability.md`
11. `docs/windows-integration-test.md`
12. `docs/windows-integration-evidence.md`
13. `docs/windows-soak-evidence.md`
14. `.github/workflows/build.yml`
15. `.github/workflows/handoff.yml`

Before changing architecture inspect current `src/ProxyToAnyConnect/{Configuration,Gui,Runtime,Proxy,Network,Vpn,Diagnostics}` and the corresponding self-tests.

## Critical facts

- Live protected GitHub `main` is authoritative over every handoff document/archive.
- #14 strict HTTP framing/request-smuggling boundary is completed/closed.
- #15 transactional drain-safe startup ownership is completed/closed.
- Current release-critical external issues are #2/#4/#5/#6/#7; #13 needs a representative 12–24 h exact-binary soak; #11 remains permanent performance/memory architecture work.
- RAS callback roots must never be released on ambiguous teardown; one drain attempt is bounded and timeout retains exact native ownership for retry.
- All GUI config/control generations are serialized, invalid legacy config can be repaired through an in-memory staged draft, and durable save is the desired-state publication boundary.
- Latest evidence supports expected egress per proxy endpoint plus separate direct-host expected IPv4; verify collector/validator/aggregate/CI coverage on live head.

## Handoff archive

`.github/workflows/handoff.yml` creates GitHub Actions artifact `ProxyToAnyConnect-handoff-<sha>` for every `main` push and manual run. Current archive content includes:

- `src/`, `tests/`, `tools/`, `docs/`, `.github/`;
- `README.md`, solution and `.gitignore`;
- generated `HANDOFF_BUILD_INFO.txt` with exact commit/ref/run/timestamp;
- generated `RECENT_COMMITS.tsv` (last 120 commits);
- generated `START_HERE.txt`.

Use the latest artifact whose embedded SHA matches the live head you intend to continue from. The workflow retention is 90 days.
