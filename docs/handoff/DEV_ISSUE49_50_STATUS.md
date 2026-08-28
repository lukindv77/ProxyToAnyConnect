# #49 / #50 validated development status

Branch: `dev/issue49-probe-target`

Production base used for validation: `2e56f8f76efda9047ec83f3cd0e58aee395de322`.

Final successful Windows validation:
- run `33130832271` — success;
- full aggregate self-tests — success;
- source publish — success;
- source commit `1684718295944ecdb28216ae02c32365ff7b2b0c`.

The source commit changes exactly four files:
- `src/ProxyToAnyConnect/Configuration/AppOptions.cs`
- `src/ProxyToAnyConnect/Vpn/VpnConnectivityVerifier.cs`
- `tests/ProxyToAnyConnect.SelfTests/SettingsValidationSelfTests.cs`
- `tests/ProxyToAnyConnect.SelfTests/VerificationProbeRequestSelfTests.cs`

Key behavior:
- strict byte-exact ASCII origin-form `probePath`, valid `%HH` only, no fragment/control/space/non-ASCII substitution;
- builder-level fail-closed validation;
- IDNA A-label canonicalization of `probeHost`;
- explicit LDH DNS-label validation after canonicalization because Windows/.NET `Uri.CheckHostName` accepts forms such as `bad_.example`;
- one canonical host used for L2TP DNS, TLS SNI/TargetHost, and HTTP Host;
- `münich.example` -> `xn--mnich-kva.example`;
- verification setup allocation/timing guard retained.

Important validation assets:
- `issue49-transform.ps1` blob `8986e4461c3a2098be6a4519b1b42e9ad124c7d5`;
- `issue49-post-transform.ps1` blob `ef2db77e477b49cde12f63ad51f0d5a2c19d663f`;
- setup parent before source publish `545351d2cb7871f3b903b6242c82d494a0cde17d`.

Do not merge the dev workflow/transport history wholesale. Reconstruct the four source/test files cleanly from current main, then permanent PR Windows CI, merge/rebase, and exact-main build + handoff before closing #49/#50.
