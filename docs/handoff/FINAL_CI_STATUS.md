# CI status — handoff checkpoint

## Last fully accepted production code SHA before this docs commit

`5811900dfbf7488bd8ac53af20348c462681eeef` (tree `e44bf16408da3abade0c0f4d04708e6fd5ccd4ac`).

Latest engineering acceptance:
- #79 production `92fa6c8e94a2cb466e3e27780547d35a2d587ec5`: exact build #612 and handoff #391 green.
- #80 production `e5c817aeebc3b29c8e19fa07423eba8221cd47d7`: exact build #614 and handoff #392 green.
- #85 permanent PR #86 build #615 / run `33152115589`: success.
- #85 merged production `5811900dfbf7488bd8ac53af20348c462681eeef`.

Exact-main CI on `5811900dfbf7488bd8ac53af20348c462681eeef`:
- build #616 / run `33152272544`: success including Windows evidence smokes, restore/build, full aggregate self-tests, self-contained win-x64 publish, binary integrity manifest, ZIP and artifact upload;
- Windows artifact `9678213447`, digest `sha256:bd31b7f143d11c56cfc6794e55760e156341ca07bdd8fcbb52691d5010e9c1e7`;
- handoff #393 / run `33152272516`: success;
- handoff artifact `9678172387`, digest `sha256:bef544b5997914274001b50fce35684dcdd633d44c6230de654c0769db0a77c9`.

No performance/security threshold was widened to obtain these verdicts. The proxy transfer hot path remains pooled 32 KiB and production has no forced GC.

This docs-only commit moves `main`; after merge, fetch the new exact head and require its own `build` + `handoff` success before calling the documentation checkpoint fully accepted.
