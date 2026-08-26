# New Chat Startup Prompt — ProxyToAnyConnect

Скопируй весь текст ниже первым сообщением в новый чат.

---

Продолжаем разработку private GitHub-проекта **`lukindv77/ProxyToAnyConnect`** после длинного предыдущего чата. Не начинай проект заново и не переосмысливай зафиксированные требования. **Live GitHub `main` — главный source of truth.**

## Сначала синхронизируйся с GitHub

До любых изменений:

1. Получи current `main` SHA.
2. Прочитай на current `main`:
   - `docs/handoff/CURRENT_STATE.md`
   - `docs/handoff/AUDIT_SNAPSHOT.md`
   - `docs/handoff/ISSUES_SNAPSHOT.md`
   - `docs/handoff/MANIFEST.md`
   - `docs/handoff/HANDOFF_INDEX.md`
   - `docs/requirements.md`
   - `docs/architecture.md`
   - `docs/memory-stability.md`
   - `docs/windows-integration-test.md`
   - `README.md`
   - `.github/workflows/build.yml`
   - `.github/workflows/handoff.yml`
3. Получи live issues и последние comments, особенно #11, #13, #14, #15.
4. Проверь `build` и `handoff` Actions **для exact current head**. Не называй код green по старому SHA.
5. Перед архитектурными изменениями просмотри актуальные `Runtime`, `Proxy`, `Network`, `Vpn`, `Configuration`, `Gui` и self-tests.
6. Если prompt расходится с live GitHub, приоритет у live GitHub.

## Проект и неизменяемые требования

Windows 11 x64 GUI-приложение C# / **.NET 10 `net10.0-windows`**, WinForms + tray. Поднимает несколько локальных HTTP/HTTPS forward proxy; каждый отправляет трафик **только через выбранное L2TP**, без DIRECT fallback. Домены выбираются внешним PAC/браузером.

Не откатывать:

- всегда GUI; `X` скрывает в tray, процесс завершает только явный Exit;
- multiple independent proxies: bind IPv4, port, timeouts, `maxConcurrentConnections`, selected L2TP, state, RX/TX, Pause/Resume;
- shared/dedicated L2TP leases; first lease dial+verify, last release disconnect/RasHangUp;
- ExistingWindowsProfile + CustomEphemeral private `.pbk`, без persistent Windows Settings VPN profile;
- password/PSK — Windows user-bound DPAPI only, never plaintext;
- keepalive Off / PPP server internal IPv4 / CustomIPv4; threshold => fail-closed teardown + reconnect while leases remain;
- append-only JSONL `<root>/YYYY-MM/YYYY-MM-DD.jsonl`, retention, no secrets/body/tunnel contents;
- HTTPS CONNECT only, no MITM.

### Fail-closed invariants

- никогда DIRECT;
- outbound proxy TCP socket всегда source `Bind()` к L2TP IPv4 + `IP_UNICAST_IF` L2TP ifIndex;
- proxied DNS — custom L2TP-bound resolver, не `System.Net.Dns`;
- existing profile preflight: L2TP + split tunneling;
- IPv4 default-route guard before/after dial + continuous;
- `Disconnected -> Dialing -> Verifying -> Ready`; context не публикуется до Ready;
- real L2TP-bound HTTPS verification; fixed expected IPv4 должен совпасть;
- L2TP loss cancels dependent sessions;
- `ProxyServer.RunAsync` drains accepted sessions before higher runtime may release its L2TP lease.

### Performance / memory

Fail-closed, latency/throughput и bounded whole-process memory равноправны. Не добавлять global locks/sync waits/extra copies/serialization/per-buffer allocations в hot path; production forced GC запрещён; buffers нельзя уменьшать ради working set ценой syscall/latency/throughput. Memory-only regression beyond measurement noise не принимается.

## Что уже реализовано

WinForms/tray, multi-proxy runtime, shared/dedicated `VpnLeaseManager`, Pause/Resume, existing profile validation, CustomEphemeral phonebook + DPAPI, RAS IPv4/interface/DNS discovery, source+interface socket binding, route guards, HTTPS verification, L2TP-bound DNS UDP/TCP/CNAME/bounded cache, HTTP proxy + CONNECT, pooled transfer buffers, bounded session admission, deterministic shutdown drain, traffic/ping metrics, append-only logs, L2TP latest status GUI/backend, process-memory snapshot, deterministic `VpnContext` ownership, per-RAS-session monitor CTS/task ownership, selective reconfigure isolation, cancellation reconciliation and long-run collectability tests.

Не регрессируй per-RAS-session monitor ownership: stale monitor после disconnect не может hangup replacement handle.

## Последний большой code block — issue #14 HTTP framing/request-smuggling

Production commit **`f9db53f074d6740296e46452077622099b6f64ff`** (`fix: enforce plain HTTP request framing`).

Plain HTTP теперь:
- validates framing before outbound connect;
- accepts only one non-negative decimal `Content-Length`;
- rejects duplicate/conflicting/comma-list CL;
- rejects any `Transfer-Encoding`, including TE+CL;
- no CL => zero body;
- already-read post-header bytes cannot exceed CL;
- forwards exactly CL body bytes and never later pipelined/smuggled bytes;
- fails early EOF;
- preserves valid CL;
- CONNECT remains opaque/unchanged.

Added `ProxyHttpFramingSelfTests.cs` and runner integration.

## Два текущих CI findings — оба нужно решить

### A. `ProxySetupTimingSelfTests` demonstrably unstable across identical code

Current test source uses paired alternating measurement, warmup 2048, 9 rounds, 32768 ops/round and unchanged **1.25x** slowdown policy.

