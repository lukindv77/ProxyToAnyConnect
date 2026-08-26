# Final CI status at handoff — 2026-08-26

This file exists because hosted-runner results exposed two independent issues across docs-only commits with unchanged production code.

## Build #272 — `b3fbe1f96c0ffa7d031cb72b81793ec6ea9c2858`

- compile: success, 0 warnings/errors;
- paired proxy setup timing guard: PASS;
  - parser 1999 vs 2042 ns/op = 0.98x;
  - origin 973 vs 1222 ns/op = 0.80x;
- suite reached `ProxyHttpFramingSelfTests`;
- `ExactContentLengthBoundsClientToOriginBytesAsync` failed because response `ReadToEndAsync` received IOException with inner Windows SocketException 10054 / connection reset by remote host.

## Build #273 — `b304a4331b8527b8280396047d3c649cfaed80f3`

- compile: success, 0 warnings/errors;
- same production/parser code and same timing-test source as #272;
- paired timing guard: FAIL;
  - parser 5218 vs 2917 ns/op = 1.79x;
  - limit 1.25x;
- suite stopped before the framing test.

## Interpretation for the next chat

The two results prove the current setup timing gate is not sufficiently reproducible on hosted runners. Do not simply widen the 1.25x policy. Audit measurement methodology/current-vs-predecessor equivalence and make the gate stable enough that docs-only commits cannot swing from 0.98x to 1.79x without a code change.

After the gate is stable, the already-observed framing reset must be investigated. Preserve the strict invariant that no bytes after declared Content-Length reach the origin. Determine whether reset 10054 is caused by Windows close behavior with deliberately unread malicious trailing bytes, premature proxy close before full origin response, or an over-strict clean-EOF test.

Issue #14 remains open. Issue #15 remains the next confirmed lifecycle implementation after #14 validation.
