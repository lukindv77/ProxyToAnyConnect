# New Chat Startup Prompt — ProxyToAnyConnect

Скопируй весь текст после разделителя первым сообщением в новый чат.

---

Продолжаем разработку публичного GitHub-репозитория **`lukindv77/ProxyToAnyConnect`**. Не начинай проект заново. **Live GitHub — главный source of truth.** Сначала синхронизируй exact current `main`, Actions и live issues/comments, затем сразу продолжай разработку.

## Обязательная синхронизация в начале

1. Получи exact current `main` SHA и tree SHA.
2. Прочитай на current `main` минимум `docs/handoff/NEW_CHAT_PROMPT.md`, `SESSION_2026-09-13.md`, `CURRENT_STATE.md`, `ACTIVE_DEVELOPMENT.md`, `FINAL_CI_STATUS.md`, `ISSUES_SNAPSHOT.md`, `AUDIT_SNAPSHOT.md`, `CHAT_TRANSFER_CHECKPOINT.md`, а также `docs/requirements.md`, `docs/architecture.md`, `docs/memory-stability.md`, Windows integration/soak evidence docs и permanent `.github/workflows/build.yml` / `handoff.yml`.
3. Получи live issue states/comments для #94/#95 и external/evidence issues. На этом snapshot открыты **#2/#4/#5/#6/#7/#11/#13/#94/#95**. Если live GitHub отличается, верь live GitHub.
4. Проверь `build` и `handoff` именно для exact current `main`. Не переноси green verdict со старого SHA.

## Последний полностью принятый production-changing checkpoint

Последний production-changing commit: **`f0763ec9337a0758c45a0add65e27d4b8f689482`**, tree **`e6928be6d0134330cf8b7637e475e69ff159cdd5`**.

Exact production-tree CI:
- build #624 / run **`33165692687`** — success;
- Windows artifact `9683511938`, digest `sha256:38bde53b760ceeb045d985058d7c5627f7275d189422c70fb277a45b5cab8247`;
- handoff #397 / run **`33165692716`** — success;
- handoff artifact `9683470928`, digest `sha256:2bd3366dd24a68e3dd3911438055ade9e9e51f13d235b0bb7f0b1c832e45368c`.

После #92 были два maintenance commits: `d6100bf75ae46c2c7a3e45af0a077c7955ede179` временно добавил one-time Actions cleanup workflow, а `4a653627e0aa0a22967a077821470c133980c675` удалил его с `[skip ci]`, вернув тот же production tree. Затем transition-doc commits снова двинули `main`. Поэтому сначала обязательно проверь фактический live docs head и его exact Actions.

## Последнее принятое deterministic hardening

- #88 — verification HTTP grammar hardening, closed completed.
- #89 — reject ambiguous exact-owner CNAME/A and multiple-owned-CNAME RRsets, closed completed.
- #92 — reject non-QUERY DNS OPCODE and exact queried-owner A/IN with `RDLENGTH != 4`, closed completed; production commit `f0763ec...`.
- RAS x64 ABI/layout audit — green run **`33164715623`**; Windows SDK C++ и managed probe совпали по всем 12 проверяемым size/offset values. Production RAS interop менять не понадобилось.

Earlier completed deterministic chain includes #52/#53/#54/#58/#59/#62/#63/#66/#67/#70/#71/#73/#75/#77/#79/#80/#85 and earlier strict HTTP/DNS/RAS/performance-test work. Exact lineage брать из live issue comments, не из старых transient dev branches.

## ПЕРВОЕ активное задание: #94

Issue **#94 — `Validate complete DNS message sections before accepting routing evidence`** открыт.

Finding: post-#92 `L2tpDnsResolver.ParseResponse` получает routing evidence из answer section, но не структурно валидирует все объявленные `NSCOUNT`/`ARCOUNT` records и exact exhaustion всего DNS packet. Нужно fail closed на declared-but-missing/truncated authority/additional RR и arbitrary trailing bytes, при этом валидные authority/additional/OPT records должны оставаться допустимыми и не давать routing evidence.

Dev branch: **`dev/issue94-dns-complete-message`**, head **`525216f0c8b1470638d989affbffd6d3e0b89e17`**.

