# S1-c Slice-0a 只读评审：新增受控文本断言检查类型（TextAssertion）——持久化与身份协议落地清单

- 目标：为 G92-1 / S1-c「新增受控文本断言检查类型」确认**接线面 + 身份协议**，不含实现。
- 方法：只读源码 + 检索取证；每条结论附 `file:line`；无法取证者标【未证实】。
- 约束遵守：仅写本文件；未调用 save_memory/manage_memory/goal_update/agent_state/manage_tasks；未做 git 写/build/test。
- 既有事实（输入）：DefinitionHash 载荷仅 `"{DefinitionRef}|{Kind}|{CommandTemplate}"`；现有 kind = Build/Test/FileEvidence（另有 Postcondition/Artifact/Semantic/External 词表值）。

---

## Q1 新增一个受控 kind 的完整接线面

| # | 层级 | 位置（仓库根相对路径:行） | 现行为 | 是否需改 |
|---|---|---|---|---|
| 1 | kind 词表 | `Source/PuddingCore/Goals/GoalVerificationContracts.cs:137`（`GoalVerificationSpecKinds`）、`:139-144`、`:150` | **不是 enum，是字符串常量类**（:136 注释「显式字符串，避免与既有 wire 值冲突」） | **需改**：+1 常量（如 `TextAssertion="text-assertion"`） |
| 2 | spec 载体 | `Source/PuddingCore/Goals/GoalCheckContracts.cs:17`（`Kind`）、`:14`（`CriterionRevision`）、`:20`/`:22`（`DefinitionRef`/`DefinitionHash`）、`:28`（`InputFingerprint`） | `Kind` 为自由字符串；spec **无「期望文本」字段** | **需改**：+`ExpectedText`（或等价受控字段） |
| 3 | 定义注册表 | `Source/PuddingPlatform/Services/Goals/GoalCheckDefinitionRegistry.cs:25`/`:26`（Ref 常量）、`:28-48`（`Definitions` 字典；FileEvidence 条目 :40-43）、`:52-58`（`ComputeDefinitionHash`）、`:62-68`、`:70-77`、`:95-130`（`TryBuildCommand`；`:106-111` 显式拒绝把 file-evidence 解析成命令） | 3 条已登记定义（build/test/file-evidence） | **需改**：+1 定义；并把新 kind 加入「永不建命令」拒绝集 |
| 4 | Runner 分派 | `Source/PuddingPlatform/Services/Goals/GoalCheckRunner.cs:135-143`（WorkingDirectory 门禁）、`:145-151`（file-evidence 只读分支）、`:153-161`（TryBuildCommand 失败⇒failed）、`:163-181`（命令准入）、`:187`（启动进程）、`:413-427`（`MatchesRecord`）、`:556-590`（`BuildReport`；`:575` 复制 spec.DefinitionHash）、`:601`（`LacksTestEvidence`） | 唯一「非进程」分支是 file-evidence，且仍在门禁之后 | **需改**：+纯文本分支（建议置于 :135 之前，见 Q5） |
| 5 | EvidencePolicy | `Source/PuddingCore/Goals/GoalCheckEvidencePolicy.cs:47-59`（`Evaluate`）、`:62-107`（身份/版本/新鲜度/证据引用通用校验）、`:108-115`（kind 分类变量）、`:117-122`（file-evidence 要求 `file:` 前缀证据）、`:124-135`（executed-kind 要求 InvocationId/ReportRef）、`:137-142`（未结束后台进程⇒waiting）、`:144+`（exit/test 计数）、`:199-211`（终局分派：semantic/external/executed 放行 :201-202；file-evidence 放行 :206-207；**未知 kind ⇒ `unsupported_check_kind` 降级 failed :209-210**） | 未登记 kind 一律降级失败 | **需改**：+`isTextAssertion` 放行分支 + 专属证据要求（如要求 `assistant-output:<turnId>@<seq>` 前缀） |
| 6 | CheckRecordStore 落库 | `Source/PuddingPlatform/Services/Goals/GoalCheckRecordStore.cs:12`（去重键语义）、`:44-52`（dedupKey）、`:56-57`（recordId）、`:70-90`（新增行，含三要素 :86-88）、`:100-108`（定义/指纹变化⇒回 pending、清 ReportJson）、`:182-240`（`FinishAsync`；`:233` EvidenceUnavailable⇒回 pending） | kind 无关，只吃 identity 三要素 | **不改**（前提：新 spec 必须带齐三要素） |
| 7 | 结算读取/裁决 | `Source/PuddingPlatform/Services/Goals/GoalSettlementStore.cs:110`（`GetCandidatesAsync`）、`:157-178`（合同行/检查记录/证据范围）、`:264`（`PlanFingerprint=binding?.PlanFingerprint`）、`:267`（`CheckReports`）；`.../GoalSettlementWorker.cs:97-104`（空合同⇒派生）、`:110-139`（RunChecks+ReadForEpochAsync+waiting 合并）、`:159-186`（GoalCheckContext，含 WorkingDirectory）、`.../ConservativeGoalIterationVerifier.cs:22-27`、`:52-84` | 合同+持久报告 ⇒ capsule ⇒ verdict | **仅当引入新受阻码时才改**（否则 :52-84 分支不动） |
| 8 | 前端受阻码/文案 | `Source/PuddingPlatformAdmin/src/pages/chat/components/goalBlockerCodes.ts:20-83`（码→文案表）、`:76-80`（合同覆盖码）、`:86-89`（未知码兜底）；`GoalBanner.tsx:264-265`、`:382-384`；`src/services/platform/api.ts:2821`（blockedCode）、`:2867-2870`（blockerCode） | kind 不直接落受阻码；未知码有兜底展示 | **不改**；仅当新增受阻码时，同步改 `goalBlockerCodes.ts` + `goalBlockerCodes.test.ts:9-36` 白名单断言 |
| 9 | 回归/测试锚点 | `Source/PuddingPlatformTests/Services/Goals/GoalCheckRunnerTests.cs:621-623`（file-evidence 用例模板，动态算 hash）、`GoalAcceptanceContractPlannerTests.cs:329`（断言 DefinitionRef） | 已有 file-evidence 负向/正向对照 | **需改**：+文本断言用例（沿用同一模板） |

