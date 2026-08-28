# Active development — 2026-08-28

## Priority 1: clean acceptance for #49 + #50

Development branch: `dev/issue49-probe-target`.

Windows validation run `33130832271` is green and published source commit `1684718295944ecdb28216ae02c32365ff7b2b0c`.

The commit changes exactly:
- `src/ProxyToAnyConnect/Configuration/AppOptions.cs`
- `src/ProxyToAnyConnect/Vpn/VpnConnectivityVerifier.cs`
- `tests/ProxyToAnyConnect.SelfTests/SettingsValidationSelfTests.cs`
- `tests/ProxyToAnyConnect.SelfTests/VerificationProbeRequestSelfTests.cs`

Behavior under acceptance:
- `probePath` is byte-exact ASCII origin-form, strict `%HH`, reject fragments/controls/spaces/non-ASCII/lossy forms; builder also fails closed.
- `probeHost` is IDNA-canonicalized and then checked with explicit strict ASCII DNS LDH-label grammar; the same canonical host is used for L2TP DNS, TLS SNI/TargetHost and HTTP Host.
- valid `münich.example` emits `xn--mnich-kva.example`; `_` and malformed DNS labels are rejected.
- existing verification request allocation/timing guard remains.

Do not merge dev workflow/validation transport. Reconstruct only those four files from current main, open clean permanent PR, require Windows CI, merge/rebase, then exact-main `build` + `handoff`. Only then close #49/#50.

## Priority 2: broad deterministic audit

After #49/#50, continue concrete fail-closed/lifecycle/resource/performance findings across multiple coherent blocks. Avoid churn on already-proven #45/#47/#6 boundaries without new evidence.

## External acceptance remains blocked on real environment

#2/#4/#5/#6/#7 and the #13 12–24 h soak cannot be truthfully completed using hosted CI alone.
