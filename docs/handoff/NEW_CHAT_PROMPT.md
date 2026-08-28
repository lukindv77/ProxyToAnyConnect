# New Chat Startup Prompt — ProxyToAnyConnect

Скопируй весь текст после разделителя первым сообщением в новый чат.

---

Продолжаем разработку публичного GitHub-репозитория **`lukindv77/ProxyToAnyConnect`** после длинной предыдущей сессии. Не начинай проект заново. **Live GitHub — главный source of truth.** Сначала синхронизируй exact current `main`, Actions, issues/comments и только потом продолжай код.

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
3. Получи live issues и последние comments для **#2, #4, #5, #6, #7, #11, #13, #49, #50**. #45 и #47 закрыты completed; #14/#15 также закрыты и не требуют churn без нового finding.
4. Проверь `build` и `handoff` именно для exact current head. Не называй head green по старому SHA.
5. Проверь branch **`dev/issue49-probe-target`**, её latest SHA и Actions run lineage. Это незавершённая работа #49/#50, а не production baseline.
6. Если prompt/docs расходятся с live GitHub, приоритет у live GitHub.

## Последний принятый production checkpoint перед handoff-doc commit

Production `main` был **`2e56f8f76efda9047ec83f3cd0e58aee395de322`** после clean PR #48.

Exact-main CI на этом SHA:
- build #577 / run **`33097542082`** — success целиком: evidence smokes, restore/build, aggregate self-tests, self-contained win-x64 publish, binary integrity manifest, ZIP, artifact upload;
- handoff #373 / run **`33097542206`** — success;
- exact handoff artifact id `9657003054`, digest `sha256:a7fcf633740e12b2fa2dcde388567b7038ea48b4686a725986e0c517c40394f0`.

Handoff commit с этим prompt/archive намеренно двигает `main`, поэтому **первым действием перепроверь новый live head и его exact-head Actions**.

## Что принято в последней сессии

- **#45**: inbound HTTP request-line теперь строго `method SP request-target SP HTTP-version` с ровно двумя ASCII SP; ambiguous/repeated/alternate whitespace reject до outbound ownership. #45 closed completed.
- **#47**: soak `observedDurationSeconds` producer/validator согласованы по first/last **serialized sample timestamps**; существующая 50 ms consistency tolerance не расширена; tamper/mismatch остаётся fail-closed. #47 closed completed.
- **#6 audit**: current main уже содержит canonical/reparse-safe/exact-leaf/non-recursive CustomEphemeral orphan cleanup и соответствующие Windows regressions. Managed RAS password/PSK carriers также очищаются сразу после native handoff. Нового duplicate patch не делали. #6 остаётся open только на real Windows/L2TP acceptance и новые конкретные findings.

## Незавершённый новый audit/development block: #49 + #50

### #49
`verification.probePath` в production сейчас слишком lenient: только leading `/`, затем ASCII wire builder. Control/space/CRLF могут менять framing, non-ASCII молча превращается в `?`. Issue #49 требует byte-exact ASCII origin-form, корректных `%HH`, запрета fragment/controls/non-ASCII/raw ambiguous characters и builder-level fail-closed validation.

### #50
Windows/.NET 10 diagnostic доказал, что `Uri.CheckHostName("münich.example") == Dns`. Production DNS/TLS получают Unicode host, а HTTP Host через ASCII encoder получает `m?nich.example`, поэтому authority может расходиться. Issue #50 требует единой IDNA/A-label canonicalization для L2TP DNS + TLS SNI/TargetHost + HTTP Host. Дополнительный Windows aggregate показал, что `Uri.CheckHostName` также принимает `bad_.example`, поэтому security boundary должен использовать явную strict DNS-label grammar, а не только platform classifier.

### Где лежит работа

Branch: **`dev/issue49-probe-target`**, base production SHA `2e56f8f76efda9047ec83f3cd0e58aee395de322`.

Ключевые validation assets:
- `.github/validation/issue49-transform.ps1` blob `8986e4461c3a2098be6a4519b1b42e9ad124c7d5`;
- `.github/validation/issue49-post-transform.ps1` blob `ef2db77e477b49cde12f63ad51f0d5a2c19d663f`;
- workflow setup commit before final run: `545351d2cb7871f3b903b6242c82d494a0cde17d`;
- validation run7 `33130832271`: **SUCCESS**;
- bot-published validated source commit: **`1684718295944ecdb28216ae02c32365ff7b2b0c`**.

История failures важна, не теряй её:
- run1 `33098221153`: transport failed before compile, но подтвердил Unicode DNS classification = Dns;
- run2 `33098523188`: patch hunk transport failure;
- run3 `33098768040`: transform passed, compile caught missing namespace import in new test;
- run4 `33130656615`: compile + aggregate ran; only new suites failed because `bad_.example` escaped `Uri.CheckHostName`, что подтвердило необходимость explicit LDH label grammar;
- run5/run6: YAML parse-only failures при переносе strict-label transform; не считать semantic regression;
- run7 `33130832271`: exact transforms + full aggregate + source publish all success.

Validated source commit `1684718295944ecdb28216ae02c32365ff7b2b0c` меняет ровно четыре production/test файла:
- `src/ProxyToAnyConnect/Configuration/AppOptions.cs`
- `src/ProxyToAnyConnect/Vpn/VpnConnectivityVerifier.cs`
- `tests/ProxyToAnyConnect.SelfTests/SettingsValidationSelfTests.cs`
- `tests/ProxyToAnyConnect.SelfTests/VerificationProbeRequestSelfTests.cs`

**Не merge dev workflow/validation transport в main.** Сначала reconstruct clean acceptance commit только из этих четырёх файлов от current `main`, затем clean PR → permanent Windows CI → merge/rebase → exact-main build + handoff. Только после этого закрывать #49/#50.

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
- no production forced GC and no memory optimization that regresses proxy latency/throughput;
- `ProxyServer.RunAsync` drains accepted sessions before higher ownership releases VPN lease.

## Real release boundaries — не подменять hosted smoke

Open external/evidence-critical work remains:
- #2 real Windows 11 + real L2TP E2E;
- #4 real shared/dedicated multi-proxy lease behavior;
- #5 real operator GUI/profile/selective live behavior;
- #6 real CustomEphemeral auth/PSK/cert/cleanup;
- #7 real keepalive failure → hangup → cooldown → reconnect;
- #11 permanent performance/memory requirement;
- #13 representative 12–24 h exact-binary soak with traffic/reconnect/Pause/Resume/reconfigure and correlated managed/native resource series.

Hosted Actions smoke — tooling mechanics only. Не выдумывай real L2TP/soak evidence.

## Порядок продолжения

1. Live sync exact `main` + exact-head build/handoff.
2. Read handoff docs and issue comments.
3. Verify live `dev/issue49-probe-target` still contains validated source commit `1684718295944ecdb28216ae02c32365ff7b2b0c`, then finish #49/#50 clean acceptance.
4. Update issues with concrete result SHA/run IDs.
5. Update handoff docs after every accepted engineering block.
6. Continue широкими связанными блоками, а не одной мелкой задачей, но не churn уже доказанных #45/#47/#6 boundaries без нового finding.
7. Для новых findings: issue-first с acceptance, затем code/tests, permanent Windows CI, exact-head CI.

Общайся с пользователем по-русски. Не задавай вопросы, ответы на которые уже есть в live GitHub/requirements. Начни сразу с синхронизации GitHub и продолжай от фактического current state.

---
