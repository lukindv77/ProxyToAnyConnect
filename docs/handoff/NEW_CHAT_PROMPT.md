# New Chat Startup Prompt — ProxyToAnyConnect

Скопируй весь текст ниже первым сообщением в новый чат.

---

Продолжаем разработку private GitHub-проекта **`lukindv77/ProxyToAnyConnect`** после длинного предыдущего чата. Не начинай проект заново и не переосмысливай зафиксированные требования. **Актуальный GitHub `main` — главный source of truth.**

## Сначала синхронизируйся с GitHub

До любых изменений:

1. Получи текущий `main` SHA.
2. Прочитай на актуальном `main`:
   - `docs/handoff/CURRENT_STATE.md`
   - `docs/handoff/AUDIT_SNAPSHOT.md`
   - `docs/handoff/ISSUES_SNAPSHOT.md`
   - `docs/handoff/MANIFEST.md`
   - `docs/requirements.md`
   - `docs/architecture.md`
   - `docs/memory-stability.md`
   - `docs/windows-integration-test.md`
   - `README.md`
   - `.github/workflows/build.yml`
   - `.github/workflows/handoff.yml`
3. Получи актуальные GitHub Issues и последние комментарии к open issues.
4. Проверь `build` и `handoff` GitHub Actions **для текущего head**, а не для старого commit.
5. Перед архитектурными изменениями просмотри текущие `src/ProxyToAnyConnect/Runtime`, `Proxy`, `Network`, `Vpn`, `Configuration`, `Gui` и соответствующие self-tests.
6. Если prompt/handoff расходится с live GitHub, приоритет у live GitHub, кроме явно сломанной незавершённой промежуточной правки — тогда сначала разберись по commit history/issues/CI.

## Проект

Windows 11 x64 GUI-приложение на **C# / .NET 10 (`net10.0-windows`)**, WinForms + tray. Поднимает несколько локальных HTTP/HTTPS forward proxy. Каждый proxy направляет трафик **только через выбранное L2TP**, без DIRECT fallback. Защищаемые домены выбираются внешним PAC/браузером; само приложение домены не маршрутизирует.

## Неподлежащие откату требования

- Всегда GUI. `X` формы скрывает в tray; процесс завершается только явным **«Выйти»**.
- Несколько proxy одновременно; у каждого bind IPv4, port, timeouts, `maxConcurrentConnections`, выбранный L2TP, state, RX/TX; независимые Pause/Resume.
- L2TP — shared/dedicated catalog entities. Shared может иметь несколько active leases; dedicated — одну.
- Running proxy держит L2TP lease. Первая lease dial+verify; последняя release вызывает disconnect / `RasHangUp`.
- Existing Windows profile и CustomEphemeral private `.pbk` без постоянного Windows Settings VPN profile.
- Custom fields: server, user/password/domain/current Windows creds, PSK/cert, PPP auth/encryption/timeouts.
- Password/PSK — только Windows user-bound DPAPI, никогда plaintext.
- Keepalive Off / PPP server internal IPv4 / CustomIPv4; source-bound к конкретному L2TP; threshold => fail-closed teardown + reconnect при оставшихся leases.
- JSONL append-only `<root>/YYYY-MM/YYYY-MM-DD.jsonl`, retention, configurable root; никаких secrets/body/tunnel contents.
- HTTPS через CONNECT без MITM.

## Fail-closed invariants

- Никогда DIRECT.
- Все outbound proxy TCP sockets: `Bind()` к динамическому L2TP IPv4 + `IP_UNICAST_IF` L2TP ifIndex.
- DNS proxy destinations — собственный L2TP-bound resolver, не `System.Net.Dns`.
- Existing profile preflight: L2TP + split tunneling.
- Default IPv4 route guard before/after dial + continuous.
- Lifecycle `Disconnected -> Dialing -> Verifying -> Ready`; context недоступен proxy до Ready.
- Реальный L2TP-bound HTTPS verification. Fixed public IPv4 обязан совпасть; DNS publicAddress пропускает только equality-to-fixed-IP check.
- L2TP loss отменяет зависимые CONNECT/HTTP sessions.
- Proxy runtime обязан полностью drain accepted sessions до освобождения L2TP lease.

