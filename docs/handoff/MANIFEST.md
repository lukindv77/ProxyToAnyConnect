# ProxyToAnyConnect handoff manifest

Live GitHub is authoritative. This manifest defines the minimum reading and archive behavior for continuing in a new chat.

## Required reading

1. `docs/handoff/NEW_CHAT_PROMPT.md`
2. `docs/handoff/CURRENT_STATE.md`
3. `docs/handoff/FINAL_CI_STATUS.md`
4. `docs/handoff/AUDIT_SNAPSHOT.md`
5. `docs/handoff/ISSUES_SNAPSHOT.md`
6. `docs/handoff/HANDOFF_INDEX.md`
7. `docs/requirements.md`
8. `docs/architecture.md`
9. `docs/memory-stability.md`
10. `docs/windows-integration-test.md`
11. `README.md`
12. `.github/workflows/build.yml`
13. `.github/workflows/handoff.yml`

Before architecture changes inspect current `Configuration`, `Gui`, `Runtime`, `Proxy`, `Network`, `Vpn`, `Diagnostics` and `tests/ProxyToAnyConnect.SelfTests`.

## Live facts the next chat must query

- exact current `main` SHA;
- exact-head build/handoff conclusions;
- latest issue states/comments;
- commits after this package;
- latest artifacts.

Never infer green CI from older heads.

## Handoff archive

`.github/workflows/handoff.yml` creates GitHub Actions artifact `ProxyToAnyConnect-handoff-<github.sha>` from the exact checked-out commit. It contains `src`, `tests`, `docs`, `.github`, README, solution, `.gitignore` and generated `HANDOFF_BUILD_INFO.txt` with repository, exact commit, ref, workflow run, UTC timestamp and startup prompt path. `bin`, `obj`, `.git` are excluded.

Observed archive examples during preparation:

- handoff #84 / `b3fbe1f...`: success, artifact id 9611924335, SHA-256 `5b9307c6a184f3a6bf4ddc47b60af6569ea4a3611940f7cb7d9b527eaa72aa6b`;
- handoff #85 / `b304a433...`: success, artifact id 9612150421, SHA-256 `a25e61eb00c969fa96a0f56b92c4d6b9f621b0fb5386f6a6f1f18ea7855a042a`.

This final handoff-doc commit will create a newer artifact. New chat must use the latest artifact corresponding to live `main`, not the historical ids above.

## Current code/CI interpretation

Production HTTP framing hardening is in `f9db53f074d6740296e46452077622099b6f64ff`.

Hosted Windows results on docs-only heads with unchanged production/parser code are intentionally both preserved:

- build #272: paired setup timing PASS (parser 0.98x, origin 0.80x), then framing exact-CL test failed with SocketException 10054 while reading proxy response;
- build #273: paired setup timing FAIL (parser 1.79x vs 1.25x limit), suite stopped before framing test.

Therefore the next chat must first make `ProxySetupTimingSelfTests` reproducible and semantically fair without simply widening the 1.25x policy, then resolve the already-observed framing reset while preserving the invariant that no bytes after declared Content-Length reach origin.

Issue #14 remains open. Issue #15 transactional proxy startup ownership remains the next confirmed lifecycle block after #14 validation.

## Immediate continuation

1. Fetch live state.
2. Stabilize/harden the paired timing benchmark methodology and predecessor equivalence.
3. Reproduce/fix framing reset 10054 without weakening smuggling boundary.
4. Get exact-head Windows CI through framing suite and finish #14 only after acceptance.
5. Implement #15 cancel -> exact run drain -> same-generation clear -> CTS dispose -> lease release once.
6. Continue #11/#13 and real Windows #2/#4/#5/#6/#7 acceptance.
