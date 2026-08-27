# Windows integration evidence bundles

`docs/windows-integration-test.md` remains the manual/E2E acceptance procedure. The scripts under `tools/` turn its route/profile/interface/proxy checks into a reproducible evidence bundle without reading VPN credentials or initiating/disconnecting RAS by themselves.

## Capture stages

Use one output directory for all three stages. Keep the same proxy endpoint list and egress expectation contract for the entire run. The contract may use one backward-compatible default proxy public IPv4, per-proxy endpoint overrides for heterogeneous shared/dedicated groups, and a separate expected direct-host public IPv4.

For a release-grade run, extract the GitHub Actions `ProxyToAnyConnect-win-x64` artifact and keep its generated `build-identity.json` next to `ProxyToAnyConnect.exe`. The manifest contains the exact Git commit and SHA-256 of the executable produced by that workflow run.

```powershell
$evidence = '.\artifacts\integration-evidence'
$proxyA = '127.0.0.1:18080'
$proxyB = '127.0.0.1:18081'
$expectedVpnIPv4 = '<DEFAULT_EXPECTED_VPN_IPV4>'
$expectedProxyIPv4 = @{ $proxyB = '<PROXY_B_EXPECTED_VPN_IPV4>' }
$expectedDirectIPv4 = '<EXPECTED_DIRECT_HOST_IPV4>'
$buildIdentity = Get-Content '.\ProxyToAnyConnect-win-x64\build-identity.json' -Raw | ConvertFrom-Json
$expectedExecutableSha256 = [string]$buildIdentity.sha256
```

Before starting ProxyToAnyConnect for the release-grade process-lifecycle run, capture the host baseline:

```powershell
.\tools\Invoke-WindowsIntegrationEvidence.ps1 `
  -Stage Baseline `
  -OutputDirectory $evidence `
  -SkipExternalProbes
```

After ProxyToAnyConnect has reached `Ready`, capture the same machine again and exercise the real proxy. Set `-HttpProbeUrl` to a controlled plain-HTTP endpoint when plain HTTP is part of the acceptance run.

```powershell
.\tools\Invoke-WindowsIntegrationEvidence.ps1 `
  -Stage Ready `
  -OutputDirectory $evidence `
  -ProxyEndpoint @($proxyA, $proxyB) `
  -ExpectedVpnPublicIPv4 $expectedVpnIPv4 `
  -ExpectedProxyPublicIPv4 $expectedProxyIPv4 `
  -ExpectedDirectPublicIPv4 $expectedDirectIPv4 `
  -HttpProbeUrl 'http://<HTTP_TEST_HOST>/' `
  -LogDirectory '<ProxyToAnyConnect log directory>'
```

After explicit application Exit and RAS cleanup:

```powershell
.\tools\Invoke-WindowsIntegrationEvidence.ps1 `
  -Stage Final `
  -OutputDirectory $evidence `
  -SkipExternalProbes `
  -LogDirectory '<ProxyToAnyConnect log directory>'
```

## Complete acceptance verdict

Do not manually infer acceptance from three unrelated JSON files. Run the aggregate validator after all checkpoints exist:

```powershell
.\tools\Complete-WindowsIntegrationEvidence.ps1 `
  -OutputDirectory $evidence `
  -ProxyEndpoint @($proxyA, $proxyB) `
  -ExpectedVpnPublicIPv4 $expectedVpnIPv4 `
  -ExpectedProxyPublicIPv4 $expectedProxyIPv4 `
  -ExpectedDirectPublicIPv4 $expectedDirectIPv4 `
  -ExpectedExecutableSha256 $expectedExecutableSha256 `
  -RequireProcessLifecycle `
  -RequireExternalProbes `
  -RequireProxyHttpProbe
```

`Complete-WindowsIntegrationEvidence.ps1` re-runs the per-stage validator before trusting any existing summary or manifest. It then fails closed unless:

- Baseline, Ready and Final evidence/summary/manifest files all exist and validate;
- one IPv4 default-route fingerprint is preserved across Baseline -> Ready -> Final;
- the Windows VPN-profile fingerprint returns to the Baseline value in Final;
- when `-RequireProcessLifecycle` is used, no ProxyToAnyConnect process exists at Baseline, exactly one exists at Ready, and none remains after explicit Exit at Final;
- when `-ExpectedExecutableSha256` is supplied, the single Ready process executable hash exactly matches the expected CI-published binary;
- every requested Ready proxy HTTPS probe succeeded;
- the direct Ready HTTPS probe succeeded;
- every requested proxy HTTPS result matches its effective expected IPv4: a matching `-ExpectedProxyPublicIPv4` override first, otherwise `-ExpectedVpnPublicIPv4`;
- when `-ExpectedDirectPublicIPv4` is supplied, the direct Ready HTTPS result exactly matches it;
- the Ready evidence expectation metadata must exactly match the aggregate command-line contract, so a stale or substituted expected-egress contract is rejected;
- direct host egress differs from each proxy egress by default;
- every requested plain-HTTP proxy probe exists and succeeded when `-RequireProxyHttpProbe` is used.

