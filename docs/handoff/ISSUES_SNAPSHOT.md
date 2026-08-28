# Issue snapshot — 2026-08-28

Live GitHub issue state/comments are authoritative; refresh them at chat start.

## Open live work

- #2 — real Windows 11 + real L2TP E2E/fail-closed acceptance.
- #4 — real shared/dedicated multi-proxy lease behavior.
- #5 — real GUI/operator/profile/selective live acceptance.
- #6 — real CustomEphemeral auth/PSK/certificate/cleanup acceptance.
- #7 — real keepalive failure -> invalidation -> hangup -> cooldown -> reconnect acceptance.
- #11 — permanent low-latency/throughput/process-memory architecture requirement.
- #13 — representative 12–24 h exact-binary soak and resource-trend review.

These are the only open issues at this checkpoint.

## Recently completed deterministic hardening

- #52 resolver authority identity / canonical IPv4 and exact CNAME identity.
- #53 preserve verification wire identity through the settings editor.
- #54 zero unmanaged DPAPI plaintext before release.
- #58 fixed-width RAS field capacity fail-closed validation.
- #59 reparse-safe owned logging append/retention boundaries.
- #62 clear RAS password carrier on every pre-dial exit.
- #63 strict verification HTTP response framing.
- #66 bind DNS response question/answer ownership to the exact query.
- #67 DPAPI acquisition/copy failure cleanup gaps.
- #70 invalidate failed monitored VPN context before sibling cleanup drain.
- #71 retain residual exact VPN ownership across reconfigure cleanup failure.
- #73 monotonic DNS TTL expiry.
- #75 CONNECT response commitment boundary.
- #77 plain-HTTP origin response commitment boundary.
- #79 enforce configured outbound connection deadline and 504 mapping.
- #80 map client-header deadline to 408 before outbound ownership.
- #85 retain terminal exact cleanup ownership and expose one bounded top-level retry.

All above are closed completed; use each live issue's comments for exact dev/PR/main lineage.

## Latest exact-main proof

Production `5811900dfbf7488bd8ac53af20348c462681eeef`, tree `e44bf16408da3abade0c0f4d04708e6fd5ccd4ac`; build #616 / `33152272544` green; handoff #393 / `33152272516` green.

Do not infer closure of #2/#4/#5/#6/#7/#13 from hosted Actions. Their remaining acceptance requires genuine external evidence.
