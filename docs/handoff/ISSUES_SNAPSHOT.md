# Issue snapshot — 2026-08-28

Live GitHub issue state/comments are authoritative; refresh them at chat start.

## Open release/architecture work

- #2 — real Windows 11 + real L2TP E2E/fail-closed acceptance.
- #4 — real shared/dedicated multi-proxy behavior.
- #5 — real GUI/operator/profile/selective live acceptance.
- #6 — real CustomEphemeral auth/PSK/cert/cleanup acceptance. Latest filesystem/secret-carrier audit found existing hardening sufficient; no duplicate patch.
- #7 — real keepalive failure → invalidation → hangup → cooldown → reconnect acceptance.
- #11 — permanent proxy latency/throughput/process-memory architecture requirement.
- #13 — representative 12–24 h exact-binary soak and resource trend review.

## Recently completed

- #49 — byte-exact verification origin-form request-target; closed completed after PR #51 and exact-main build/handoff.
- #50 — canonical IDNA/A-label verification authority across L2TP DNS/TLS/HTTP plus explicit strict DNS LDH labels; closed completed after PR #51 and exact-main build/handoff.
- #45 — strict HTTP request-line separator grammar; closed completed.
- #47 — soak serialized-timestamp duration consistency; closed completed.
- #44 — earlier HTTP OWS/framing block; completed.
- #14 — HTTP framing; completed.
- #15 — transactional proxy startup; completed.

## #49/#50 accepted evidence

- dev validation run `33130832271`, source commit `1684718295944ecdb28216ae02c32365ff7b2b0c`;
- clean PR #51 head `c67a29a0c82a5eb6f5bdee4e20ece39c426ac652`;
- permanent PR build #579 / `33131957422`, identical-head attempt 2 success;
- merged main `ddbdc95e3b9e7080a31c2b631da1c1f187a1f1a3`, tree `4f11a13a1ac0d1839b86671dc0b7ccae7eed0d40`;
- exact-main build #580 / `33132200561`: success;
- exact-main handoff #375 / `33132200498`: success.

Do not infer closure of #2/#4/#5/#6/#7/#13 from hosted Actions smoke. Their remaining acceptance requires genuine external evidence.