If direct and VPN egress are intentionally the same in the real environment, add `-AllowDirectPublicIPv4Match` explicitly. Do not use that switch merely to make a failed isolation test pass.

On success the script writes `acceptance-summary.json`. The summary contains the aggregate verdict, route/profile fingerprints, process counts, expected/observed executable SHA-256, explicit expected direct IPv4, observed direct IPv4, effective expected public IPv4 per proxy, observed proxy public IPv4 values, and for each stage the evidence/summary/manifest paths, validated file count and assertion counts. This is the machine-readable acceptance record to archive with the tested artifact and Git commit SHA.

If plain HTTP is not available in the test environment, omit both `-HttpProbeUrl` during Ready capture and `-RequireProxyHttpProbe` during completion. HTTPS/direct-route/VPN isolation checks remain mandatory for the real endpoint run.

## What is captured

Each stage records scalar/system evidence only:

- current IPv4 default routes and a stable SHA-256 route fingerprint;
- Current User and All Users Windows VPN profile metadata and profile fingerprint;
- interface/index/IPv4/default-gateway/DNS metadata;
- current ProxyToAnyConnect process resource counters when present;
- SHA-256 of the running ProxyToAnyConnect executable when Windows exposes its process path; the path itself is never persisted, and a hash-capture failure records only the exception type;
- optional direct and proxy HTTP/HTTPS probe results;
- optional default expected proxy public IPv4, per-proxy expected public IPv4 overrides and explicit expected direct-host public IPv4 assertions;
- optional copies of the most recent application JSONL logs.

The capture script does not read saved RAS passwords, PSKs, DPAPI payloads or application configuration secrets. It does not call `RasDial` or `RasHangUp`.

## Per-stage validator and integrity manifest

`Test-WindowsIntegrationEvidence.ps1` is intentionally separate from capture. The aggregate completion script invokes it automatically, but it can also be run directly while diagnosing a checkpoint. It fails closed when:

- `evidence.json` or `summary.json` is absent;
- schema/stage metadata is inconsistent;
- a recorded assertion failed;
- summary assertion counts/names do not exactly match the evidence assertions;
- expectation metadata is malformed, duplicated, missing its matching probe/assertion, or disagrees with the recorded probe output;
- required route, VPN-profile, interface or process capture failed;
- route/profile fingerprints are absent;
- Ready/Final validation is attempted without the Baseline record.

After validation it writes `manifest.json` containing the relative path, byte length and SHA-256 of every stage evidence/log file. The manifest contains hashes and metadata, not credential values.

Keep the entire evidence directory together with the tested artifact. A real-endpoint acceptance result is not complete if the validator/aggregate completion fails, if `-ExpectedExecutableSha256` does not match the Ready process, or if the exact tested build cannot be identified.

## CI scope

The Windows GitHub Actions build parses all three PowerShell tools and executes a safe three-stage `Baseline -> Ready -> Final` capture with external probes disabled, followed by `Complete-WindowsIntegrationEvidence.ps1`. The hosted smoke verifies stage creation, per-stage manifests, aggregate route/profile invariants and the final machine-readable acceptance summary. It additionally injects a synthetic Ready process record and re-runs completion with `-ExpectedExecutableSha256` plus `-RequireProcessLifecycle`. The hosted smoke then constructs a schema-faithful heterogeneous Ready egress contract with two proxy endpoints, a default proxy IPv4, one per-proxy override and a distinct direct-host IPv4. Positive aggregate completion must preserve the expected/observed values; negative aggregate calls with a wrong proxy or direct expectation must fail without replacing the last accepted summary; and a tampered Ready expectation must be rejected by the per-stage validator. This continuously exercises exact-binary and egress-contract paths without launching the application or contacting a VPN endpoint.

Every pushed self-contained artifact now contains:

- `ProxyToAnyConnect.exe`;
- `ProxyToAnyConnect.exe.sha256` for simple command-line verification;
- `build-identity.json` with schema version, exact Git commit and executable SHA-256.

Hosted smoke does **not** replace real Windows 11 + real L2TP endpoint acceptance required by issues #2, #4, #5, #6 and #7.