## Performance/memory invariants

Fail-closed, latency/throughput и bounded memory равноправны.

- Не добавлять global locks, sync waits, лишние copies/serialization/per-buffer allocations в hot path.
- Не уменьшать buffers ради working set, если растёт syscall rate/latency или падает throughput.
- Никакого production forced GC/working-set trim.
- Любое in-memory state bounded.
- Memory-only change не принимается, если repeatable benchmark ухудшает latency/tail/jitter/throughput больше measurement noise.

## Реализованный baseline — не повторяй без проверки кода

Уже есть WinForms/tray, multi-proxy runtime, shared/dedicated `VpnLeaseManager`, Pause/Resume, Existing profile validation, CustomEphemeral phonebook + DPAPI, RAS client/server IPv4/interface/DNS discovery, source-IP + interface socket binding, route guard, HTTPS verification, L2TP-bound DNS UDP/TCP/CNAME/bounded cache, HTTP proxy + CONNECT, `ArrayPool<byte>` pumps, bounded connection admission, deterministic session drain, traffic/ping metrics, append-only logs, latest L2TP status GUI/backend, process-memory snapshot, deterministic `VpnContext` ownership, selective reconfigure isolation, cancellation reconciliation and long-run weak-reference tests.

Important lifecycle fix history: per-RAS-session monitor CTS/task ownership уже реализован; старый monitor не должен переживать disconnect и не может hangup новый handle.

## Самые новые изменения перед этим handoff

### HTTP framing / request-smuggling — issue #14

Production commit **`f9db53f074d6740296e46452077622099b6f64ff`** — `fix: enforce plain HTTP request framing`.

Plain HTTP теперь:
- строго парсит request framing до outbound connect;
- принимает только один валидный non-negative decimal `Content-Length`;
- rejects duplicate/conflicting/comma-list CL;
- rejects любой `Transfer-Encoding`, включая TE+CL;
- no CL => body length 0;
- initial bytes после header не могут превышать CL;
- client→origin forwarding ограничен ровно CL bytes;
- trailing/pipelined/smuggled bytes после body не отправляются origin;
- early EOF before body completion fails session;
- valid CL сохраняется в origin request;
- CONNECT остаётся opaque tunnel.

Добавлен `ProxyHttpFramingSelfTests.cs`, в том числе loopback smuggling boundary/regression и pre-outbound rejection cases. `CombinedTestRunner` запускает этот suite.

### Timing guard stabilization attempt

Commit **`71a93e5d529225adfd0e1b5125a4302d81c58da5`** — `test: stabilize proxy setup timing guard` — увеличил только benchmark warmup/sample (`2048` warmup, `32768` ops/round), **порог 1.25x не ослаблен**.

Текущий известный exact-head CI для `71a93e5d...`:
- build run **#271 / 32979967766**: **FAILED** на `ProxySetupTimingSelfTests`; build compile succeeded, но text-span parser median = **5859 ns/op** vs immediate-predecessor **3350 ns/op**, ratio **1.75x**, limit 1.25x.
- предыдущий run #270 на `f9db53f...` тоже failed в том же timing gate: 5322 vs 3206 ns/op = 1.66x.
- новый `ProxyHttpFramingSelfTests` из-за порядка runner ещё не успевает выполниться, потому что timing suite падает раньше.
- handoff run **#83 / 32979967788** для `71a93e5d...`: **SUCCESS**, archive создан.

**Не называй текущий code head green.** Первая задача нового чата — выяснить, является ли 1.66–1.75x реальной регрессией production parser после framing bookkeeping или benchmark сравнивает несопоставимые predecessor/current paths. Порог не ослаблять просто ради CI. Нужен source-level разбор `ProxySetupTimingSelfTests` + `ParsedProxyRequest.Parse`, затем минимальный корректный fix и exact-head Windows CI до выполнения framing suite.

### Следующий lifecycle bug — issue #15

