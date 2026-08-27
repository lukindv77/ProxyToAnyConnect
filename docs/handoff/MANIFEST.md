# ProxyToAnyConnect handoff manifest

Live GitHub `main` is authoritative. This manifest defines the minimum reading and archive behavior for continuing development in a new chat without losing architecture, audit conclusions or current engineering goals.

## Required reading

1. `docs/handoff/NEW_CHAT_PROMPT.md`
2. `docs/handoff/CURRENT_STATE.md`
3. `docs/handoff/AUDIT_SNAPSHOT.md`
4. `docs/handoff/ACTIVE_DEVELOPMENT.md`
5. `docs/handoff/FINAL_CI_STATUS.md`
6. `docs/handoff/ISSUES_SNAPSHOT.md`
7. `docs/handoff/HANDOFF_INDEX.md`
8. `docs/requirements.md`
9. `docs/architecture.md`
10. `docs/memory-stability.md`
11. `docs/windows-integration-test.md`
12. `docs/windows-integration-evidence.md`
13. `docs/windows-soak-evidence.md`
14. `README.md`
15. `.github/workflows/build.yml`
16. `.github/workflows/handoff.yml`

Before architecture changes inspect current `Configuration`, `Gui`, `Runtime`, `Proxy`, `Network`, `Vpn`, `Diagnostics`, `tools` and `tests/ProxyToAnyConnect.SelfTests`.

## Live facts the next chat must query

- exact current `main` SHA;
- exact-head `build` and `handoff` conclusions;
- latest open issue states/comments (#2/#4/#5/#6/#7/#11/#13);
- commits after the archive SHA;
- latest build and handoff artifacts.

Never infer green CI from an older head.

## Handoff archive contract

`.github/workflows/handoff.yml` creates GitHub Actions artifact `ProxyToAnyConnect-handoff-<github.sha>` from the exact checked-out commit. It contains:

- `src`, `tests`, `tools`, `docs`, `.github`;
- README, solution and `.gitignore`;
- `HANDOFF_BUILD_INFO.txt` with repository, exact SHA/ref/run/UTC timestamp and startup prompt path;
- `RECENT_COMMITS.tsv` containing the latest 120 commits from that checkout;
- `START_HERE.txt` pointing at the authoritative handoff documents.

`bin`, `obj` and `.git` are excluded. Artifact retention is 90 days.

## Baseline immediately before final handoff-document packaging

Substantive commit `4b100f3bb6c744b08918ce122ab75982fa263740` passed Windows build #534 and handoff #340. Build artifact `9637762202` digest: `sha256:be01041fefa07c4fe4dd39f4a02e5c038b9e729b97049a7da4880d685aedf239`.

The final handoff-document commits move the live SHA forward. Their archive embeds the exact final SHA in `HANDOFF_BUILD_INFO.txt`; the new chat must verify the live head instead of assuming the baseline SHA above is current.

## Current engineering interpretation

- #14 HTTP framing/request-smuggling and #15 transactional proxy startup are completed and closed.
- Core proxy/L2TP/GUI/runtime architecture is implemented and deeply self-tested.
- Major remaining release boundary is real Windows 11 + real L2TP endpoint acceptance (#2/#4/#5/#6/#7), not missing proxy architecture.
- #13 additionally requires a representative 12–24 h exact-binary soak with external/native resource samples correlated to application `process.memory.*` records.
- #11 remains a permanent performance/memory requirement; memory hardening may not weaken fail-closed behavior or measurably harm latency/throughput.
- Latest evidence work adds per-proxy expected public IPv4 and independent direct-host expected IPv4. The next chat must verify end-to-end enforcement across Invoke/Test/Complete scripts and hosted smoke.

## Immediate continuation

1. Fetch live head/actions/issues and read this package.
2. Verify/complete per-proxy + direct expected-egress evidence validation across the whole evidence toolchain.
3. Keep exact-head Windows CI green after every substantial block.
4. Continue deterministic ownership/stress/performance work without duplicating already accepted #14/#15/RAS bounded-drain work.
5. Execute real #2/#4/#5/#6/#7 acceptance and #13 12–24 h soak when the endpoint/environment is available.
