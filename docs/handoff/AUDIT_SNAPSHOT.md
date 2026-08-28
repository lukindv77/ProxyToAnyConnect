# Audit snapshot — 2026-08-28

Live production source and issue comments remain authoritative.

## Accepted hardening state

The current deterministic audit chain now covers:
- strict request-line/header/framing/Host/authority parsing and canonical routing;
- no post-commit proxy-generated HTTP response for CONNECT or plain HTTP;
- explicit 408 client-header and 504 outbound deadline semantics with owner/VPN cancellation precedence;
- canonical verification request target/authority and strict HTTP response framing;
- DNS exact question/owner binding, canonical CNAME/IPv4 identity, monotonic TTL and bounded context-scoped cache;
- DPAPI managed/unmanaged cleanup, acquisition-failure cleanup and fixed-width RAS field limits;
- reparse-safe CustomEphemeral/log filesystem ownership;
- fail-closed monitor invalidation before cleanup joins;
- exact residual VPN ownership retention across reconfigure and terminal shutdown;
- one bounded top-level retry of the same runtime host during application exit;
- pooled 32 KiB proxy transfer path, bounded state and unchanged performance policy.

## Clean audit results immediately around #85

- DNS failover/deadline composition: each DNS attempt retains its lower-level timeout, while the whole outbound acquisition is now bounded by #79; no unbounded admitted-session path found.
- Windows VPN PowerShell helper: `-EncodedCommand` + `ArgumentList`, process-tree termination, independent stdout/stderr drain and bounded cleanup are coherent; no concrete new defect found.
- ICMP async native lifetime: documentation does not prove `IcmpCloseHandle` joins arbitrary pending I/O, so an existing comment is stronger than the public contract; however no deterministic post-timeout write/UAF path was established. Treat as documentation-risk, not a production issue, unless future evidence makes it reproducible.
- Proxy lease/session terminal ownership: residual native VPN ownership is retained by the VPN manager/coordinator path after #85; no separate proxy-owner retry issue was justified.

## Next audit directions

Prioritize new reproducible defects only:
1. response/deadline/cancellation precedence under mixed failures after #79/#80;
2. RAS/native interop size/version/output-buffer boundaries and callback/helper ownership;
3. verification parser edge cases that could alter Ready-state evidence;
4. DNS TCP fallback/CNAME/cache/failover exactness under cancellation;
5. bounded diagnostics/logging/metrics and process-wide #11 memory/latency behavior.

Any new finding must be issue-first, deterministic, permanent-Windows-CI green, then exact-main build/handoff. Never widen the 1.25x policy merely for hosted-runner noise.

## Remaining evidence boundary

#2/#4/#5/#6/#7 require real Windows 11/L2TP/operator runs; #13 requires representative 12–24 h exact-binary soak; #11 remains an ongoing performance/memory constraint.
