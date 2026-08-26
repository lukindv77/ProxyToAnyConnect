# New Chat Startup Prompt — ProxyToAnyConnect

Copy the text below as the first message in a new ChatGPT conversation.

---

Продолжаем разработку private GitHub-проекта **`lukindv77/ProxyToAnyConnect`**. Это перенос из предыдущего длинного чата. Не начинай проект заново и не переосмысливай уже зафиксированные требования без причины.

## Сначала синхронизируй контекст из GitHub

Используй подключённый GitHub и **до любых изменений**:

1. Прочитай на актуальном `main`:
   - `docs/handoff/CURRENT_STATE.md`
   - `docs/requirements.md` — основной source of truth требований
   - `docs/architecture.md`
   - `docs/memory-stability.md`
   - `docs/windows-integration-test.md`
   - `README.md`
   - `.github/workflows/build.yml`
   - `.github/workflows/handoff.yml` (если уже присутствует)
2. Получи latest commit `main` и не предполагай, что SHA из handoff всё ещё последний.
3. Получи актуальные GitHub Issues, особенно #2, #4–#7, #9–#13, и их комментарии/acceptance criteria.
4. Проверь GitHub Actions для **текущего head**, а не старого commit. Не утверждай, что CI зелёный, пока это не подтверждено для актуального head.
5. Просмотри текущую структуру `src/ProxyToAnyConnect` и `tests/ProxyToAnyConnect.SelfTests`, прежде чем писать код.
6. Если GitHub расходится с этим prompt/handoff, **актуальный GitHub имеет приоритет**, кроме случаев, когда это очевидная незавершённая/сломанная промежуточная правка — тогда сначала разберись по commit history/issues/CI.

## Что это за проект

Windows 11 x64 GUI-приложение на **C# / .NET 10 (`net10.0-windows`)**, WinForms + system tray. Оно поднимает один или несколько локальных HTTP/HTTPS forward proxy и направляет трафик каждого proxy **исключительно через выбранное L2TP**.

Защищаемые домены выбираются снаружи, например Chrome PAC. Сам ProxyToAnyConnect не выбирает домены и **не имеет DIRECT fallback**.

## Жёсткие требования, которые уже согласованы

- .NET **10**, не .NET 8.
- Приложение всегда GUI, может скрываться в tray.
- `X` главной формы не завершает процесс, а скрывает его в tray.
- Завершение только через явное **«Выйти»** в меню приложения или tray.
- Несколько независимых proxy-настроек одновременно.
- У каждого proxy свой bind IPv4, порт, timeouts, `maxConcurrentConnections`, выбранный L2TP, Running/Paused/Error, RX/TX.
- Каждый proxy можно Pause/Resume независимо.
- L2TP — отдельные сущности каталога: shared или dedicated.
- Shared L2TP может использоваться несколькими активными proxy.
- Dedicated — одним proxy.
- Running proxy держит lease выбранного L2TP.
- Первая lease поднимает/проверяет VPN, последняя освобождённая lease вызывает `RasHangUp`.
- Если proxy поставлен на паузу и других активных proxy на его L2TP нет — L2TP отключается.
- Два режима L2TP:
  1. existing Windows L2TP profile;
  2. custom ephemeral L2TP через private temporary `.pbk`, **без постоянного VPN-профиля в Windows Settings**.
- Custom L2TP настраивает server, user/password/domain/current credentials, IPsec PSK/certificate, PPP auth/encryption, timeouts.
- Password/PSK не хранятся plaintext; используется Windows user-bound DPAPI.
- Keepalive L2TP: Off / PPP server internal IPv4 / arbitrary CustomIPv4, с interval, timeout, consecutive-failure threshold.
- Keepalive идёт с source IPv4 конкретного L2TP; при threshold — fail-closed teardown + reconnect, если есть активные leases.
- GUI показывает proxy RX/TX и L2TP aggregate RX/TX + average successful keepalive ping за последние 5 минут.
- Логи JSONL append-only:
  - настраиваемая log root folder, default папка приложения;
  - retention days;
  - `<root>/YYYY-MM/YYYY-MM-DD.jsonl`;
  - новая строка только append, файл целиком не читается/не переписывается;
  - password/PSK/body/tunnel contents не логируются.

## Fail-closed / routing — нельзя ослаблять

- Прокси-трафик никогда не должен уйти DIRECT.
- Все outbound TCP proxy sockets используют:
  - `Bind()` к динамически выданному L2TP IPv4;
  - `IP_UNICAST_IF` = L2TP interface index.
- DNS проксируемых destination выполняется своим L2TP-bound resolver, не `System.Net.Dns`.
- Установка L2TP не должна менять default Internet route других приложений.
- Existing Windows profile до `RasDial` проверяется на L2TP + split tunneling.
- Default IPv4 routes снимаются до/после dial и непрерывно контролируются.
- VPN lifecycle: `Disconnected -> Dialing -> Verifying -> Ready`.
- До `Ready` proxy не получает VPN context.
- Verification выполняет реальный L2TP-bound HTTPS probe.
- Если configured public address — IPv4 literal, observed public IP обязан совпасть.
- Если configured public address — DNS name, пропускаются только проверки, которые требуют заранее фиксированный expected IPv4; остальные проверки обязательны.
- При L2TP loss активные зависимые CONNECT/HTTP sessions отменяются и закрываются.
- HTTPS — обычный CONNECT, без MITM.