### Q1 【建议】最小改动清单（4 处生产 + 1 处测试）
1. 词表 +1 常量（`GoalVerificationContracts.cs:137` 区）。
2. spec +1 字段（`GoalCheckContracts.cs:17` 附近），并保证序列化进 `ChecksJson`（`GoalVerificationPersistence.SerializeChecks`）。
3. 注册表 +1 定义条目并把它并入 `TryBuildCommand` 的「非命令 kind」拒绝集（`GoalCheckDefinitionRegistry.cs:28-48`、`:106-111`）。
4. Runner +1 纯文本分支 + Policy +1 放行/证据要求（Q5/Q2 给出插入点语义）。
5. 测试沿用 file-evidence 对照结构（`GoalCheckRunnerTests.cs:621`）。

---

## Q2 期望文本如何进入 DefinitionHash 载荷

**【事实】**
- 现载荷：`GoalCheckDefinitionRegistry.cs:52-58` → `sha256(utf8("{DefinitionRef}|{Kind}|{CommandTemplate}"))`。
- 产出侧：`GoalAcceptanceContractPlanner.cs:179`/`:239`/`:283` 计算 hash，写入 spec 与 criterion（`:191`/`:205`、`:249`/`:263`、`:295`/`:309`）。
- 报告侧：hash 只是**从 spec 复制**（`GoalCheckRunner.cs:575`），不重算。
- 比较侧：`GoalCheckEvidencePolicy.cs:79-81`（spec 无 hash ⇒ failed `definition_hash_missing`）、`:85-87`（report≠spec ⇒ **invalidated `definition_changed`**）。
- 另一条 hash 传播：`GoalCheckInputIdentity.cs:229-230`（把检查定义 hash 收进输入清单 Identity）；但该类**无任何生产调用点**（`Source/PuddingPlatform` 全目录检索仅命中自身 `:42`，类注释 `:39-40` 自述 P1 零接线）⇒ 现状 Identity 未参与失效判定。

**【建议】哈希变化与影响面**
- 若把期望文本**无条件**拼进 payload（如 `...|{ExpectedText}`，旧定义填空串），则 3 条既有 DefinitionRef 的 hash 全变。后果：
  - 已落库合同行存的是生成时的 hash，不会被重算；且 planner 对「已有条件」的合同早退（`GoalAcceptanceContractPlanner.cs:45-53`）⇒ **同 epoch 不会重算**，历史报告不会立刻失效；
  - 但一旦 `GoalCheckInputIdentity` 接线（正是 S1-c 方向），Identity 立刻变化 ⇒ 旧报告 `input_fingerprint` 失配 ⇒ `stale_input_fingerprint`（`GoalCheckEvidencePolicy.cs:95-97`）；
  - 硬编码 hash 字面量的测试会红【未证实：检索受 2000 文件枚举上限限制，未做全仓字面量扫描】。
- **最小侵入方案 A（推荐）**：新增**独立 DefinitionRef**（如 `checks/text-assertion.md#equals`），期望文本只进**该条目**的载荷（`ExpectedText` 字段，仅非空时拼接；旧三条目 payload 字节不变）⇒ 既有历史 hash/报告零影响。
- 方案 B（不推荐）：给现有条目加字段并拼进公共 payload ⇒ 需一次显式「全量失效宣告」。
- **硬约束**：无论 A/B，期望文本必须进入某个 hash 载荷（DefinitionHash 或 dedup 键新分量，`GoalVerificationPersistence.cs:29-36`），否则文本变化**不会**失效旧绿灯。

