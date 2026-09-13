# Audit snapshot — 2026-09-13

Live production source and issue comments remain authoritative.

## Accepted deterministic coverage

Current accepted hardening covers:
- strict proxy request-line/header/framing/Host/authority parsing and canonical routing;
- no post-commit proxy-generated HTTP response for CONNECT/plain HTTP;
- explicit 408 client-header and 504 outbound deadline semantics with lifecycle/VPN cancellation precedence;
- canonical verification authority/request target and strict verification response framing;
- DNS exact transaction/question/owner binding, canonical CNAME/IPv4 identity, ambiguous CNAME RRset rejection, non-QUERY OPCODE rejection, exact-owner A RDATA length validation, monotonic TTL and bounded context cache;
- DPAPI managed/unmanaged cleanup, fixed-width RAS field guards and secret-carrier release;
- reparse-safe CustomEphemeral/log ownership;
- fail-closed monitor invalidation before cleanup joins and exact residual native ownership retention;
- pooled 32 KiB proxy transfer path, bounded process state and unchanged 1.25x performance policy.

## Clean audit results from the latest pass

- RAS x64 ABI/layout: Windows SDK C++ vs managed probe matched all 12 checked size/offset values in run `33164715623`; no production change warranted.
- Proxy accepted-session shutdown/lease drain after #79/#80/#85: no new exact ownership gap established.
- Verification response-read ownership/framing after #88: pooled owner/cap/Content-Length/chunked/EOF boundaries remained fail-closed; no new issue established.
- `VpnContext` reference lifetime and monitor invalidation: exact context is invalidated before slow sibling cleanup; no new defect established.

## Active findings

### #94 — incomplete whole-message DNS framing

`ParseResponse` reads answer evidence but, before the proposed fix, does not structurally validate every declared authority/additional RR or exact message exhaustion. Issue #94 is open. First dev run `33165671844` failed only in transform transport before build because the helper anchor was too brittle. No behavioral verdict yet.

### #95 — enumeration-order-dependent interface ownership

`VpnInterfaceResolver.ResolveByAddress` currently returns the first adapter owning the PPP local IPv4. Duplicate ownership can make `InterfaceIndex` selection enumeration-order dependent even though that index later governs L2TP-bound DNS/socket routing. Issue #95 is open. First dev transform applied, but compile run `33165867074` failed only on a C# null-coalescing operand type mismatch before aggregate tests.

## Next audit directions after #94/#95

Continue only on reproducible findings: RAS projection/interface-generation identity, DNS TCP/failover/deadline exactness, proxy response/cancellation ownership under mixed failures, and bounded logging/metrics/process state under #11. Do not churn already accepted boundaries without a concrete failure.

## Remaining evidence boundary

#2/#4/#5/#6/#7 require real Windows 11/L2TP/operator runs; #13 requires representative 12–24 h exact-binary soak; #11 remains an ongoing performance/memory constraint. Hosted Actions cannot substitute for that evidence.
