# Active development — 2026-08-28

## Completed production block: #49 + #50

#49/#50 are production-accepted and closed completed.

Accepted lineage:
- dev Windows validation run `33130832271`, source commit `1684718295944ecdb28216ae02c32365ff7b2b0c`;
- clean acceptance commit `c67a29a0c82a5eb6f5bdee4e20ece39c426ac652`, exactly four production/test files and no dev workflow/transport history;
- permanent PR #51;
- PR build #579 / run `33131957422`: attempt 1 hit only the previously documented hosted-runner-sensitive DNS setup timing gate at 1.30x; no threshold or code was changed; identical-head attempt 2 passed build + aggregate self-tests;
- rebase-merged production SHA `ddbdc95e3b9e7080a31c2b631da1c1f187a1f1a3`, tree `4f11a13a1ac0d1839b86671dc0b7ccae7eed0d40`;
- exact-main build #580 / `33132200561`: success through publish/artifact;
- exact-main handoff #375 / `33132200498`: success.

Accepted behavior:
- `verification.probePath` is byte-exact ASCII HTTP origin-form; `%HH` must be valid; fragment, controls, SP/HTAB, non-ASCII and malformed/lossy forms reject; request builder independently revalidates before ASCII wire encoding.
- verification DNS host is canonicalized once to an IDNA A-label and checked using explicit strict ASCII LDH-label grammar; the same canonical authority is used for L2TP DNS, TLS `TargetHost`/SNI and HTTP `Host`.
- `münich.example` becomes `xn--mnich-kva.example`; `_`, empty/oversized labels and leading/trailing hyphens reject.
- proxy transfer hot path and existing verification allocation/timing guard are unchanged.

## Current engineering priority: broad deterministic audit

Continue coherent multi-file/multi-boundary audits rather than cosmetic churn. Prioritize concrete fail-closed, identity-consistency, lifecycle/resource ownership and performance/memory findings that can be proven in deterministic Windows CI without fabricating real VPN evidence.

For a new finding:
1. open an issue first with explicit acceptance criteria;
2. implement production/test changes without weakening routing/security/performance invariants;
3. require permanent Windows PR CI;
4. merge/rebase and require exact-main `build` + `handoff`;
5. record exact SHA/run IDs in the issue and handoff docs.

Do not reopen or churn #45/#47/#49/#50 or the already-audited #6 cleanup/secret-carrier boundaries without a new concrete finding.

## External acceptance remains blocked on real environment

#2/#4/#5/#6/#7 require real Windows 11 + real L2TP/operator evidence. #13 requires a representative 12–24 h exact-binary soak with traffic/reconnect/Pause/Resume/reconfigure and correlated managed/native resource series. #11 remains permanently open as the latency/throughput/process-memory architecture contract.