## Performance + memory — также жёсткое требование

Скорость data path и стабильность памяти равноправны с fail-closed.

- Минимизировать added latency и jitter.
- Сохранять высокий sustained throughput.
- Оптимизировать память **всего процесса**, не только одного соединения.
- Не допускается memory-only оптимизация, если repeatable before/after тест показывает увеличение proxy latency/tail latency/jitter или снижение throughput выше measurement noise.
- Не добавлять global locks/sync waits/лишние data copies/serialization/per-buffer allocations в byte-transfer hot path ради памяти.
- Не уменьшать transfer buffers просто ради working set, если растёт syscall frequency или падает throughput.
- Никакого forced GC/working-set trimming в production.
- In-memory state должен быть bounded; никаких unbounded history/task/cache registries.
- Если есть конфликт «минимальный footprint» против «bounded predictable memory + faster data path», выбирай второй вариант.

## Что уже реализовано — не делай повторно без проверки текущего кода

По handoff-состоянию в проекте уже есть:

- WinForms + tray lifecycle.
- Multi-proxy runtime и shared/dedicated VPN lease manager.
- Pause/Resume и disconnect L2TP при последней lease.
- Existing Windows L2TP profile enumeration/validation.
- Custom ephemeral private RAS phonebook + DPAPI secrets; native Windows CI smoke-test создания L2TP `.pbk` + PSK + cleanup.
- Custom ephemeral mode уже подключён к `RasConnectionManager` common dial/verify path; реальный внешний endpoint ещё надо тестировать.
- RAS PPP IPv4/interface/DNS discovery.
- Source-IP binding + `IP_UNICAST_IF`.
- Split-tunnel guard и native default-route snapshot/continuous guard.
- Active HTTPS connectivity verification.
- Custom L2TP-bound DNS: UDP, TCP fallback, CNAME, bounded TTL cache per L2TP, shared между proxy на одном shared VPN.
- HTTP forward proxy + bidirectional CONNECT.
- `ArrayPool<byte>` transfer buffers, no full tunnel buffering.
- `maxConcurrentConnections` per proxy и accept-backpressure.
- Deterministic proxy shutdown drain до освобождения VPN lease.
- Runtime RX/TX metrics and rolling ping.
- Append-only daily logs + retention.
- Process memory-health snapshot and logging.
- `VpnContext` ref-count lifetime; CTS dispose после последнего consumer.
- Bounded latest-L2TP-status registry.
- Proxy runtime completion observer tracked/joined при Pause/reconfigure/Exit.
- Per-session RAS monitor CTS/task, join при disconnect/reconnect; stale old monitor не может hangup новый RAS handle.
- Windows self-tests и self-contained win-x64 publish artifact.

## Последний известный проверенный baseline до handoff-doc commits

Commit `5c3955fce4896c0a02b78c021eaccd8078ada8f4` — `fix: own RAS monitor lifetime per VPN session`.
GitHub Actions run #181 был полностью успешен: Build, Self-tests, Publish, ZIP, Upload artifact.

После него добавлялись docs/handoff/workflow commits. Поэтому **обязательно проверь CI текущего head** и используй текущий head как baseline.

## Известные audit gaps / следующий приоритет

Главный незакрытый риск — не компиляция, а реальная Windows 11 + настоящий L2TP integration validation.

Проверь актуальные issues, но ожидай такие направления:

1. #2 — реальный Windows 11 E2E с существующим и custom ephemeral L2TP.
2. #6/#7 — реальный custom L2TP + keepalive/reconnect validation.
3. #12 — убедиться, что bounded latest L2TP status уже выведен в GUI; backend registry реализован, GUI может быть ещё не доведён.
4. #13 — продолжать long-run memory/resource audit: repeated Pause/Resume/reconnect/selective reconfigure, без monotonic retained graph/handles.
5. Проверять selective reload: изменение proxy перезапускает только его; изменение shared L2TP — только зависимую группу; остальные продолжают работать.
6. Performance/memory changes должны иметь repeatable regression coverage и не ухудшать data path.
7. Обновлять `docs/windows-integration-test.md` результатами реального теста.

## Правила работы в новом чате

- Общайся со мной по-русски, технически и прямо.
- Не задавай повторно вопросы, ответы на которые уже есть в requirements/handoff/GitHub.
- Делай best-effort архитектурные решения и сразу реализуй их.
- При длительной работе давай короткие промежуточные обновления.
- Пиши изменения непосредственно в GitHub `main`, как делалось ранее, если я не изменю workflow.
- После значимых изменений проверяй CI и не называй код рабочим/зелёным без фактической проверки актуального head.
- Фиксируй новые постоянные требования в `docs/requirements.md`, архитектурные решения/аудит — в соответствующих docs/issues.
- Не откатывай уже принятые требования (нет DIRECT, .NET 10, GUI/tray, multi-proxy, custom ephemeral и т.д.).

### Начни сейчас

Синхронизируйся с GitHub по шагам выше, кратко сообщи:

1. актуальный head SHA;
2. статус последнего CI для него;
3. какие roadmap issues сейчас open/closed;
4. есть ли расхождения между handoff и текущим кодом;
5. какой следующий конкретный кодовый шаг ты выбираешь.

После этого **сразу продолжай разработку**, не ожидая дополнительного подтверждения, если GitHub не обнаружил критическое противоречие.

---
