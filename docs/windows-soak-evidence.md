# Windows long-run soak evidence

This procedure produces repeatable long-run process-stability evidence for issue #13 without changing the production data path or forcing garbage collection.

It complements, but does not replace, the real Windows 11 + L2TP integration acceptance procedure in `windows-integration-test.md` and `windows-integration-evidence.md`.

## What the collector proves

`tools/Invoke-WindowsSoakEvidence.ps1` attaches to one already-running process by PID and first verifies:

- the process name expected by the operator;
- the process start time, which is retained and checked on every sample so PID reuse is rejected;
- SHA-256 of the executable backing that process against the exact expected release binary hash.

After identity is established, the collector samples only process-level diagnostics outside the proxy byte-transfer path:

- working set;
- private bytes;
- Windows handle count;
- process thread count.

The collector writes each sample immediately to JSONL and retains only scalar min/max/first/last aggregates in its own process. A 12-24 hour run therefore does not create an unbounded in-memory history.

The application itself already records `process.memory.startup` and `process.memory.periodic` JSONL events containing managed heap, total allocated bytes, GC counts, working set, private bytes, handles and threads. Keep those application logs for the same time window and correlate them with the external soak series. The external collector intentionally does not inject diagnostics into the target process merely to obtain managed-heap data.

## Required release identity

Use the self-contained Windows artifact produced by GitHub Actions. The artifact contains:

- `ProxyToAnyConnect.exe`;
- `ProxyToAnyConnect.exe.sha256`;
- `build-identity.json` containing the Git commit and executable SHA-256.

Do not type or copy a hash from a different build. The soak evidence is valid only for the executable actually running during the soak.

Example PowerShell setup after starting the release binary:

```powershell
$identity = Get-Content .\build-identity.json -Raw | ConvertFrom-Json
$process = @(Get-Process -Name ProxyToAnyConnect -ErrorAction Stop)
if ($process.Count -ne 1) {
    throw "Expected exactly one ProxyToAnyConnect process, found $($process.Count)."
}
```

## 12-hour collection

A five-minute interval yields approximately 145 external samples during a 12-hour run while adding negligible monitoring overhead:

```powershell
.\tools\Invoke-WindowsSoakEvidence.ps1 `
  -ProcessId $process[0].Id `
  -ExpectedProcessName 'ProxyToAnyConnect' `
  -ExpectedExecutableSha256 $identity.sha256 `
  -DurationSeconds 43200 `
  -SampleIntervalSeconds 300 `
  -OutputDirectory .\artifacts\soak-12h
```

For a 24-hour acceptance run use `-DurationSeconds 86400`.

The collector fails if the target process exits, its PID is reused, its process name changes, or its executable hash does not match the expected release identity.

## Validate bundle integrity

After collection:

```powershell
.\tools\Test-WindowsSoakEvidence.ps1 `
  -OutputDirectory .\artifacts\soak-12h `
  -ExpectedExecutableSha256 $identity.sha256 `
  -MinimumSamples 140 `
  -MinimumObservedDurationSeconds 42600
```

The small duration tolerance accounts for time between collector initialization and the first external sample. For a 24-hour run choose corresponding minimums appropriate to the configured interval.

The validator checks:

- evidence schema versions;
- exact executable SHA-256;
- manifest file lengths and SHA-256 values;
- sample count against the summary;
- contiguous sample indexes;
- stable PID, process name and process start time across every sample;
- monotonic sample timestamps;
- non-negative memory/handle/thread measurements;
- requested minimum sample count and observed duration.

Validation means the evidence bundle is internally consistent and belongs to the expected process binary. It does **not** automatically declare the application leak-free.

## Evidence files

The output directory contains:

- `metadata.json` — exact process/binary identity and requested sampling parameters;
- `process-samples.jsonl` — append-only external sample stream;
- `summary.json` — first/last/min/max/delta process metrics and collection status;
- `manifest.json` — SHA-256 and byte length for the evidence files;
- `result.json` — compact successful-collection result.

The collector deliberately does not store the executable path in the evidence bundle; the release identity is represented by process name, PID/start time and SHA-256.

## Workload to exercise during the soak

A release-grade #13 soak should include representative traffic and lifecycle churn rather than an idle-only process. Where the real endpoint permits it, exercise:

- multiple concurrently configured proxy listeners;
- sustained HTTP and CONNECT traffic;
- repeated client connect/disconnect cycles;
- shared and dedicated L2TP usage;
- Pause/Resume cycles;
- selective proxy-only and L2TP-dependent configuration changes;
- L2TP reconnects and full verification cycles;
- keepalive success and controlled failure/recovery cases;
- CustomEphemeral create/disconnect cleanup when that mode is under acceptance.

Keep timestamps or operational notes for deliberate lifecycle events so changes in process metrics can be related to actual workload transitions.

## Acceptance interpretation

Do not use a machine-specific absolute working-set number as the sole pass/fail criterion. Windows socket buffers, runtime GC policy, current traffic and OS memory pressure can change the working set without representing retained application ownership.

Review the external sample series together with the application's `process.memory.*` records and lifecycle logs. Repeated session/reconnect/reconfigure cycles must not create monotonic retained growth in managed heap, handles, threads or other owned state after the workload returns to a comparable steady condition.

If a suspected retention pattern appears, reproduce it with the deterministic ownership/collectability self-tests first. Production code must not add forced `GC.Collect`, working-set trimming, synchronous hot-path cleanup or smaller transfer buffers merely to make soak graphs look smaller.

## Hosted CI coverage

The Windows build workflow performs a short smoke run of both soak scripts against the current PowerShell process. That smoke validates script syntax, process identity/hash checking, sample emission, manifest integrity and validator execution without pretending to replace the required multi-hour Windows 11/L2TP soak.
