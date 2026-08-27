# Final CI status at handoff — 2026-08-27

Live protected `main` is authoritative. A source block is called accepted only after an exact-head Windows build completes successfully.

## Authoritative accepted source checkpoint

Commit `fb0d2743b815204fe87eb7ba972513894b41f445` — `test: prove independent coordinator starts overlap`.

Windows build #531, run `33051006335`, job `98446224154`, completed successfully on `windows-latest`:

- PowerShell tool validation: PASS;
- Baseline -> Ready -> Final integration-evidence smoke: PASS;
- exact binary/process identity smoke: PASS;
- Windows soak + managed-log correlation smoke: PASS;
- restore/build: PASS;
- aggregate self-tests: PASS;
- self-contained win-x64 publish: PASS;
- binary identity manifest / ZIP / artifact upload: PASS.

Artifact `9637611201`, `ProxyToAnyConnect-win-x64`, digest:
`sha256:8042f82e1343f2f5133d85bbf7e576942cd2bd20857d0f286445404292ba5c55`.

This checkpoint includes all previously accepted fail-closed, configuration, RAS/VPN, GUI shutdown and evidence work plus:

- staged repair of multiple invalid loaded configuration fields before one complete durable publication;
- one persisted desired generation applied independently to logging + runtime with caller-cancellation precedence;
- GUI Start/Pause serialized with configuration generations;
- L2TP dialog-owned Windows-profile helper cancellation/drain before config-generation release;
- bounded/streaming exact-binary soak evidence and log correlation;
- process-memory monitor cleanup through throwing cancellation callbacks;
- process-wide native callback-root current count + high-watermark telemetry, with large sequential/concurrent churn returning to baseline;
- independent proxy start/restart generations run concurrently inside one serialized coordinator generation;
- independent proxy cleanup owners run concurrently inside the proxy phase and independent VPN managers concurrently inside the VPN phase;
- the proxy phase remains a hard barrier before VPN-manager disposal;
- failed proxy start remains isolated/pending while an unrelated generation may become Running;
- cleanup primary/secondary diagnostics remain deterministic by input order rather than completion order.

## Release boundary still open

Hosted CI cannot replace real Windows 11 + real L2TP endpoint acceptance. Issues #2/#4/#5/#6/#7 remain open for endpoint/UI validation. Issue #13 additionally requires a representative 12–24h exact-binary soak under CONNECT/HTTP traffic, shared/dedicated L2TP, reconnect, Pause/Resume and reconfigure activity, with external soak samples correlated to `process.memory.*` logs.
