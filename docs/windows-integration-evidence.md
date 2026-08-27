# Windows integration evidence bundles

`docs/windows-integration-test.md` remains the manual/E2E acceptance procedure. The scripts under `tools/` turn its route/profile/interface/proxy checks into a reproducible evidence bundle without reading VPN credentials or initiating/disconnecting RAS by themselves.

## Capture stages

Use one output directory for all three stages. Keep the same proxy endpoint list and expected VPN egress identity for the entire run.

```powershell
$evidence = '.\artifacts\integration-evidence'
$proxy = '127.0.0.1:18080'
$expectedVpnIPv4 = '<EXPECTED_VPN_IPV4>'
```

Before ProxyToAnyConnect establishes the test L2TP session, capture the host baseline:

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
  -ProxyEndpoint $proxy `
  -ExpectedVpnPublicIPv4 $expectedVpnIPv4 `
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
  -ProxyEndpoint $proxy `
  -ExpectedVpnPublicIPv4 $expectedVpnIPv4 `
  -RequireExternalProbes `
  -RequireProxyHttpProbe
```

`Complete-WindowsIntegrationEvidence.ps1` re-runs the per-stage validator before trusting any existing summary or manifest. It then fails closed unless:

- Baseline, Ready and Final evidence/summary/manifest files all exist and validate;
- one IPv4 default-route fingerprint is preserved across Baseline -> Ready -> Final;
- the Windows VPN-profile fingerprint returns to the Baseline value in Final;
- every requested Ready proxy HTTPS probe succeeded;
- the direct Ready HTTPS probe succeeded;
- every requested proxy HTTPS result matches `-ExpectedVpnPublicIPv4` when it is supplied;
- direct host egress differs from proxy egress by default;
- every requested plain-HTTP proxy probe exists and succeeded when `-RequireProxyHttpProbe` is used.

If direct and VPN egress are intentionally the same in the real environment, add `-AllowDirectPublicIPv4Match` explicitly. Do not use that switch merely to make a failed isolation test pass.

On success the script writes `acceptance-summary.json`. The summary contains the aggregate verdict, route/profile fingerprints, probe outputs, and for each stage the evidence/summary/manifest paths, validated file count and assertion counts. This is the machine-readable acceptance record to archive with the tested executable and commit SHA.

If plain HTTP is not available in the test environment, omit both `-HttpProbeUrl` during Ready capture and `-RequireProxyHttpProbe` during completion. HTTPS/direct-route/VPN isolation checks remain mandatory for the real endpoint run.

## What is captured

Each stage records scalar/system evidence only:

- current IPv4 default routes and a stable SHA-256 route fingerprint;
- Current User and All Users Windows VPN profile metadata and profile fingerprint;
- interface/index/IPv4/default-gateway/DNS metadata;
- current ProxyToAnyConnect process resource counters when present;
- optional direct and proxy HTTP/HTTPS probe results;
- optional exact expected L2TP public-IPv4 assertion;
- optional copies of the most recent application JSONL logs.

The capture script does not read saved RAS passwords, PSKs, DPAPI payloads or application configuration secrets. It does not call `RasDial` or `RasHangUp`.

## Per-stage validator and integrity manifest

`Test-WindowsIntegrationEvidence.ps1` is intentionally separate from capture. The aggregate completion script invokes it automatically, but it can also be run directly while diagnosing a checkpoint. It fails closed when:

- `evidence.json` or `summary.json` is absent;
- schema/stage metadata is inconsistent;
- a recorded assertion failed;
- required route, VPN-profile, interface or process capture failed;
- route/profile fingerprints are absent;
- Ready/Final validation is attempted without the Baseline record.

After validation it writes `manifest.json` containing the relative path, byte length and SHA-256 of every stage evidence/log file. The manifest contains hashes and metadata, not credential values.

Keep the entire evidence directory together with the tested executable SHA and Git commit SHA. A real-endpoint acceptance result is not complete if the validator/aggregate completion fails or if the exact tested build cannot be identified.

## CI scope

The Windows GitHub Actions build parses all three PowerShell tools and executes a safe three-stage `Baseline -> Ready -> Final` capture with external probes disabled, followed by `Complete-WindowsIntegrationEvidence.ps1`. The hosted smoke verifies stage creation, per-stage manifests, aggregate route/profile invariants and the final machine-readable acceptance summary. It does **not** replace real Windows 11 + real L2TP endpoint acceptance required by issues #2, #4, #5, #6 and #7.
