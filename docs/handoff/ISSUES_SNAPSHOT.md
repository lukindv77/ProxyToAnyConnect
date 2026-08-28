# Issue snapshot — 2026-08-28

Live GitHub issue state/comments are authoritative; refresh them at chat start.

## Open release/architecture work

- #2 — real Windows 11 + L2TP E2E/fail-closed acceptance.
- #4 — real shared/dedicated multi-proxy behavior.
- #5 — real GUI/operator/profile/selective live acceptance.
- #6 — real CustomEphemeral auth/PSK/cert/cleanup acceptance. Latest filesystem/secret-carrier audit found existing hardening already sufficient; no duplicate patch.
- #7 — real keepalive failure → invalidation → hangup → cooldown → reconnect acceptance.
- #11 — permanent proxy latency/throughput/process-memory architecture requirement.
- #13 — representative 12–24 h exact-binary soak and resource trend review.
- #49 — reject unsafe/lossy verification request-targets. Dev source is Windows-green but not main-accepted.
- #50 — canonical IDNA verification host across DNS/TLS/HTTP plus strict DNS labels. Dev source is Windows-green but not main-accepted.

## Recently completed

- #45 — strict HTTP request-line separator grammar; closed completed.
- #47 — soak serialized-timestamp duration consistency; closed completed.
- #44 — earlier HTTP OWS/framing block; completed.
- #14 — HTTP framing; completed.
- #15 — transactional proxy startup; completed.

## #49/#50 current evidence

Run `33130832271` green; validated source commit `1684718295944ecdb28216ae02c32365ff7b2b0c`. Clean reconstruction/permanent PR/exact-main CI are still required before closure.
