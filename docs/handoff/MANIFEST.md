# ProxyToAnyConnect handoff manifest

Live GitHub is authoritative. This manifest defines minimum reading and archive behavior for the new conversation.

## Read first

1. `docs/handoff/NEW_CHAT_PROMPT.md`
2. `docs/handoff/CURRENT_STATE.md`
3. `docs/handoff/AUDIT_SNAPSHOT.md`
4. `docs/handoff/ISSUES_SNAPSHOT.md`
5. `docs/handoff/HANDOFF_INDEX.md`
6. `docs/requirements.md`
7. `docs/architecture.md`
8. `docs/memory-stability.md`
9. `docs/windows-integration-test.md`
10. `README.md`
11. `.github/workflows/build.yml`
12. `.github/workflows/handoff.yml`

Before changing architecture inspect current `Configuration`, `Gui`, `Runtime`, `Proxy`, `Network`, `Vpn`, `Diagnostics` and `tests/ProxyToAnyConnect.SelfTests`.

## Live facts the new chat must query

- current `main` SHA;
- exact-head `build` and `handoff` conclusions;
- latest issue states/comments;
- newer commits after this package;
- latest artifacts.

Never infer green CI from older heads.

## Handoff archive

`.github/workflows/handoff.yml` creates GitHub Actions artifact:

`ProxyToAnyConnect-handoff-<github.sha>`

It contains exact checked-out `src`, `tests`, `docs`, `.github`, README, solution, `.gitignore` and generated `HANDOFF_BUILD_INFO.txt` with exact commit/ref/run/timestamp and startup prompt path. `bin`, `obj`, `.git` are excluded.

Recorded refreshed archive before final status correction:

- head `b3fbe1f96c0ffa7d031cb72b81793ec6ea9c2858`;
- handoff #84 / run `32982263807`: success;
- artifact id `9611924335`;
- artifact name `ProxyToAnyConnect-handoff-b3fbe1f96c0ffa7d031cb72b81793ec6ea9c2858`;
- SHA-256 `5b9307c6a184f3a6bf4ddc47b60af6569ea4a3611940f7cb7d9b527eaa72aa6b`.

This final handoff status correction will create a newer handoff artifact; the next chat must use the latest exact-head artifact, not blindly the older id above.

## Exact code validation at `b3fbe1f...`

Build #272 / run `32982263806`:

- compile succeeded, 0 warnings/errors;
- paired proxy setup timing guard passed: parser 0.98x, origin 0.80x;
- framing suite was reached;
- failed `ProxyHttpFramingSelfTests.ExactContentLengthBoundsClientToOriginBytesAsync` because client `ReadToEndAsync` saw Windows SocketException 10054 / connection reset.

Thus the current development blocker is the exact Content-Length smuggling-boundary test/connection-close behavior, not the older timing noise.

## Immediate continuation

1. Audit/fix framing reset semantics without allowing any bytes after declared CL to reach origin.
2. Reach exact-head green `ProxyHttpFramingSelfTests` and finish #14 only after all semantic/performance gates.
3. Implement #15 transactional `ProxyInstanceRuntime.StartAsync` ownership: cancel -> await exact run drain -> clear same generation -> dispose CTS -> release lease once.
4. Continue #11/#13 and real Windows #2/#4/#5/#6/#7 acceptance.
