# New Chat Startup Prompt — ProxyToAnyConnect

Скопируй весь текст после разделителя первым сообщением в новый чат.

---

Продолжаем разработку публичного GitHub-репозитория **`lukindv77/ProxyToAnyConnect`**. Не начинай проект заново. **Live GitHub — главный source of truth.** Сначала синхронизируй exact current `main`, Actions и live issues/comments, затем продолжай код.

## Обязательная синхронизация

1. Получи exact current `main` SHA и tree SHA.
2. Прочитай на current `main` минимум `docs/handoff/NEW_CHAT_PROMPT.md`, `SESSION_2026-08-28.md`, `CURRENT_STATE.md`, `AUDIT_SNAPSHOT.md`, `ACTIVE_DEVELOPMENT.md`, `FINAL_CI_STATUS.md`, `ISSUES_SNAPSHOT.md`, `CHAT_TRANSFER_CHECKPOINT.md`, а также `docs/requirements.md`, `docs/architecture.md`, `docs/memory-stability.md`, Windows integration/soak evidence docs и permanent `build.yml`/`handoff.yml`.
3. Получи live issues/comments для открытых задач. На этом snapshot открыты только **#2/#4/#5/#6/#7/#11/#13**; если live GitHub отличается, верь live GitHub.
4. Проверь `build` и `handoff` именно для exact current head. Не называй новый head green по старому SHA.

## Последний полностью принятый production code checkpoint перед docs commit

Production `main`: **`5811900dfbf7488bd8ac53af20348c462681eeef`**, tree **`e44bf16408da3abade0c0f4d04708e6fd5ccd4ac`**.

Exact-main CI:
- build #616 / run **`33152272544`** — success целиком;
- Windows artifact `9678213447`, digest `sha256:bd31b7f143d11c56cfc6794e55760e156341ca07bdd8fcbb52691d5010e9c1e7`;
- handoff #393 / run **`33152272516`** — success;
- handoff artifact `9678172387`, digest `sha256:bef544b5997914274001b50fce35684dcdd633d44c6230de654c0769db0a77c9`.

Docs commit после этого checkpoint двигает `main`, поэтому новый чат обязан снова проверить live exact head и его Actions.

## Что уже принято

Deterministic production hardening закрыт completed как минимум через #85. Последняя цепочка:
- #79 — реальный configured outbound acquisition deadline; owner/VPN cancellation precedence сохранён; genuine deadline -> 504 до commitment;
- #80 — client header deadline -> 408 до outbound ownership; Pause/Shutdown остаётся lifecycle cancellation;
- #85 — terminal coordinator/host сохраняет только failed exact VPN cleanup owners для serialized retry, runtime не reopen; real application shutdown делает максимум один immediate retry того же host после полного first pass.

Также закрыты #52/#53/#54/#58/#59/#62/#63/#66/#67/#70/#71/#73/#75/#77 и более ранние HTTP/DNS/RAS/performance-test hardening issues. Exact lineage брать из live issue comments; transient dev validation branches не merge wholesale.

## Product/architecture invariants — не ослаблять

Windows 11 x64, C#/.NET 10 WinForms+tray, multiple local HTTP/HTTPS forward proxies. Каждый proxy связан с выбранным L2TP и **никогда не имеет DIRECT fallback**.

Сохранять:
- explicit Exit lifecycle, multiple independent proxies, Pause/Resume, bounded concurrency, exact accepted-session drain;
- shared/dedicated L2TP lease semantics;
- ExistingWindowsProfile + private CustomEphemeral PBK;
- DPAPI-protected password/PSK, unmanaged zero-before-free, no plaintext persistence/logging;
- outbound TCP source `Bind()` + `IP_UNICAST_IF`; proxied DNS only custom L2TP-bound resolver;
- split-tunnel/default-route guards;
- `Disconnected -> Dialing -> Verifying -> Ready`, no usable context before real L2TP-bound HTTPS verification;
- L2TP loss cancels dependents fail-closed; no TLS MITM for CONNECT;
- strict HTTP framing/request/authority/response commitment and strict verification framing;
- configured client/outbound deadlines with lifecycle/VPN precedence;
- DNS exact response ownership, monotonic TTL and bounded cache;
- pooled 32 KiB transfer path, bounded memory, no production forced GC;
- memory optimization must not regress latency/jitter/throughput; do not widen existing 1.25x timing guards to land unrelated code;
- retryable cleanup never makes disposed runtime usable again and never permits overlapping exact native generations.

## Real release boundaries — не подменять hosted smoke

Open external/evidence-critical work remains:
- #2 — real Windows 11 + real L2TP E2E;
- #4 — real shared/dedicated multi-proxy leases;
- #5 — real operator GUI/profile/selective behavior;
- #6 — real CustomEphemeral auth/PSK/cert/cleanup;
- #7 — real keepalive failure -> invalidation -> hangup -> cooldown -> reconnect;
- #11 — permanent performance/memory requirement;
- #13 — representative 12–24 h exact-binary soak with traffic/lifecycle churn and correlated resource series.

Hosted Actions smoke — tooling mechanics only. Не выдумывай real L2TP или 12–24 h soak evidence.

## Порядок продолжения

1. Live sync exact `main`, tree и exact-head `build`/`handoff`.
2. Продолжай **широким связанным deterministic audit/development block**, а не одной косметической задачей.
3. Приоритет: proxy cancellation/deadline/commit ownership; RAS/native interop lifetime/buffers; verification response edge cases; DNS fallback/cache/deadline correctness; bounded process state under #11.
4. Для нового concrete finding: issue-first acceptance -> code/tests -> permanent Windows CI -> merge/rebase -> exact-main build + handoff -> exact SHA/run/artifact comment.
5. Не churn уже доказанные boundaries без нового воспроизводимого finding и не ослабляй security/routing/performance policy ради CI.
6. Реальные #2/#4/#5/#6/#7/#13 не закрывай без настоящего внешнего evidence.

Общайся с пользователем по-русски. Не задавай вопросы, ответы на которые уже есть в live GitHub/requirements. Начни сразу с синхронизации GitHub и продолжай разработку от фактического current state.

---