Создана open issue **#15 `Make proxy startup ownership transactional and drain-safe`**.

Подтверждённый audit finding: `ProxyInstanceRuntime.StartAsync` после создания `runTask` присваивает `_lease`, `_runCancellation`, `_runTask`, затем ждёт `WaitUntilListeningAsync`. Если wait throws/cancel, catch отменяет/Dispose локальные ресурсы, но может не очистить уже назначенные fields и не await/drain exact `runTask`. Это допускает stale disposed ownership и release L2TP lease до полного listener/session drain.

Acceptance #15:
- start attempt = единая generation/ownership transaction;
- на fail/cancel после создания runTask: cancel run CTS -> await exact runTask drain -> clear fields той же generation -> dispose CTS -> release exact lease once;
- caller cancellation остаётся cancellation;
- retry безопасен;
- bind/start failure retryable;
- Pause/Dispose idempotent без double release;
- observer tasks не теряются;
- successful Running lifecycle не меняется.

План testability seam: небольшой internal orchestration seam в `ProxyInstanceRuntime` (injectable lease acquisition + server-lifetime abstraction с `RunAsync`/`WaitUntilListeningAsync`), при этом production constructor продолжает текущую цепочку `VpnLeaseManager -> L2tpDnsResolver -> L2tpSocketFactory -> ProxyServer`; network/data path не менять.

## Актуальная issue-карта на момент handoff

Open: **#2, #4, #5, #6, #7, #11, #13, #14, #15**.
Closed: **#1, #3, #8, #9, #10, #12**.

Перепроверь live GitHub. #2 — обязательная реальная Windows 11 + L2TP E2E; #4/#5/#6/#7 требуют остаточной real-environment acceptance; #11 performance/memory ongoing; #13 long-run stability ongoing; #14 сейчас блокируется timing verdict; #15 следующий lifecycle implementation block.

## Недавние важные regression/hardening результаты

- canonical listener endpoint validation использует parsed `IPAddress`, поэтому эквивалентные IPv4 spellings (`127.1` == `127.0.0.1`) не обходят collision guard;
- selective reconfigure сохраняет exact object identity независимой группы;
- 250 repeated selective reconfigure cycles: retained replaced `ProxyInstanceRuntime` = 0/250, `VpnLeaseManager` = 0/250 в Windows CI test;
- incremental CRLFCRLF header scan не пересканирует весь prefix при fragmented reads;
- runtime start/reconfigure cancellation reconciliation regression есть и проходит до текущего timing gate;
- shutdown drain regression гарантирует `ProxyServer.RunAsync` не возвращается до cleanup accepted sessions.

## Правила работы

- Общайся по-русски, технически и прямо.
- Не задавай повторно вопросы, ответы на которые есть в GitHub/handoff/requirements.
- Делай best-effort решения и реализуй сразу.
- При длительной работе давай короткие progress updates.
- Изменения обычно пишутся прямо в `main`, если пользователь не изменит workflow.
- Новые цели/находки фиксируй в GitHub issues/docs до или вместе с кодом.
- После значимых изменений проверяй exact-head `build` и `handoff`; green можно утверждать только по exact current head.
- Не откатывай `.NET 10`, GUI/tray, multi-proxy, custom ephemeral, no-DIRECT, custom DNS, route guard, deterministic ownership и performance/memory invariants.

## Начни работу так

1. Синхронизируй live `main`, Actions, issues/comments и handoff docs.
2. Кратко сообщи current SHA, exact-head CI и расхождения с snapshot.
3. **Сначала разберись и исправь текущий `ProxySetupTimingSelfTests` / parser regression без ослабления 1.25x policy**, добейся прохождения suite до `ProxyHttpFramingSelfTests` и проверь framing tests.
4. Если #14 проходит semantic + performance gates, обнови/закрой #14.
5. Затем реализуй #15 transactional startup ownership с детерминированными tests и exact-head CI.
6. После этого продолжай крупными блоками по #11/#13 и real-environment acceptance roadmap, не ограничиваясь одной мелкой задачей.

---
