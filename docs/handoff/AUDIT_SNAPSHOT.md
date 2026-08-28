# Audit snapshot — 2026-08-28

Live production source and issue comments remain authoritative.

## Recently resolved/accepted

- #45: HTTP/1.x request-line separator grammar is strict: exactly two ASCII SP separators; ambiguity rejects before outbound ownership.
- #47: soak observed duration is derived and revalidated from first/last **serialized** sample timestamps; the existing 50 ms consistency tolerance remains unchanged.
- #6 re-audit: CustomEphemeral cleanup already uses canonical names, regular-directory/reparse checks, exact managed-leaf whitelist, lock-first ownership and non-recursive deletion. Self-tests cover malformed/oversized marker, unknown child, noncanonical name, reparse target, live owner and 16-cycle partial failure. RAS password and PSK carriers are cleared after native handoff. No duplicate patch warranted.

## New verification findings

### #49 request-target integrity
Production `verification.probePath` only enforces leading `/` before ASCII request construction. This allows unsafe framing characters and lossy Unicode substitution. Required fix: strict byte-exact ASCII origin-form, valid `%HH`, reject fragment/control/space/non-ASCII/malformed escapes, and builder-level fail-closed validation.

### #50 authority identity
Windows/.NET 10 classifies Unicode `münich.example` as DNS, while production HTTP Host ASCII encoding can turn it into `m?nich.example`, diverging from DNS/TLS identity. Further dev testing showed `Uri.CheckHostName` also accepts `bad_.example`; therefore the final security boundary must use IDNA A-label canonicalization followed by explicit LDH DNS-label validation.

## Dev proof for #49/#50

Branch `dev/issue49-probe-target`, run `33130832271` green. Bot-published source commit `1684718295944ecdb28216ae02c32365ff7b2b0c` changes exactly four production/test files and excludes dev workflow transport. It is **dev validated, not production accepted**.

Next step: reconstruct those four files cleanly from current main, permanent Windows PR CI, merge/rebase, exact-main build + handoff, then close #49/#50.

## Remaining evidence boundary

Do not fabricate release acceptance. #2/#4/#5/#6/#7 require real Windows 11/L2TP/operator runs; #13 requires representative 12–24 h exact-binary soak; #11 remains an ongoing performance/memory constraint.
