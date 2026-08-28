# New Chat Startup Prompt — ProxyToAnyConnect

Скопируй весь текст после разделителя первым сообщением в новый чат.

---

Продолжаем разработку публичного GitHub-репозитория **`lukindv77/ProxyToAnyConnect`** после длинной предыдущей сессии. Не начинай проект заново. **Live GitHub — главный source of truth.** Сначала синхронизируй exact current `main`, Actions и live issues/comments, затем продолжай код.

## Обязательная синхронизация

1. Получи exact current `main` SHA и tree SHA.
2. Прочитай на current `main` минимум:
   - `docs/handoff/NEW_CHAT_PROMPT.md`
   - `docs/handoff/SESSION_2026-08-28.md`
   - `docs/handoff/CURRENT_STATE.md`
   - `docs/handoff/AUDIT_SNAPSHOT.md`
   - `docs/handoff/ACTIVE_DEVELOPMENT.md`
   - `docs/handoff/FINAL_CI_STATUS.md`
   - `docs/handoff/ISSUES_SNAPSHOT.md`
   - `docs/handoff/CHAT_TRANSFER_CHECKPOINT.md`
   - `docs/requirements.md`
   - `docs/architecture.md`
   - `docs/memory-stability.md`
   - `docs/windows-integration-test.md`
   - `docs/windows-integration-evidence.md`
   - `docs/windows-soak-evidence.md`
   - `.github/workflows/build.yml`
   - `.github/workflows/handoff.yml`
3. Получи live issues и последние comments минимум для **#2, #4, #5, #6, #7, #11, #13**. #45/#47/#49/#50 закрыты completed; #14/#15 также закрыты. Не churn их без нового concrete finding.
4. Проверь `build` и `handoff` именно для exact current head. Не называй head green по старому SHA.
5. Если prompt/docs расходятся с live GitHub, приоритет у live GitHub.

## Последний принятый production code checkpoint перед handoff-doc commit

Production `main`: **`ddbdc95e3b9e7080a31c2b631da1c1f187a1f1a3`**, tree **`4f11a13a1ac0d1839b86671dc0b7ccae7eed0d40`**.

Exact-main CI на этом SHA:
- build #580 / run **`33132200561`** — success целиком: evidence smokes, restore/build, aggregate self-tests, self-contained win-x64 publish, binary integrity manifest, ZIP, artifact upload;
- Windows artifact id `9670700014`, digest `sha256:83e91fbda614aeb804fcdecfc05bf589247582143c609a453be76f5e92acd76e`;
- handoff #375 / run **`33132200498`** — success;
- handoff artifact id `9670678196`, digest `sha256:6091de65429ce10c5275a7a7ba27739b0d49cc4b635f182a27ea8f72cbb812d5`.

Handoff-doc commit после этого snapshot намеренно двигает `main`, поэтому первым действием нового чата снова перепроверь live exact head и его Actions.

## Что принято в последней сессии

- **#45**: inbound HTTP request-line строго `method SP request-target SP HTTP-version` с ровно двумя ASCII SP; ambiguous/repeated/alternate whitespace reject до outbound ownership. Closed completed.
- **#47**: soak `observedDurationSeconds` producer/validator согласованы по first/last serialized sample timestamps; существующая 50 ms consistency tolerance не расширена; tamper/mismatch fail-closed. Closed completed.
- **#6 audit**: current production уже содержит canonical/reparse-safe/exact-leaf/non-recursive CustomEphemeral orphan cleanup и Windows regressions; managed RAS password/PSK carriers очищаются сразу после native handoff. Нового duplicate patch не делали. #6 остаётся open на real Windows/L2TP acceptance и новые concrete findings.
- **#49**: verification `probePath` теперь byte-exact ASCII HTTP origin-form; строгие `%HH`; fragment, controls, SP/HTAB, non-ASCII и malformed/lossy forms reject; builder повторно валидирует fail-closed перед wire encoding. Closed completed.
- **#50**: verification host canonicalized to IDNA/A-label, затем explicit strict ASCII DNS LDH-label grammar; одна canonical authority используется для L2TP DNS, TLS SNI/TargetHost и HTTP Host; `münich.example` → `xn--mnich-kva.example`; `_` и malformed labels reject. Closed completed.

