# ProxyToAnyConnect handoff manifest

This file defines the minimum repository material that a new development conversation must inspect before continuing.

## Required handoff documents

1. `docs/handoff/NEW_CHAT_PROMPT.md` — first-message prompt for a new ChatGPT conversation.
2. `docs/handoff/CURRENT_STATE.md` — implementation/product state snapshot.
3. `docs/handoff/AUDIT_SNAPSHOT.md` — important audit findings, discovered races/ABI issues and the reasons behind current architecture.
4. `docs/handoff/ISSUES_SNAPSHOT.md` — roadmap issue state/remaining acceptance snapshot.
5. `docs/requirements.md` — authoritative product/runtime requirements.
6. `docs/architecture.md` — current system architecture.
7. `docs/memory-stability.md` — long-run ownership/memory rules and latency-neutral optimization invariant.
8. `docs/windows-integration-test.md` — real Windows validation procedure.
9. `README.md` — repository-level overview and invariants.

## Code areas that must be reviewed before changing architecture

- `src/ProxyToAnyConnect/Program.cs`
- `src/ProxyToAnyConnect/Configuration/`
- `src/ProxyToAnyConnect/Gui/`
- `src/ProxyToAnyConnect/Runtime/`
- `src/ProxyToAnyConnect/Proxy/`
- `src/ProxyToAnyConnect/Network/`
- `src/ProxyToAnyConnect/Vpn/`
- `src/ProxyToAnyConnect/Diagnostics/`
- `tests/ProxyToAnyConnect.SelfTests/`
- `.github/workflows/build.yml`
- `.github/workflows/handoff.yml`

## Live GitHub state that cannot be frozen only in documentation

A new conversation must query GitHub for:

- latest `main` commit SHA;
- latest GitHub Actions run for that head;
- current open/closed state and comments of roadmap issues;
- commits made after this handoff snapshot;
- current workflow artifacts.

The repository and current CI always outrank stale snapshot metadata.

## Handoff archive contents

The GitHub Actions `handoff` workflow creates a ZIP artifact from the exact checked-out commit. The archive should contain:

- `src/`
- `tests/`
- `docs/`
- `.github/`
- `README.md`
- `ProxyToAnyConnect.sln`
- root configuration/support text files needed to understand/build the repository
- generated `HANDOFF_BUILD_INFO.txt` containing commit SHA, run number and UTC creation time

Build output directories (`bin`, `obj`) and `.git` are intentionally not included in the source handoff archive. The normal build workflow separately creates the self-contained Windows executable ZIP.

## Baseline validation at handoff creation

The last code-level commit explicitly verified before handoff documentation packaging was:

`5c3955fce4896c0a02b78c021eaccd8078ada8f4` — `fix: own RAS monitor lifetime per VPN session`

GitHub Actions run #181 completed successfully through Build, Self-tests, self-contained `win-x64` publish, ZIP and artifact upload.

Subsequent handoff documentation/workflow commits must have their own current CI checked before starting the next chat.