---

## Q3 CriterionRevision / ContractVersion / InputFingerprint 三轴语义与更新路径

| 轴 | 定义位置 | 比较/失效位置 | 更新路径 | 结论 |
|---|---|---|---|---|
| `CriterionRevision` | spec：`GoalCheckContracts.cs:14`；criterion：`GoalVerificationContracts.cs:111`（注释：条件内容变化必须递增）；result：`GoalVerificationContracts.cs:176` | `GoalCheckEvidencePolicy.cs:71-74`（report≠check ⇒ **invalidated `criterion_revision_changed`**） | planner **硬编码 1**（`GoalAcceptanceContractPlanner.cs:202`/`:260`/`:306`）；合同早退（`:45-53`）；落库 `GoalCheckRecordStore.cs:49`/`:86`/`:104`；runner 匹配 `GoalCheckRunner.cs:415`；dedup 键 `GoalVerificationPersistence.cs:29-36` | 【事实】生产代码**无递增点**，等同静态轴；递增需新增显式路径（如合同整理时 bump） |
| `ContractVersion` | `GoalAcceptanceContractStore.cs:12-14`（语义注释） | **未被任何失效链读取**（检索仅 :69/:88 写入 + `GoalSettlementStore.cs:516` 核验行固定 1）【事实】 | 新建=1（`:69`）；内容/指纹变化 ⇒ +1（`:84-90`） | 【事实】只作合同行版本/审计，**不失效任何绿灯** |
| `InputFingerprint` | spec：`GoalCheckContracts.cs:28` | `GoalCheckEvidencePolicy.cs:89-91`（缺⇒failed `input_fingerprint_missing`）、`:95-97`（≠⇒**invalidated `stale_input_fingerprint`**） | 产出：`GoalAcceptanceContractPlanner.cs:150-158`（绑定的 PlanFingerprint 优先，缺失退化为 `epoch:{e}:objective:{v}`）；来源 `GoalSettlementStore.cs:264`；落库 `GoalCheckRecordStore.cs:51`/`:88`/`:106` | 输入内容轴（计划/工作树），当前与「期望文本」无关 |

**【建议】期望文本变化应通过哪一条失效旧绿灯**
- 首选 **DefinitionHash**：期望文本属「检查定义内容」语义（与 `goalBlockerCodes` 无关），改文本即改定义 ⇒ `definition_changed` invalidated（`GoalCheckEvidencePolicy.cs:85-87`），与既有 file-evidence 的失效口径一致。
- 若期望文本变化同时意味着**条件语义**变化，则叠加：bump `GoalCriterion.Revision` 与 `spec.CriterionRevision`（当前无递增路径，需在 S1-c 显式引入）⇒ `criterion_revision_changed`。
- **禁选**：只改 `ChecksJson` 的期望文本、不动任何 hash 载荷 ⇒ 旧绿灯静默保留（等于假绿灯，违反 ADR-092 §5.2 本意）。
- `ContractVersion` 不应作为失效判据（现状也不被读取）；可作「合同已变⇒应重排检查」的审计信号。

---

## Q4 文本断言的输入来源（canonical Turn 最终 assistant 输出）与工具调用数记账

**【事实】可取到，但 Goals 路径当前未接**
- 事件层读取入口：`Source/PuddingRuntime/Services/TurnExecutorAdapter.cs:151-165` 把 `done` 帧映射为 `turn.completed`，并把 **`payload.reply`** 写入终态信息（`:156-161`，`TryGetString(payload,"reply")` :159）⇒ 可按 `(conversationId, turnId)` 读 `ConversationEvents(type=turn.completed).payload.reply`。
- 投影层读取入口：`Source/PuddingCore/Platform/ConversationTranscriptFold.cs:168`（`Fold` 纯函数）→ `ConversationTurn.AssistantText`（`:118`，由 `message.content.appended` 拼接 `:340-368`；`Reply` 字段同为兜底）；现有消费方 `Source/PuddingPlatform/Services/RawSessionLogService.cs:343`、`.../Controllers/Api/MessageApiController.cs:312`。
- 现状缺口：`GoalSettlementStore.cs:173-178` 的 `evidenceQuery` 只取 `EventId/Type/Sequence`，仅在 failed 分支取 `TurnFailed` payload（`:236-247`）；`GoalEvidenceCapsule`（`GoalVerificationContracts.cs:13-58`）**无正文字段**。⇒ S1-c 必须新增一个「按 turnId 读终态 `reply`（或 Fold `AssistantText`）」的读取入口，并把文本或其 hash 注入 `GoalCheckContext`/capsule。

