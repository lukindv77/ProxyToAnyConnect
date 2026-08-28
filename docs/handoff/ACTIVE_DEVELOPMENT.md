# Active development — 2026-08-28

## Current production baseline before this docs commit

`main` `5811900dfbf7488bd8ac53af20348c462681eeef`, tree `e44bf16408da3abade0c0f4d04708e6fd5ccd4ac`.

Exact acceptance:
- build #616 / `33152272544`: success; artifact `9678213447`, digest `sha256:bd31b7f143d11c56cfc6794e55760e156341ca07bdd8fcbb52691d5010e9c1e7`;
- handoff #393 / `33152272516`: success; artifact `9678172387`, digest `sha256:bef544b5997914274001b50fce35684dcdd633d44c6230de654c0769db0a77c9`.

## Last completed engineering sequence

- #79: configured outbound acquisition deadline is now real, with owner/VPN cancellation precedence preserved and genuine pre-commit deadline mapped to HTTP 504.
- #80: incomplete client header deadline maps to HTTP 408 before outbound ownership; Pause/Shutdown remains lifecycle cancellation.
- #85: terminal coordinator/host cleanup keeps only failed exact VPN ownership for serialized retry; runtime never becomes usable again; top-level application shutdown retries the same runtime host at most once after independent first-pass cleanup.

All three are closed completed with permanent PR CI and exact-main acceptance.

## Current engineering priority

Continue broad deterministic review rather than cosmetic churn. Highest-value blocks:
1. proxy/session shutdown, cancellation and response-commit ownership after #75/#77/#79/#80/#85;
2. RAS/native interop lifetime, helper-process termination, fixed-width/buffer boundaries and exact generation ownership;
3. verification HTTP response parser/framing and pooled response ownership;
4. DNS response binding, failover/deadline composition, CNAME/cache/time semantics;
5. process-wide bounded retention, metrics/logging and #11 performance/memory invariants.

For each new finding: open an issue first with explicit acceptance criteria; implement deterministic production/tests; preserve fail-closed routing and the existing 1.25x timing policy; require permanent Windows PR CI; merge only green; then require exact-main build + handoff and record SHA/run/artifact evidence.

## External acceptance remains separate

Open live issues: #2/#4/#5/#6/#7/#11/#13. Do not close #2/#4/#5/#6/#7 without real Windows/L2TP/operator evidence. Do not close #13 without representative 12–24 h exact-binary soak evidence. #11 remains permanently open as the latency/throughput/memory architecture requirement.
