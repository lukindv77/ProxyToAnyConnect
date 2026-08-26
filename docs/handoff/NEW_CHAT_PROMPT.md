# New Chat Startup Prompt — ProxyToAnyConnect

Скопируй весь текст ниже первым сообщением в новый чат.

---

Продолжаем разработку private GitHub-проекта **`lukindv77/ProxyToAnyConnect`** после длинного предыдущего чата. Не начинай проект заново и не переосмысливай уже зафиксированные требования. **Live GitHub `main` — главный source of truth.**

## Обязательная синхронизация перед кодом

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
4. Проверь `build` и `handoff` Actions **для exact current head**. Не называй код green по старому head.
5. Перед архитектурными изменениями просмотри актуальные `Runtime`, `Proxy`, `Network`, `Vpn`, `Configuration`, `Gui` и self-tests.
6. Если prompt расходится с live GitHub, приоритет у live GitHub.

## Проект и неизменяемые требования

Windows 11 x64 GUI-приложение C# / **.NET 10 `net10.0-windows`**, WinForms + system tray. Оно поднимает несколько локальных HTTP/HTTPS forward proxy; каждый proxy отправляет трафик **только через выбранное L2TP**, без DIRECT fallback. Домены выбираются внешним PAC/браузером.

Нельзя откатывать:

- всегда GUI; `X` скрывает в tray, процесс завершает только явный «Выйти»;
- несколько independent proxies; bind IPv4, port, timeouts, `maxConcurrentConnections`, selected L2TP, state, RX/TX, independent Pause/Resume;
- shared/dedicated L2TP leases; first lease dial+verify, last release disconnect/RasHangUp;
- ExistingWindowsProfile и CustomEphemeral private `.pbk`, без persistent Windows Settings VPN profile;
- password/PSK only Windows user-bound DPAPI, never plaintext;
- keepalive Off / PPP server internal IPv4 / CustomIPv4; threshold => fail-closed teardown + reconnect while leases remain;
- append-only JSONL `<root>/YYYY-MM/YYYY-MM-DD.jsonl`, retention, no secrets/body/tunnel contents;
- HTTPS through CONNECT only, no MITM.

### Fail-closed

- никогда DIRECT;
- outbound proxy TCP socket всегда `Bind()` к динамическому L2TP IPv4 + `IP_UNICAST_IF` L2TP interface index;
- proxied DNS — собственный L2TP-bound resolver, не `System.Net.Dns`;
- existing profile preflight L2TP + split tunneling;
- IPv4 default-route guard before/after dial + continuous;
- `Disconnected -> Dialing -> Verifying -> Ready`; context не публикуется до Ready;
- real L2TP-bound HTTPS verification; fixed expected IPv4 должен совпасть;
- L2TP loss cancels dependent HTTP/CONNECT sessions;
- `ProxyServer.RunAsync` обязан drain accepted sessions before higher runtime releases L2TP lease.

### Performance / memory

Fail-closed, latency/throughput и bounded whole-process memory равноправны. Не добавлять global locks/sync waits/extra copies/serialization/per-buffer allocations в hot path; не уменьшать transfer buffers ради working set, если растёт syscall rate/latency; production forced GC запрещён; state bounded. Memory-only regression latency/jitter/throughput beyond noise не принимается.

## Что уже реализовано

Уже есть WinForms/tray, multi-proxy runtime, shared/dedicated `VpnLeaseManager`, Pause/Resume, existing profile validation, CustomEphemeral phonebook + DPAPI, RAS IPv4/interface/DNS discovery, source+interface socket binding, route guards, HTTPS verification, L2TP-bound DNS UDP/TCP/CNAME/bounded cache, HTTP proxy + CONNECT, pooled transfer buffers, bounded session admission, shutdown drain, traffic/ping metrics, append-only logs, latest L2TP status GUI/backend, process-memory snapshot, deterministic `VpnContext` ownership, per-RAS-session monitor CTS/task ownership, selective reconfigure isolation, cancellation reconciliation and long-run collectability tests.

Не регрессируй per-RAS-session monitor ownership: старый monitor после disconnect не должен переживать сессию и не может hangup replacement handle.

## Последние изменения и точный blocker

### Issue #14 — HTTP request framing / request-smuggling

Production commit **`f9db53f074d6740296e46452077622099b6f64ff`** — `fix: enforce plain HTTP request framing`.

Plain HTTP теперь:
- parses framing before outbound connect;
- accepts only one valid non-negative decimal `Content-Length`;
- rejects duplicate/conflicting/comma-list CL;
- rejects any `Transfer-Encoding`, including TE+CL;
- no CL => zero body;
- initial bytes after header cannot exceed CL;
- forwards exactly CL body bytes, never later pipelined/smuggled bytes;
- fails early EOF;
- preserves valid CL in origin request;
- CONNECT remains opaque/unchanged.

Added `ProxyHttpFramingSelfTests.cs` and runner integration.

### Setup timing gate is no longer the current blocker

`ProxySetupTimingSelfTests` current source uses paired alternating measurement with warmup 2048, 9 rounds, 32768 ops/round and unchanged 1.25x limit. On exact handoff-head build #272 it **passed**:

- parser `1999 vs 2042 ns/op = 0.98x`;
- origin `973 vs 1222 ns/op = 0.80x`.

Older builds #270/#271 had noisy failures before this later exact run; do not waste the next chat re-solving that unless live CI regresses again.

### Current real blocker from exact Windows build #272

Handoff-doc head **`b3fbe1f96c0ffa7d031cb72b81793ec6ea9c2858`** compiled successfully and reached the new framing suite. `ProxyHttpFramingSelfTests.ExactContentLengthBoundsClientToOriginBytesAsync` failed while `ReadToEndAsync` read the proxy client response:

- `IOException: Unable to read data from the transport connection`
- inner `SocketException (10054): existing connection was forcibly closed by remote host`
- failure at `ProxyHttpFramingSelfTests.cs` line ~330, called from exact-CL boundary test line ~159.

This is now the **first task**. Investigate whether:

1. production proxy closes the client with Windows RST because intentionally unconsumed trailing/smuggled client bytes remain after exactly `Content-Length` bytes are forwarded; or
2. the proxy is aborting before a complete origin response; or
3. the test incorrectly requires clean EOF when reset-after-complete-response is acceptable for this deliberate smuggling case.

Do not weaken the semantic invariant: bytes after CL must never reach origin. Prefer a deterministic test that reads/verifies the expected HTTP response framing/body rather than assuming clean EOF if Windows RST is merely a consequence of closing with unread malicious trailing bytes. But verify actual production behavior before changing the test.

Issue #14 remains open until exact-head Windows tests prove framing semantics and existing performance/fail-closed gates.

## Следующий confirmed lifecycle bug — issue #15

`ProxyInstanceRuntime.StartAsync` can acquire lease, create/run server, publish `_lease/_runCancellation/_runTask`, then fail/cancel in `WaitUntilListeningAsync`. Current catch can cleanup local resources without first awaiting exact run-task drain or clearing already-published ownership fields. Risk: stale disposed fields and release last L2TP lease before listener/session drain.

Required transactional failure order:

`cancel exact run CTS -> await exact runTask drain -> clear fields only if same attempt/generation -> dispose CTS -> release exact L2TP lease once`.

Preserve caller cancellation, safe retry, Pause/Dispose idempotence, no double release/unobserved observers and unchanged successful Running lifecycle.

Planned testability seam: orchestration-only injectable lease acquisition + server lifetime (`RunAsync` / `WaitUntilListeningAsync`). Production chain remains `VpnLeaseManager -> L2tpDnsResolver -> L2tpSocketFactory -> ProxyServer`; no network hot-path semantic change.

## Issue snapshot at handoff

Open: **#2, #4, #5, #6, #7, #11, #13, #14, #15**.
Closed: **#1, #3, #8, #9, #10, #12**.

#2 remains required real Windows 11 + real L2TP E2E. #4/#5/#6/#7 need remaining real-environment acceptance. #11/#13 are ongoing architecture/performance/memory/lifetime hardening.

## Recent important regressions/results

- canonical listener validation parses IP addresses; `127.1` and `127.0.0.1` cannot bypass bind collision guard;
- selective reconfigure keeps exact object identity of independent groups;
- 250 cycles: retained replaced `ProxyInstanceRuntime` 0/250 and `VpnLeaseManager` 0/250 in recorded Windows self-test;
- incremental CRLFCRLF scan avoids repeated whole-prefix scan on fragmented headers;
- runtime start/reconfigure cancellation reconciliation passes;
- shutdown drain passes before lease release;
- exact build #272 reached framing tests after setup timing guard passed.

## GitHub handoff archive

Latest handoff workflow for the docs snapshot creates an Actions artifact `ProxyToAnyConnect-handoff-<sha>` containing exact `src`, `tests`, `docs`, `.github`, README, solution and `HANDOFF_BUILD_INFO.txt`. Use `docs/handoff/NEW_CHAT_PROMPT.md` from the latest artifact/live main.

## Правила нового чата

- Общайся по-русски, технически и прямо.
- Не задавай повторно вопросы, ответы на которые уже есть в GitHub/requirements/handoff.
- Фиксируй новые findings/goals в GitHub issues/docs вместе с разработкой.
- Обычно commit directly to `main`, если пользователь не поменял workflow.
- После значимых изменений проверяй exact-head build + handoff.
- Не утверждай green без exact current-head success.

## Начни сразу

1. Синхронизируй live head/actions/issues/docs.
2. Подтверди exact текущий SHA и CI.
3. **Первым делом разберись с build #272 framing failure / SocketException 10054 в `ExactContentLengthBoundsClientToOriginBytesAsync`, сохранив строгую CL smuggling boundary.**
4. Добейся exact-head прохождения `ProxyHttpFramingSelfTests`; затем обнови/закрой #14 если acceptance выполнен.
5. Реализуй #15 transactional startup ownership с детерминированными cancel/fail/drain/retry/single-release tests.
6. Продолжай крупными блоками #11/#13 и real Windows acceptance, а не одной мелкой задачей.

---
