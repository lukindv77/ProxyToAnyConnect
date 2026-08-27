# New Chat Startup Prompt — ProxyToAnyConnect

Скопируй весь текст после разделителя первым сообщением в новый чат.

---

Продолжаем разработку публичного GitHub-репозитория **`lukindv77/ProxyToAnyConnect`** после длинного предыдущего чата. Не начинай проект заново. **Live GitHub `main` — главный source of truth.** Репозиторий защищён ruleset; прямые изменения `main` разрешены владельцу и ChatGPT Codex Connector.

## Обязательная синхронизация перед любой разработкой

1. Получи exact current `main` SHA.
2. Прочитай на current `main`:
   - `docs/handoff/NEW_CHAT_PROMPT.md`
   - `docs/handoff/CURRENT_STATE.md`
   - `docs/handoff/AUDIT_SNAPSHOT.md`
   - `docs/handoff/ACTIVE_DEVELOPMENT.md`
   - `docs/handoff/FINAL_CI_STATUS.md`
   - `docs/handoff/ISSUES_SNAPSHOT.md`
   - `docs/requirements.md`
   - `docs/architecture.md`
   - `docs/memory-stability.md`
   - `docs/windows-integration-test.md`
   - `docs/windows-integration-evidence.md`
   - `docs/windows-soak-evidence.md`
   - `.github/workflows/build.yml`
   - `.github/workflows/handoff.yml`
3. Получи live issues и последние comments для **#2, #4, #5, #6, #7, #11, #13**. #14 и #15 уже закрыты как completed.
4. Проверь `build` и `handoff` Actions именно для current head. Не называй head green по старому SHA.
5. Перед изменением блока перечитай его current production code и self-tests. Если этот prompt расходится с live GitHub, приоритет у live GitHub.

## Product contract — не ослаблять

Windows 11 x64 GUI-приложение C# / .NET 10 `net10.0-windows`, WinForms + tray. Несколько локальных HTTP/HTTPS forward proxy; каждый отправляет трафик **только через выбранное L2TP**, без DIRECT fallback.

Обязательные инварианты:

- приложение всегда GUI/tray; `X` скрывает окно, процесс завершает только explicit Exit;
- multiple independent proxy listeners с отдельными bind IPv4/port/timeouts/max concurrency/state/RX/TX/Pause/Resume;
- shared/dedicated L2TP leases; first active lease dial+verify, last release disconnect;
- ExistingWindowsProfile + CustomEphemeral private temporary RAS phonebook;
- password/PSK — Windows user-bound DPAPI only, never plaintext in config/logs;
- никакого DIRECT fallback;
- outbound proxy TCP всегда `Bind()` к L2TP source IPv4 + `IP_UNICAST_IF` L2TP ifIndex;
- proxied DNS только custom L2TP-bound resolver, не `System.Net.Dns`;
- existing profile preflight требует L2TP + split tunnel;
- IPv4 default-route guard before/after dial и continuously;
- `Disconnected -> Dialing -> Verifying -> Ready`; usable `VpnContext` не публикуется до Ready;
- verification — реальный HTTPS через L2TP-bound socket; expected public IPv4 должен совпадать, если задан;
- L2TP loss cancels dependent proxy sessions fail-closed;
- HTTPS CONNECT — opaque tunnel, без MITM;
- 32 KiB transfer buffers; bounded process memory/latency — first-class requirements;
- production forced GC запрещён;
- `ProxyServer.RunAsync` обязан drain accepted sessions до того, как higher runtime release-ит L2TP lease.

## Текущий проверенный инженерный baseline

Перед финальной handoff-упаковкой substantive head был **`4b100f3bb6c744b08918ce122ab75982fa263740`** (`evidence: support per-proxy and direct egress expectations`). Windows build **#534** полностью success; handoff **#340** success. Build artifact `9637762202`, digest `sha256:be01041fefa07c4fe4dd39f4a02e5c038b9e729b97049a7da4880d685aedf239`.

После handoff-doc/workflow commits SHA будет новее; поэтому новый чат обязан повторно проверить exact live head. Handoff archive содержит `HANDOFF_BUILD_INFO.txt` с exact SHA своей сборки.