**【事实 + 推断】「本轮工具调用数」记账**
- 落点：`GoalSettlementStore.cs:214-215`（在 evidenceQuery 范围内 `CountAsync(Type==ToolCallRequested)`）→ `:276`（`ToolCalls`）→ `:492`/`:501`（写入 iteration.ToolCalls 与 goal.TotalToolCalls）；证据范围 = goal conversation + 该 iteration.TurnId + `Sequence ∈ [AcceptedSequence, TerminalSequence]`（`:173-178`）。
- 覆盖性：父 Turn 的每个 `tool_call` 帧逐条产生 `ToolCallRequested`（`TurnExecutorAdapter.cs:152`），**父级覆盖完整**；但子代理工具调用属另一组事件类型（`subagent.tool.started/completed/failed`，`Source/PuddingCore/Platform/ConversationContracts.cs:127-130`；归档计数见 `Source/PuddingPlatform/Services/FileSubAgentRunStore.cs:711-745`），且委托用量是**单独**汇总（`GoalSettlementStore.cs:203-210`，定义 `:340 SumDelegatedUsageAsync`）⇒ **ToolCalls 不覆盖子代理工具调用**。
- 补充：`ToolCalls` 目前只参与记账/进度写回，未见参与裁决分支【未证实：`ApplyProgressAccounting` 对 ToolCalls 的消费未逐行核查】。

---

## Q5 Runner 的 WorkingDirectory 门禁与纯文本分支插入点

**【事实】**
- 门禁位置：`Source/PuddingPlatform/Services/Goals/GoalCheckRunner.cs:135-143` —— `ExecuteOneAsync` 顶部、**所有 kind 分派之前**：`context.WorkingDirectory` 空白 ⇒ `FailedReport(EvidenceMissing)`。
- 既有唯一「纯程序」分支（file-evidence）在门禁**之后**（`:145-151`），且自身仍要求工作区（`:460` `Path.GetFullPath(context.WorkingDirectory!)`）⇒ 它是「同根只读」，不是「无根检查」。
- WorkingDirectory 提供方：`GoalSettlementWorker.cs:164-166`（`_options.CheckWorkingDirectory`，空白则 null）+ `:179` 传入 context。

**【建议】纯文本检查如何走纯程序分支**
- 方案 1（零行为变化，推荐）：在 `:135` 门禁**之前**插入文本断言分支（`EvaluateAssistantText(spec, context)`），只用 context 中的 `GoalRunId/TurnId/IterationNo` 等事实，不触碰 :137 的判据与既有 kind 的任何路径。
- 方案 2（改动门禁表达式）：把 `:137` 改为按 kind 判定，例如 `if (RequiresWorkingDirectory(spec.Kind) && string.IsNullOrWhiteSpace(...))`。语义上更明确，但**改动了既有门禁行为面**（需回归锁定 build/test/file-evidence 在无 Workdir 时仍 `evidence_missing`；现有用例见 `GoalCheckRunnerTests.cs`），非必要不选。
- 无论哪案：EvidencePolicy 侧需同步新增 `isTextAssertion` 放行（`GoalCheckEvidencePolicy.cs:206` 同层）+ 其专属证据前缀校验，否则落到 `:209-210` 的 `unsupported_check_kind` 降级。

---

## 风险与未证实清单
1. 【未证实】是否存在硬编码 definition hash 字面量的测试/文档（检索受 2000 文件枚举上限限制）。
2. 【未证实】子代理事件是否写入父 conversation（现有证据只支持「不在父 Turn 事件范围、且用量单独汇总」）。
3. 【未证实】`ApplyProgressAccounting` 是否消费 `ToolCalls`。
4. 【事实】`GoalCheckInputIdentity` 仍未接线 ⇒ Q2 的「历史报告全部失效」只在接线后才成为现实；若在 S1-c 同时接线 Identity + 改公共 payload，将产生一次性全量 stale。
5. 【缓解】前端对未知受阻码有兜底（`goalBlockerCodes.ts:86-89`、`GoalBanner.tsx:382-384`），新增 kind 不会导致前端崩溃。

## NOTES
- 一次 `list_dir` 报错：`Source/PuddingPlatform/Services/Conversations` 不存在（已知路径纠正，改用 `Services/Conversations` 之外的目录），无阻塞。
- 一次 `file_read` 路径写错：`GoalCheckEvidencePolicy.cs` 实际位于 `Source/PuddingCore/Goals/`，已纠正。
- `search_grep` 两次命中 2000 文件枚举上限（`Source` 全仓、`Source/PuddingPlatformTests`），已缩小目录重试并记录 partial coverage。
- 未执行任何写操作（除本文件）、未执行 git/build/test，符合只读约束。
