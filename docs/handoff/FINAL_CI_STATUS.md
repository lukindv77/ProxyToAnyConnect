# Final CI status at handoff — 2026-08-27

Live protected `main` is authoritative. A source block is called accepted only after an exact-head Windows build completes successfully.

## Last fully verified substantive source checkpoint before handoff-only commits

Commit `4b100f3bb6c744b08918ce122ab75982fa263740` — `evidence: support per-proxy and direct egress expectations`.

Windows build #534, run `33051353263`, completed successfully on `windows-latest`:

- PowerShell tool validation: PASS;
- Baseline -> Ready -> Final integration-evidence smoke: PASS;
- exact binary/process identity smoke: PASS;
- Windows soak + managed-log correlation smoke: PASS;
- restore/build: PASS;
- aggregate self-tests: PASS;
- self-contained win-x64 publish: PASS;
- binary identity manifest / ZIP / artifact upload: PASS.

Build artifact `9637762202`, `ProxyToAnyConnect-win-x64`, digest:
`sha256:be01041fefa07c4fe4dd39f4a02e5c038b9e729b97049a7da4880d685aedf239`.

Handoff #340 for the same SHA also succeeded. Its historical archive artifact was `9637723727`, digest `sha256:a5f6938cd10c8ba1cede68e96c4e57968f12c3a0f3bc8d7bca6182f14061b948`.

## What this checkpoint includes

This checkpoint includes all previously accepted fail-closed, HTTP framing, transactional proxy startup, configuration, RAS/VPN, GUI shutdown and evidence work plus the current broad hardening state:

- staged multi-step repair of invalid loaded configuration before one complete durable publication;
- one persisted desired generation consumed independently by logging + runtime, with caller-cancellation precedence;
- GUI Start/Pause serialized with configuration generations;
- L2TP dialog-owned Windows-profile helper cancellation/drain before config-generation release;
- bounded/streaming exact-binary soak evidence and managed-log correlation;
- process-memory monitor and cleanup ownership resilient to cancellation callback faults;
- process-wide native RAS callback-root current count + high-watermark telemetry;
- bounded RAS hangup/drain attempt that retains exact native ownership on timeout rather than risking callback-after-free;
- independent proxy start/restart generations running concurrently inside one serialized coordinator operation;
- independent cleanup owners running concurrently inside dependency phases, with proxy phase completed before VPN-manager phase;
- failed independent proxy start isolated/pending while unrelated generation can become Running;
- cleanup primary/secondary diagnostics deterministic by input order;
- integration evidence collector support for per-proxy expected public IPv4 plus a separate expected direct-host public IPv4.

## Handoff-only commits after this checkpoint

The handoff preparation intentionally updates documentation and `.github/workflows/handoff.yml`, moving the live SHA forward without changing the accepted runtime/data path. The new handoff workflow includes `tools/`, `RECENT_COMMITS.tsv`, `START_HERE.txt` and 90-day artifact retention.

The new chat must fetch the exact live head and its Actions. Do not call a handoff-only head green solely because `4b100f3...` was green; verify current Actions explicitly.

## Release boundary still open

Hosted CI cannot replace real Windows 11 + real L2TP endpoint acceptance. Issues #2/#4/#5/#6/#7 remain open for endpoint/UI validation. Issue #13 additionally requires a representative 12–24 h exact-binary soak under CONNECT/HTTP traffic, shared/dedicated L2TP, reconnect, Pause/Resume and reconfigure activity, with external soak samples correlated to `process.memory.*` logs. #11 remains an ongoing performance/memory architecture requirement.