## Что уже реализовано и принято Windows CI

### Proxy / HTTP / data path

- HTTP forward proxy + HTTPS CONNECT без MITM;
- strict HTTP request framing/request-smuggling boundary: single valid Content-Length, reject ambiguous CL/TE, exact body forwarding, no post-CL leakage; #14 закрыт;
- pooled 32 KiB transfer buffers, bounded connection admission/backpressure, incremental header scan, allocation/timing/data-path regressions;
- accepted sessions deterministically drain before listener run completion and lease release.

### Transactional proxy lifecycle

- startup publication boundary — listener readiness;
- rejected/cancelled start cancels exact run CTS, drains exact task, clears same-generation ownership and only затем releases exact lease; #15 закрыт;
- Pause/Dispose preserves `cancel -> drain -> release lease` ordering;
- caller cancellation remains control flow and does not get replaced by secondary cleanup defects.

### RAS / L2TP / native ownership

- callback-driven async `RasDialW` with exact `HRASCONN` ownership;
- managed password carrier cleared immediately after native handoff; PSK carrier cleared after `RasSetCredentialsW`;
- callback root stays alive until Connected or proven terminal `ERROR_INVALID_HANDLE`;
- one hangup/drain attempt is bounded (production 10 s); timeout does **not** release callback root or falsely declare terminal state; exact handle remains retryable;
- native callback-root registry has deterministic churn coverage and current/high-watermark diagnostics;
- RAS manager cleanup, monitor cancellation and residual-handle retry preserve primary failure while draining independent owners;
- CustomEphemeral uses private temporary PBK, lock-first ownership marker protocol, orphan recovery and stale entry deletion; repeated partial-creation failure cycles leave no accumulating managed session directories;
- ExistingWindowsProfile enumeration owns and kills/drains its PowerShell helper process tree on cancellation/timeout; L2TP settings dialog owns exact profile-load task and shutdown waits it.

### VPN leases / keepalive / reconnect

- shared/dedicated `VpnLeaseManager` semantics;
- first lease connects/verifies, last release disconnects and clears L2TP DNS cache;
- shared failure invalidates dependents fail-closed while unrelated VPN groups remain independent;
- reconnect cooldown is observed without exception/log churn; maintenance stops promptly after last lease;
- cleanup failures do not prevent release of independent DNS/status/lifetime owners.

### Runtime coordinator / concurrency / recovery

- Start/Pause/Reconfigure serialized at runtime-generation boundaries;
- independent proxy start/restart generations may run concurrently inside one coordinator operation; failure of one group remains isolated/pending while unrelated group may reach Running;
- independent proxy cleanup owners drain concurrently inside proxy phase; all proxy cleanup completes before VPN-manager phase; independent VPN managers then drain concurrently;
- cleanup primary/secondary failure ordering is deterministic by input order, not scheduler completion order;
- same-config apply detects missing topology as drift and recreates missing VPN/proxy generations;
- pending desired starts survive interrupted startup/reconfigure and retry on identical config;
- host/coordinator/VPN/RAS cleanup continues through throwing cancellation callbacks.

### GUI / configuration (#5)

- strict FIFO GUI generation queue for Add/Edit/Remove/Logging **и Start/Pause**;
- unique-temp transactional `appsettings.json` save; file changes only after complete serialization and cancellation boundary;
- persisted desired state is authoritative after durable save even if runtime reconciliation fails;
- `desired ∪ actual` grid projection shows desired-but-missing runtime and residual cleanup drift;
- legacy invalid config supports multi-step in-memory staged repair: incomplete invalid generations do not reach disk/runtime; final repair publishes the whole accumulated valid generation;
- logging and runtime consume the same persisted desired generation independently; logging edit completing the last invalid field also applies staged proxy/VPN repairs;
- caller cancellation wins over secondary persisted-consumer faults;
- explicit Exit closes active configuration modal, stops new queue admission, cancels/drains exact config generations/profile helper, then disposes runtime and memory monitor;
- loaded invalid numeric settings are clamped in editors so repair UI remains usable;
- unused protected password/PSK are pruned when auth/mode changes.

