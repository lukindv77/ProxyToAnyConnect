# ProxyToAnyConnect handoff manifest

This manifest defines the minimum material a new development chat must inspect before continuing. Live GitHub is authoritative.

## Required reading

1. `docs/handoff/NEW_CHAT_PROMPT.md` — first-message prompt for the new chat.
2. `docs/handoff/CURRENT_STATE.md` — exact implementation/CI snapshot.
3. `docs/handoff/AUDIT_SNAPSHOT.md` — engineering findings, races and architectural rationale.
4. `docs/handoff/ISSUES_SNAPSHOT.md` — roadmap snapshot.
5. `docs/requirements.md` — normative product/runtime requirements.
6. `docs/architecture.md` — system architecture.
7. `docs/memory-stability.md` — deterministic ownership, bounded memory and latency-neutral optimization rules.
8. `docs/windows-integration-test.md` — real Windows/L2TP validation plan.
9. `README.md`.
10. `.github/workflows/build.yml` and `.github/workflows/handoff.yml`.

## Code areas to inspect before architecture changes

- `src/ProxyToAnyConnect/Configuration/`
- `src/ProxyToAnyConnect/Gui/`
- `src/ProxyToAnyConnect/Runtime/`
- `src/ProxyToAnyConnect/Proxy/`
- `src/ProxyToAnyConnect/Network/`
- `src/ProxyToAnyConnect/Vpn/`
- `src/ProxyToAnyConnect/Diagnostics/`
- `tests/ProxyToAnyConnect.SelfTests/`

## Live state the next chat must query

- exact current `main` SHA;
- exact-head `build` and `handoff` Actions status;
- current open/closed issues and newest comments;
- commits made after this snapshot;
- latest build and handoff artifacts.

Never infer green CI from an older head.

## Handoff source archive

`.github/workflows/handoff.yml` runs on every `main` push and creates an Actions artifact:

`ProxyToAnyConnect-handoff-<github.sha>`

The ZIP contains the exact checked-out revision of:

- `src/`
- `tests/`
- `docs/` including this manifest and `NEW_CHAT_PROMPT.md`
- `.github/`
- `README.md`
- `ProxyToAnyConnect.sln`
- `.gitignore`
- generated `HANDOFF_BUILD_INFO.txt` with repository, exact commit SHA, ref, workflow run, creation UTC, startup prompt path and read-first list.

`bin`, `obj` and `.git` are intentionally excluded. The normal `build` workflow separately creates the self-contained win-x64 application ZIP when its build/self-tests pass.

## Snapshot code/CI baseline immediately before this handoff docs commit

- `f9db53f074d6740296e46452077622099b6f64ff` — HTTP framing/request-smuggling production hardening.
- `71a93e5d529225adfd0e1b5125a4302d81c58da5` — timing benchmark sample stabilization only; current code head before docs packaging.
- build #270 on `f9db53f...`: failed in `ProxySetupTimingSelfTests` at 1.66x vs 1.25x limit after successful compilation and earlier suites.
- build #271 on `71a93e5d...`: failed in the same test at 1.75x vs 1.25x limit. Compilation succeeded; framing suite is later in runner and has not yet executed in exact Windows CI.
- handoff #83 on `71a93e5d...`: success and source archive uploaded.

The final handoff documentation commit will have its own `build`/`handoff` runs. The next chat must inspect those exact runs; docs-only commit does not magically make the inherited code-level timing failure green.

## Immediate continuation order

1. Resolve `ProxySetupTimingSelfTests` / current parser performance verdict without casually widening the 1.25x threshold.
2. Reach and execute `ProxyHttpFramingSelfTests` on Windows CI; complete #14 only if semantic + performance gates pass.
3. Implement #15 transactional `ProxyInstanceRuntime.StartAsync` ownership: cancel -> drain exact run -> clear same generation -> dispose CTS -> release lease once.
4. Continue #11/#13 performance/memory/lifetime hardening and real Windows #2/#4/#5/#6/#7 acceptance.
