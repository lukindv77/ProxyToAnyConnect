# Final CI status at handoff — 2026-08-27

Live protected `main` is authoritative. A source block is called accepted only after an exact-head Windows build has completed successfully.

## Authoritative accepted checkpoint before the current runtime-independence block

Commit `03c372e90cd39b52cb261acd59e608e4caef19e5` — `test: prove caller cancellation wins persisted consumer faults`.

Windows build #511, run `33047798858`, completed successfully:

- integration/evidence PowerShell smoke: PASS;
- exact executable/process identity and soak-tool smoke: PASS;
- restore/build: PASS;
- aggregate self-tests: PASS;
- self-contained win-x64 publish: PASS;
- binary identity/ZIP/artifact upload: PASS.

Artifact `9636363559`, `ProxyToAnyConnect-win-x64`, digest:
`sha256:a604c88db7c8c1e2a5ad168571d6016f45af7eb1f16564f795be17779a108273`.

This accepted checkpoint includes staged repair of invalid loaded configuration, unified persisted configuration consumers, caller-cancellation precedence across consumer faults, serialized GUI Start/Pause generations, L2TP dialog-owned profile-helper drain, streaming/self-validating Windows soak evidence correlation, bounded RAS hangup attempts and the previously accepted fail-closed/lifecycle work.

## Current development after #511

The following commits are intentionally newer than the accepted checkpoint and require a new exact-head Windows verdict before they are called accepted:

- `c111de381280c48fb06c26d32625a78d5e7456a8` — process memory health now exposes live RAS callback-root count and drains its worker/CTS even when a cancellation callback faults;
- `039ab936e0e88463992389df1bd342bbc355134a` — regression for process-memory cleanup fault ownership and RAS-root diagnostics;
- coordinator independence work: dispose every independent owner inside one dependency phase concurrently while keeping proxy-before-VPN phase ordering and deterministic input-order error aggregation.

The coordinator change is intended to remove N× teardown-timeout behavior when several dedicated L2TP groups are shutting down or being selectively replaced. One stuck RAS owner must not add its full drain timeout to every unrelated group.

## Remaining release boundary

Hosted CI is not a substitute for real Windows 11 + real L2TP acceptance. Issues #2/#4/#5/#6/#7 still require endpoint-backed verification. Issue #13 additionally requires a representative 12–24h exact-binary soak with matching `process.memory.*` logs and external soak-series correlation.
