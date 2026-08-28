# Audit snapshot — 2026-08-28

Live production source and issue comments remain authoritative.

## Recently resolved/accepted

- #45: HTTP/1.x request-line separator grammar is strict: exactly two ASCII SP separators; ambiguity rejects before outbound ownership.
- #47: soak observed duration is derived and revalidated from first/last serialized sample timestamps; the existing 50 ms consistency tolerance remains unchanged.
- #6 re-audit: CustomEphemeral cleanup already uses canonical names, regular-directory/reparse checks, exact managed-leaf whitelist, lock-first ownership and non-recursive deletion. RAS password/PSK carriers are cleared after native handoff. No duplicate patch warranted.
- #49: verification request-target is byte-exact ASCII HTTP origin-form with strict `%HH`; fragment, controls, SP/HTAB, non-ASCII and malformed/lossy forms reject both in settings validation and builder-level wire construction.
- #50: verification host is canonicalized to IDNA/A-label form, then validated using explicit ASCII LDH DNS-label grammar; one canonical authority is used for L2TP DNS, TLS `TargetHost`/SNI and HTTP `Host`. `münich.example` becomes `xn--mnich-kva.example`; `_` and malformed labels reject.

## #49/#50 production proof

- dev validation: run `33130832271`, source commit `1684718295944ecdb28216ae02c32365ff7b2b0c`;
- clean permanent PR #51, head `c67a29a0c82a5eb6f5bdee4e20ece39c426ac652`, four files only;
- PR build #579 / `33131957422`, attempt 2 success on identical head; attempt 1's unrelated DNS setup 1.30x timing result was treated as hosted-runner variance without widening the 1.25x policy or changing production;
- merged main `ddbdc95e3b9e7080a31c2b631da1c1f187a1f1a3`, exact build #580 / `33132200561` success and handoff #375 / `33132200498` success.

## Next audit direction

Continue source-level fail-closed and identity/ownership review across coherent boundaries, with emphasis on:
- authority/endpoint canonicalization before DNS/TLS/socket ownership;
- cancellation/lifetime ordering across proxy sessions, VPN contexts and reconnect maintenance;
- bounded caches/registries/diagnostic work and process-wide memory retention;
- regression harnesses that preserve security/performance policy instead of loosening thresholds for hosted-runner noise.

New findings must be issue-first with acceptance criteria, deterministic regressions, permanent Windows PR CI and exact-main CI.

## Remaining evidence boundary

Do not fabricate release acceptance. #2/#4/#5/#6/#7 require real Windows 11/L2TP/operator runs; #13 requires representative 12–24 h exact-binary soak; #11 remains an ongoing performance/memory constraint.