Первый validation run: **`33165671844`**, failure ДО build. Exact-base/blob guards прошли. `.github/validation/issue94-transform.ps1` упал на regex anchor: `Expected exactly one generic DNS section parser helper anchor, found 0.`

Что делать сразу:
- re-fetch branch/source/workflow;
- исправить только validation transform anchor под фактическую post-#92 сигнатуру/расположение helper boundary;
- не менять acceptance policy, не ослаблять tests/perf thresholds;
- rerun Windows dev validation до Release build + full aggregate + bot-published validated source;
- после green собрать clean production reconstruction на exact then-current `main`, только `L2tpDnsResolver.cs` + `DnsResponseBindingSelfTests.cs`, permanent PR CI, rebase merge, exact-main build+handoff, lineage comment, close completed.

## ВТОРОЕ активное задание: #95

Issue **#95 — `Fail closed on ambiguous PPP IPv4 to Windows interface mapping`** открыт.

Finding: `VpnInterfaceResolver.ResolveByAddress` сейчас возвращает первый Windows adapter, содержащий PPP local IPv4. При duplicate ownership выбор становится enumeration-order dependent, а выбранный `InterfaceIndex` затем используется для `IP_UNICAST_IF`, local bind, L2TP DNS и HTTPS verification. Нужно принимать только ровно один interface; zero/multiple — fail closed.

Dev branch: **`dev/issue95-unique-interface`**, head **`4e8f5e4d3a2625b76730d917b7fc293a4dc01476`**.

Первый validation run: **`33165867074`**. Transform/staged-surface check прошёл; Release compile упал с **CS0019** в `src/ProxyToAnyConnect/Vpn/VpnInterfaceResolver.cs:52`: operator `??` нельзя применить к `List<VpnInterfaceInfo>` и `VpnInterfaceInfo[]`. Aggregate не запускался.

Что делать сразу:
- re-fetch branch/transform/workflow;
- исправить только type composition: например привести обе стороны к `IReadOnlyList<VpnInterfaceInfo>` или явно branch on `matches is null`;
- сохранить exactly-one candidate semantics, zero/multiple fail-closed tests и exact captured name/description/index/DNS values;
- rerun Windows dev validation до full aggregate + bot source;
- затем clean reconstruction на exact live main, permanent PR CI, rebase merge, exact-main build+handoff, lineage comment, close completed.

#94 и #95 можно вести параллельно, но production clean branches всегда собирай заново поверх exact актуального `main`; не merge dev workflow/transform history wholesale.

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
- DNS exact response ownership, CNAME ambiguity rejection, response grammar, monotonic TTL and bounded cache;
- pooled 32 KiB transfer path, bounded memory, no production forced GC;
- memory optimization must not regress latency/jitter/throughput; **не widen existing 1.25x timing guards** ради unrelated CI;
- retryable cleanup never makes disposed runtime usable again and never permits overlapping exact native generations.

## Real release boundaries — hosted smoke не подменяет evidence

- #2 — real Windows 11 + real L2TP E2E;
- #4 — real shared/dedicated multi-proxy leases;
- #5 — real operator GUI/profile/selective behavior;
- #6 — real CustomEphemeral auth/PSK/cert/cleanup;
- #7 — real keepalive failure -> invalidation -> hangup -> cooldown -> reconnect;
- #11 — permanent performance/memory requirement;
- #13 — representative 12–24 h exact-binary soak with traffic/lifecycle churn and correlated resource series.

Hosted Actions smoke доказывает mechanics/tooling only. Не выдумывай real L2TP или 12–24 h soak evidence и не закрывай эти issues без настоящего внешнего evidence.

## После #94/#95

Продолжай широкими связанными audit/development blocks: proxy/session shutdown and response commitment, RAS generation/projection/interface/callback ownership, DNS TCP/failover/deadline exactness, bounded logging/metrics/process state under #11. Создавай новый issue только при concrete reproducible defect.

Для каждого нового finding: **issue-first acceptance -> code/tests -> Windows dev validation -> clean production reconstruction -> permanent PR CI -> rebase merge -> exact-main build + handoff -> exact SHA/run/artifact lineage -> close completed**.

Общайся с пользователем по-русски. Не задавай вопросы, ответы на которые уже есть в live GitHub/requirements. Начни сразу с live sync и исправления #94/#95.

---