#5 remains open because real operator-facing Windows 11 GUI + actual VPN profile/L2TP acceptance is still required.

### Evidence / long-run stability (#2/#13)

- Baseline -> Ready -> Final Windows evidence with route/profile/interface/process snapshots, proxy/direct probes and aggregate acceptance summary;
- exact running `ProxyToAnyConnect.exe` SHA-256 captured and can be matched to CI `build-identity.json` / `.sha256`;
- latest evidence extension supports per-proxy expected public IPv4 overrides for heterogeneous shared/dedicated egress and an explicit expected direct-host public IPv4;
- portable manifest-protected soak bundle with exact PID/start-time/executable SHA, PID-reuse rejection and streaming bounded-memory validation;
- external working/private bytes/handles/threads soak data correlates to application `process.memory.*` managed heap/GC records using the same PID + process start time;
- process memory monitor retains only bounded current state; no forced GC in production;
- 250-cycle proxy/reconfigure/lifetime stress and large native callback-root churn regressions exist.

Hosted smoke validates mechanics only. #13 still needs representative **12–24 h real soak** with traffic/reconnect/Pause/Resume/reconfigure.

## Open release boundary

Open issues: **#2, #4, #5, #6, #7, #11, #13**.

Main blockers to first real beta/RC are external acceptance, not missing core architecture:

1. Windows 11 x64 + real L2TP endpoint E2E (#2).
2. Multi-proxy shared/dedicated real lease behavior (#4).
3. Manual GUI/operator acceptance with actual profiles and selective live effects (#5).
4. Real CustomEphemeral authentication/PSK/certificate and cleanup (#6).
5. Real PPP-server/CustomIPv4 keepalive failure -> hangup -> cooldown -> reconnect behavior (#7).
6. 12–24 h representative exact-binary soak and memory/resource trend review (#13).
7. Continue #11 performance/memory hardening only when it does not weaken fail-closed or data-path latency/throughput.

## Immediate continuation order in the new chat

1. Fetch live `main`, issues/comments and exact-head Actions; state the exact SHA and verdict before coding.
2. Inspect the latest per-proxy/direct egress expectation work around `4b100f3...`; ensure `Invoke/Test/Complete-WindowsIntegrationEvidence.ps1` and hosted positive/negative smoke all enforce the new contract end-to-end. Do not assume a single collector commit completes the validator path.
3. Move the authoritative checkpoint only after exact-head Windows build + publish/upload success.
4. Continue broad deterministic lifecycle/stress work where real endpoint is not required; do not duplicate already-closed RAS hangup timeout, #14 or #15 work.
5. When a real Windows 11/L2TP endpoint is available, execute the release-critical #2/#4/#5/#6/#7 matrix and capture the exact-binary evidence bundle.
6. Run the documented 12–24 h #13 soak and correlate external/native + managed memory series.
7. Fix findings in multiple coherent commits, update GitHub issues/docs as work proceeds, and keep `main` + handoff archive synchronized.

## Handoff archive

`.github/workflows/handoff.yml` creates artifact **`ProxyToAnyConnect-handoff-<sha>`**. The archive includes `src/`, `tests/`, `tools/`, `docs/`, `.github/`, solution/README, `HANDOFF_BUILD_INFO.txt`, `RECENT_COMMITS.tsv` and `START_HERE.txt`. Use the latest artifact whose embedded SHA equals current `main` (or explicitly treat a newer live `main` as authoritative).

## Рабочий стиль

- Общайся с пользователем по-русски.
- Не ограничивайся одной мелкой задачей/одним коммитом: пользователь просит широкие функциональные блоки.
- Новые findings, цели, acceptance и результаты фиксируй в GitHub issues/docs.
- Не задавай вопросы, ответ на которые уже есть в requirements/live GitHub.
- Не утверждай локальную компиляцию: authoritative compile/test — Windows GitHub Actions.
- Никогда не ослабляй fail-closed invariants ради удобства тестов или performance.

Начни сразу с live GitHub synchronization и продолжай разработку от фактического current head.

---