## #49/#50 acceptance lineage — только для аудита

- dev branch `dev/issue49-probe-target`;
- dev validation run `33130832271` green;
- bot-published validated source commit `1684718295944ecdb28216ae02c32365ff7b2b0c`;
- clean acceptance commit `c67a29a0c82a5eb6f5bdee4e20ece39c426ac652`, ровно четыре production/test файла;
- clean permanent PR #51;
- PR build #579 / run `33131957422`: attempt 1 дал только уже известный hosted-runner-sensitive DNS setup timing 1.30x при неизменном limit 1.25x; никакой threshold/code weakening не делали; identical-head attempt 2 на другом Windows runner прошёл build + aggregate self-tests;
- rebase merge → `ddbdc95e3b9e7080a31c2b631da1c1f187a1f1a3`;
- exact-main build #580 + handoff #375 green;
- #49/#50 закрыты completed с concrete SHA/run/artifact evidence.

**Не merge `dev/issue49-probe-target` wholesale.** Это историческая validation/transport lineage, production уже принят clean PR #51.

## Product/architecture invariants — не ослаблять

Windows 11 x64, C#/.NET 10 WinForms+tray, multiple local HTTP/HTTPS forward proxies. Каждый proxy связан с выбранным L2TP и **никогда не имеет DIRECT fallback**.

Сохранять:
- GUI/tray lifecycle, explicit Exit only;
- multiple independent proxies, Pause/Resume, bounded concurrency and exact session drain;
- shared/dedicated L2TP lease semantics;
- ExistingWindowsProfile + private CustomEphemeral PBK;
- DPAPI-protected password/PSK, no plaintext persistence/logging;
- outbound TCP source `Bind()` + `IP_UNICAST_IF` selected L2TP interface;
- proxied DNS only custom L2TP-bound resolver;
- split-tunnel/default-route guards;
- `Disconnected -> Dialing -> Verifying -> Ready`, no usable context before verification;
- verification via real L2TP-bound HTTPS;
- L2TP loss cancels dependents fail-closed;
- no TLS MITM for CONNECT;
- pooled 32 KiB data path, bounded memory and low latency as first-class requirements;
- no production forced GC;
- memory optimization must not regress proxy latency, jitter or sustained throughput;
- `ProxyServer.RunAsync` drains accepted sessions before higher ownership releases VPN lease.

## Real release boundaries — не подменять hosted smoke

Open external/evidence-critical work remains:
- #2 — real Windows 11 + real L2TP E2E;
- #4 — real shared/dedicated multi-proxy lease behavior;
- #5 — real operator GUI/profile/selective live behavior;
- #6 — real CustomEphemeral auth/PSK/cert/cleanup;
- #7 — real keepalive failure → invalidation → hangup → cooldown → reconnect;
- #11 — permanent performance/memory requirement;
- #13 — representative 12–24 h exact-binary soak with traffic/reconnect/Pause/Resume/reconfigure and correlated managed/native resource series.

Hosted Actions smoke — tooling mechanics only. Не выдумывай real L2TP или 12–24 h soak evidence.

## Порядок продолжения

1. Live sync exact `main`, tree и exact-head `build`/`handoff`.
2. Прочитай handoff docs, requirements/architecture/memory/evidence docs и live issue comments.
3. Продолжай **широким связанным deterministic audit/development block**, а не одной косметической задачей.
4. Не churn уже доказанные #45/#47/#49/#50 и audited #6 boundaries без нового concrete finding.
5. Для новых findings: issue-first с acceptance → code/tests → permanent Windows CI → merge/rebase → exact-main build + handoff.
6. Зафиксируй concrete SHA/run IDs в issue и handoff docs после каждого принятого engineering block.
7. Реальные #2/#4/#5/#6/#7/#13 не закрывай без настоящего внешнего evidence.

Общайся с пользователем по-русски. Не задавай вопросы, ответы на которые уже есть в live GitHub/requirements. Начни сразу с синхронизации GitHub и продолжай разработку от фактического current state.

---