На docs-only heads с одинаковым production/test tree были разные результаты:

- build **#272** on `b3fbe1f96c0ffa7d031cb72b81793ec6ea9c2858`: PASS — parser `1999 vs 2042 ns/op = 0.98x`, origin `973 vs 1222 = 0.80x`;
- build **#273** on `b304a4331b8527b8280396047d3c649cfaed80f3`: FAIL — parser `5218 vs 2917 ns/op = 1.79x`, limit 1.25x.

Между этими heads менялись только handoff docs, не parser code. Поэтому один hosted-runner ratio сейчас не является воспроизводимым gate.

**Первая задача нового чата:** source-level audit benchmark methodology. Не просто расширять 1.25x. Сделай current vs predecessor workload действительно эквивалентным и measurement устойчивым к hosted-runner scheduling/JIT/CPU migration/GC effects. Возможные направления: paired per-round ratio rather than ratio of independent medians, stronger randomized/interleaved batching, enough batch duration, GC noise isolation only in test harness, verify predecessor semantics include equivalent framing work if это честный immediate predecessor. Не меняй production ради шумного теста без evidence.

### B. Когда timing прошёл, проявился реальный framing-suite blocker

В build #272 suite дошёл до `ProxyHttpFramingSelfTests.ExactContentLengthBoundsClientToOriginBytesAsync` и упал:

- `IOException: Unable to read data from the transport connection`;
- inner Windows `SocketException 10054`: connection forcibly closed by remote host;
- failure in test `ReadToEndAsync` while reading proxy response.

Исследуй, не является ли RST следствием того, что proxy намеренно прочитал ровно declared CL и закрыл client socket, оставив malicious trailing/smuggled bytes unread; Windows может reset connection при close с unread receive data. Но также исключи premature production close до полного origin response.

**Не ослаблять semantic invariant:** bytes after declared CL никогда не должны попасть origin. Если valid response уже полностью получен до reset, тесту возможно следует читать/валидировать ожидаемое HTTP response framing/body, а не требовать clean EOF. Это гипотеза — сначала докажи packet/stream behavior тестом.

Issue #14 остаётся open до устойчивого timing gate + exact-head green framing suite.

## Следующий confirmed lifecycle bug — issue #15

`ProxyInstanceRuntime.StartAsync` может acquire lease, создать run task, записать `_lease/_runCancellation/_runTask`, затем fail/cancel в `WaitUntilListeningAsync`. Current catch может cleanup local resources без гарантированного await exact run-task drain и очистки уже опубликованных fields — риск release last L2TP lease до listener/session drain и stale disposed ownership.

Required failure order:

`cancel exact run CTS -> await exact runTask drain -> clear fields only if same attempt/generation -> dispose CTS -> release exact L2TP lease once`.

Preserve caller cancellation, safe retry, Pause/Dispose idempotence, no double release/unobserved observer, unchanged successful Running path.

План deterministic seam: orchestration-only injectable lease acquisition + server lifetime (`RunAsync` / `WaitUntilListeningAsync`). Production chain остаётся `VpnLeaseManager -> L2tpDnsResolver -> L2tpSocketFactory -> ProxyServer`; network hot path не менять.

## Issue snapshot

Open: **#2, #4, #5, #6, #7, #11, #13, #14, #15**.
Closed: **#1, #3, #8, #9, #10, #12**.

#2 — mandatory real Windows 11 + real L2TP E2E. #4/#5/#6/#7 — остаточная real-environment acceptance. #11/#13 — ongoing performance/memory/lifetime hardening.

## Недавние результаты, которые нельзя потерять

- canonical listener validation parses `IPAddress`; `127.1` и `127.0.0.1` не обходят bind collision guard;
- selective reconfigure preserves exact object identity independent groups;
- 250 cycles: retained replaced `ProxyInstanceRuntime` 0/250, `VpnLeaseManager` 0/250 in recorded Windows test;
- incremental CRLFCRLF scan avoids whole-prefix rescans;
- runtime start/reconfigure cancellation reconciliation regression exists;
- shutdown drain ensures accepted sessions cleanup before `RunAsync` returns and before lease release.

## Handoff archive

`.github/workflows/handoff.yml` creates GitHub Actions artifact `ProxyToAnyConnect-handoff-<sha>` with exact `src`, `tests`, `docs`, `.github`, README, solution and `HANDOFF_BUILD_INFO.txt`. Use the **latest artifact for current main head** and this prompt from `docs/handoff/NEW_CHAT_PROMPT.md`.

## Рабочий процесс

- Общайся по-русски, технически и прямо.
- Не задавай повторно вопросы, уже отвеченные в GitHub/requirements/handoff.
- Новые findings/goals фиксируй в GitHub issues/docs вместе с code changes.
- Обычно commit directly to `main`, если пользователь не изменил workflow.
- Проверяй exact-head build + handoff после значимых changes.
- Не утверждай green без exact current-head success.

## Начни сразу

1. Fetch live head/actions/issues/docs.
2. Подтверди current SHA и exact-head CI.
3. **Сначала стабилизируй и сделай честным `ProxySetupTimingSelfTests` без простого ослабления 1.25x policy.** Учитывай доказанную разницу #272 PASS vs #273 FAIL на docs-only heads.
4. Затем воспроизведи/исправь `ProxyHttpFramingSelfTests` SocketException 10054, сохранив строгую CL smuggling boundary.
5. Добейся exact-head green через framing suite; обнови/закрой #14 только после acceptance.
6. Реализуй #15 transactional startup ownership с cancel/fail/drain/retry/single-release tests.
7. Продолжай крупными блоками #11/#13 и real Windows acceptance, не ограничиваясь одной мелкой задачей.

---
