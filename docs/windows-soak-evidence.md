# Windows long-run soak evidence

This procedure produces repeatable long-run process-stability evidence for issue #13 without changing the production data path or forcing garbage collection.

It complements, but does not replace, the real Windows 11 + L2TP integration acceptance procedure in `windows-integration-test.md` and `windows-integration-evidence.md`.

## What the collector proves

`tools/Invoke-WindowsSoakEvidence.ps1` attaches to one already-running process by PID and verifies process name, process start time on every sample (rejecting PID reuse), and SHA-256 of the exact executable. It samples working set, private bytes, handle count and thread count. Samples are written immediately to JSONL while the collector retains only scalar min/max/first/last aggregates, so a 12–24 hour run does not create an unbounded in-memory history.

The application separately records `process.memory.startup` and `process.memory.periodic` events with managed heap/allocation/GC, working/private bytes, handles and threads. Preserve those logs for correlation with the external series.

## Required release identity

Use the self-contained Windows artifact produced by GitHub Actions and its `ProxyToAnyConnect.exe.sha256` / `build-identity.json`. Evidence is valid only for the executable actually running during the soak.

## 12-hour collection

Example:

```powershell
.\tools\Invoke-WindowsSoakEvidence.ps1 `
  -ProcessId $process[0].Id `
  -ExpectedProcessName 'ProxyToAnyConnect' `
  -ExpectedExecutableSha256 $identity.sha256 `
  -DurationSeconds 43200 `
  -SampleIntervalSeconds 300 `
  -OutputDirectory .\artifacts\soak-12h
```

Use `-DurationSeconds 86400` for 24 hours. The collector fails if the target exits, its PID is reused, its process name changes, or its executable hash no longer matches.

## Validate bundle integrity

```powershell
.\tools\Test-WindowsSoakEvidence.ps1 `
  -OutputDirectory .\artifacts\soak-12h `
  -ExpectedExecutableSha256 $identity.sha256 `
  -MinimumSamples 140 `
  -MinimumObservedDurationSeconds 42600
```

### Canonical observed-duration contract (#47)

`observedDurationSeconds` has one canonical representation: the UTC span between the **first and last timestamps exactly as serialized into `process-samples.jsonl`**. The collector summary/result derives the value from those serialized timestamps. The validator reparses the same serialized sample stream and requires the recomputed span to match within the existing 50 ms consistency tolerance.

Do **not** derive acceptance duration from a separate higher-precision in-memory clock path, and do **not** widen the tolerance to hide representation mismatches. The minimum observed-duration threshold is independent of this internal consistency check.

The validator also checks schema versions, exact executable SHA in metadata/summary/result, complete portable manifest paths/lengths/hashes, sample count/index continuity, stable PID/name/start-time identity, monotonic timestamps, non-negative resource metrics, requested minimum samples/duration, and summary/result duration consistency against the serialized first/last sample timestamps.

Validation proves bundle identity/integrity/consistency; it does **not** by itself prove that the application is leak-free.

## Evidence files and portability

The bundle contains `metadata.json`, append-only `process-samples.jsonl`, `summary.json`, `result.json`, and `manifest.json`. Payloads deliberately avoid host-specific absolute paths. Editing any manifested payload after collection must make validation fail; do not regenerate only the manifest around edited evidence.

## Workload for release-grade soak

Exercise representative traffic and lifecycle churn: multiple proxies, sustained HTTP/CONNECT, client churn, shared/dedicated L2TP, Pause/Resume, selective reconfiguration, reconnect/verification cycles, keepalive success/failure/recovery, and CustomEphemeral cleanup where applicable.

## Acceptance interpretation

Do not use a machine-specific working-set number as the sole pass/fail criterion. Review external series with application `process.memory.*` records and lifecycle logs. Repeated comparable steady states must not show monotonic retained growth in managed heap, handles, threads or other owned state. If retention is suspected, reproduce it with deterministic ownership tests first. Do not add forced `GC.Collect`, working-set trimming, synchronous hot-path cleanup, or smaller transfer buffers merely to improve graphs.

## Hosted CI coverage

Hosted Windows CI performs a short smoke of collector/validator identity, sampling, manifest integrity, duration consistency and tamper rejection. It is tooling mechanics only and does not replace the required multi-hour Windows 11/L2TP soak.
