# Windows integration evidence bundles

`docs/windows-integration-test.md` remains the manual/E2E acceptance procedure. The scripts under `tools/` turn its route/profile/interface/proxy checks into a reproducible evidence bundle without reading VPN credentials or initiating/disconnecting RAS by themselves.

## Capture stages

Use one output directory for all three stages.

```powershell
$evidence = '.\artifacts\integration-evidence'

.\tools\Invoke-WindowsIntegrationEvidence.ps1 `
  -Stage Baseline `
  -OutputDirectory $evidence `
  -ProxyEndpoint '127.0.0.1:18080' `
  -ExpectedVpnPublicIPv4 '<EXPECTED_VPN_IPV4>'
```

After ProxyToAnyConnect has reached `Ready`, capture the same machine again:

```powershell
.\tools\Invoke-WindowsIntegrationEvidence.ps1 `
  -Stage Ready `
  -OutputDirectory $evidence `
  -ProxyEndpoint '127.0.0.1:18080' `
  -ExpectedVpnPublicIPv4 '<EXPECTED_VPN_IPV4>' `
  -LogDirectory '<ProxyToAnyConnect log directory>'

.\tools\Test-WindowsIntegrationEvidence.ps1 `
  -Stage Ready `
  -OutputDirectory $evidence
```

After explicit application Exit and RAS cleanup:

```powershell
.\tools\Invoke-WindowsIntegrationEvidence.ps1 `
  -Stage Final `
  -OutputDirectory $evidence `
  -SkipExternalProbes `
  -LogDirectory '<ProxyToAnyConnect log directory>'

.\tools\Test-WindowsIntegrationEvidence.ps1 `
  -Stage Final `
  -OutputDirectory $evidence
```

Validate Baseline as well before relying on the bundle:

```powershell
.\tools\Test-WindowsIntegrationEvidence.ps1 `
  -Stage Baseline `
  -OutputDirectory $evidence
```

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

## Validator and integrity manifest

`Test-WindowsIntegrationEvidence.ps1` is intentionally separate from capture. It fails closed when:

- `evidence.json` or `summary.json` is absent;
- schema/stage metadata is inconsistent;
- a recorded assertion failed;
- required route, VPN-profile, interface or process capture failed;
- route/profile fingerprints are absent;
- Ready/Final validation is attempted without the Baseline record.

After validation it writes `manifest.json` containing the relative path, byte length and SHA-256 of every stage evidence/log file. The manifest contains hashes and metadata, not credential values.

Keep the entire evidence directory together with the tested executable SHA and Git commit SHA. A real-endpoint acceptance result is not complete if the validator fails or if the exact tested build cannot be identified.

## CI scope

The Windows GitHub Actions build executes a safe `Baseline -SkipExternalProbes` smoke capture and runs the validator. This proves the scripts remain syntactically valid and that the hosted Windows image can execute the route/profile/interface capture paths. It does **not** replace real Windows 11 + real L2TP endpoint acceptance required by issues #2, #4, #5, #6 and #7.
