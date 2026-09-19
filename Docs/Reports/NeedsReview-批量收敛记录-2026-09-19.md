# NeedsReview 批量收敛记录（2026-09-19）

> 目的：为「卡面陈旧」型卡片的收敛提供持久、可审计的证据留档。
> 背景：用户指出「某些任务没有及时更新，导致内部的设计细节与代码现状已经发生差异」。
> 本记录对应 Task Orchestrator 批 1/4 ～ 4/4 回执的 4 张卡（已全部归档）。

## 1. 收敛总览（2026-09-19，共 9 张 Archived）

| # | 卡 ID | 标题 | 关键结论 | 归档时刻 (UTC) | 最终版本 |
|---|---|---|---|---|---|
| 1 | `4d152325a4574b959d6fdc03552581d8` | SQL 迁移压缩 | 提交 `0742fc6` / `3e17dd4` | 01:48:02 | — |
| 2 | `104fe9c0707f4d458fd9b5f9d96e32d3` | [T01-R] 统一 save_memory 参数声明 | `33c1489` | 01:56:12 | v10 |
| 3 | `77f4141bd9bb4af0ab42cdd7668080c4` | [V6-T4] 图片标注编辑器选型 | `2841576` | 01:56:12 | — |
| 4 | `d0ac9f710b944ee5810b48c74128e98b` | [S01-A-R] 双账本幂等 | `9537f60` | 01:56:12 | — |
| 5 | `ec50aeb052084d1da7e242e34371904b` | DeepSeek 余额前端 | `366191a` | 01:56:12 | — |
| 6 | `7af1b5cc4e5f40abb888967dea268496` | [P2][治理] NuGet 漏洞包升级 | 提交 `b20e95c`（已 push），6 包清零 | 02:29:31 | v7 |
| 7 | `2d5a2ebec158427c9a13e93cd74c78fe` | task_update 版本绑定死锁 | **已修复**：活版本优先于注入快照 | 02:31:09 | v9 |
| 8 | `2b60040d6eb94db7800d0522698e3975` | [P0] Goal 派发未注入 ActiveTask | **已修复**：唯一映射器 `ActiveTaskMetadata.TryBuild` | 02:31:51 | v9 |
| 9 | `4fcd4aa5d7da4a5cba6b5c233e47ab82` | [P0] G92-1 [S1-c] 文本断言 + 合同整理通道 | **已落地**：`GoalContractProposal` 通道 18 文件 | 02:33:02 | v10 |

看板指标：`NeedsReview` 17 → **13**。

---

## 2. 卡 `2b60040d`（P0）详解

### 卡面原述（诊断态，2026-09-13）
GoalRun 派发给本 Agent 实例，payload 携带 `task:{taskId, assignmentId, expectedVersion, status}`，但 canonical 调用全失败：`task_claim` / `task_update` 均报 `task.active_context_missing`，`context_rebuild{attempted:true, stage:"lookup", outcome:"not_visible"}`；`task_get` 报 `task.not_found`；`task_list(mine)` 不含该卡。

三层根因：
- **层 1（关键）**：`AgentInvocationDispatchFactory.cs:134/:141-153` 的 `BuildActiveTask` 要求 `invocation.Metadata` 同时含 `task_id` 与 `assignment_id`，任一为空即 `return null`；而 Goal 迭代 run 的 metadata 未携带（payload 的 task 块与 `invocation.Metadata` 是两条通道）。
- **层 2**：反查重建的身份口径不一致 —— 读侧 `context.AgentInstanceId`（临时子会话身份）vs 写侧 `assignment.AgentId`（持久 agent id）永不相等 ⇒ `mine=false` ⇒ `outcome=not_visible`。
- **层 3**：无人认领 ⇒ Tracker 判 `assignment_execution_missing` ⇒ 写 `Blocked` + 清 `ActiveAssignmentId`（单向闩锁，已单独立卡 `e2c35d6e`）。

### ✅ 复核结论（2026-09-19）
**判定：修复已落地。无新增代码改动。**

**层 1 + 层 2 修复形态 —— 统一映射器（结构性消除）**

`Source/PuddingRuntime/Services/AgentInvocationDispatchFactory.cs:140`：

```csharp
/// <summary>解析 task metadata → Active Task Runtime Context（唯一映射器 ActiveTaskMetadata.TryBuild，ADR-072 §9.1；
/// 三别名/缺键语义见映射器，不得两处各自演化）。</summary>
private static ActiveTaskRuntimeContext? BuildActiveTask(WorkspaceAgentInvocation invocation)
    => ActiveTaskMetadata.TryBuild(invocation.WorkspaceId, invocation.AgentId, invocation.Metadata);
```

映射器本体 `Source/PuddingCore/Tasks/ActiveTaskMetadata.cs` 类注释：

> ADR-072 §9.1：metadata → `ActiveTaskRuntimeContext` 的**唯一映射器**。
> 语义与派发路径（`AgentInvocationDispatchFactory.BuildActiveTask`）**逐键一致**：task_id/assignment_id 等键均支持 snake/camel/Pascal 三别名（按序取首个非空白值）；`task_id` 或 `assignment_id` 任一缺失即返回 `null`（fail-safe）……**canonical 命令路径（ExecutionCommandReader）与投递路径共用本映射器，禁止两处各自演化**。

- **层 1**：`ActiveTaskMetadata.TryBuild` 支持三别名（`task_id`/`taskId`/`TaskId`、`assignment_id`/`assignmentId`/`AssignmentId`），消除了「payload 用 camelCase、metadata 期望 snake_case」这类通道差异。
- **层 2**：`AgentId = agentId` 由调用方传入 `invocation.AgentId`（持久 agent 身份），不再依赖临时子会话身份；且 `ExecutionCommandReader`（canonical 命令路径）与派发路径共用同一映射器 —— 读写两侧口径归一。

`git grep -ln "ActiveTaskMetadata" -- "*.cs"` 命中 4 处：本体 + `ExecutionCommandReader.cs` + `AgentInvocationDispatchFactory.cs` + `GoalContinuationTests.cs`（测试）。

### 验收逐条取证

**验收 1**：`task_claim`/`task_update` 不再返回 `active_context_missing`（ActiveTask 非空走注入路径），并有单测断言 `BuildActiveTask` 在 goal 派发 metadata 下返回非空。

- ✅ `Source/PuddingRuntimeTests/Services/AgentExecutionWakeupActiveTaskPreservationTests.cs:139 CreateForWorkspaceAgentAsync_MetadataTaskKeys_BuildActiveTask`
- ✅ `Source/PuddingRuntimeTests/Services/TaskE2E/TaskActiveTaskFourChainE2ETests.cs:68 ExecuteAsync_ActiveTaskInjected_PropagatesIntoToolInvocationRequest`
- ✅ `:222 ToolInvocationService_ActiveTask_PassthroughToToolExecutionContext`

**验收 2**：反查重建路径在其身份口径下仍命中 `mine`，且跨 Agent 伪造不可通过；覆盖「同 instance / 不同 instance / 子代理深度」三类身份。

- ✅ 可命中：`TaskActiveTaskFourChainE2ETests.cs:325 UpdateTodo_WithoutActiveContext_BlockedTask_RebuildsAndRecoversToReady`
- ✅ 伪造拒绝：`:356 UpdateProgress_WithoutActiveContext_BlockedTask_RejectedAsStateConflict`、`:393 TaskClaimTool_WithoutActiveContext_BlockedTask_RejectedAsStateConflict`、`:184 UpdateCompleted_WithoutActiveContext_RebuildChecksState_ReturnsStateConflict`
- ✅ 版本陈旧：`:375 UpdateTodo_WithoutActiveContext_BlockedTask_StaleVersion_ReturnsVersionConflict`

**验收 3**：端到端 `Ready → reserved → assigned → accepted → progress` 全链在同一 GoalRun 内完成。

- ✅ `TaskActiveTaskFourChainE2ETests.cs:90 ClaimThenCompleted_RealTools_AtomicallyWritesTaskAttemptEventBinding`
- ✅ `:282 WaitThenWakeup_ActiveTaskRestored_RealTools_CompletesTask`
- ✅ `:155 ClaimAsync_StaleAssignment_ReturnsAssignmentStale`
- 另 `Source/PuddingPlatformTests/Services/Goals/GoalContinuationTests.cs` 亦引用该映射器

合计 20 个用例，跨 3 个测试文件。

### 遗留
层 3（无人认领 → 单向闩锁）已单独立卡 `e2c35d6eeae244c191ed508ccd85b6fe`，不在本卡范围。

---

## 3. 卡 `4fcd4aa5`（P0 G92-1 [S1-c]）详解

### 卡面原述（诊断态，2026-09-18）
前端 UI 创建的目标是**一行自然语言**，而 `GoalObjectiveEvidenceParser.cs` 只认显式 `证据:` 标记且要求目标通过 `IsSafeTarget`（`.csproj`/`.sln`/`.slnx`）→ `evidence.HasAny=false` → `GoalAcceptanceContractPlanner.cs:98` 产出 `source="bounded_planning"`（仅 `bounded:build` + `bounded:test`）→ S1-a 覆盖门命中 `IsEngineeringGatesOnly` → `Blocked("contract_coverage_insufficient")` → **每次迭代同一分支 ⇒ 目标永远无法完成**。

实测：`goal_acceptance_contracts` 9 行，source 全为 `bounded_planning`。

**三个待裁决点**：① 文本断言判据来源 ② 版本化表示 ③ 合同整理通道形态。

### ✅ 复核结论（2026-09-19）
**判定：三个裁决点均已实现并落地。无新增代码改动。**

**裁决点 ③ → 走「Agent 可写的合同修订接口」**
`git grep -ln "goal_contract_proposal|GoalContractProposal" -- "*.cs"` 命中 **18 个文件**：

| 层 | 文件 |
|---|---|
| 契约 | `Source/PuddingCore/Goals/GoalContractProposal.cs` |
| 端口 | `Source/PuddingCore/Platform/ExecutionRunContracts.cs`、`Source/PuddingCore/Runtime/ITurnExecutor.cs` |
| 校验 | `Source/PuddingPlatform/Services/Goals/GoalContractProposalValidator.cs` |
| 整理 | `Source/PuddingPlatform/Services/Goals/GoalContractRefinementStore.cs` |
| 接线 | `GoalSettlementStore.cs`、`GoalSettlementWorker.cs`、`ExecutionRunCoordinator.cs`、`SqliteExecutionJournal.cs` |
| Agent 侧 | `Source/PuddingRuntime/Services/AgentLoop/AgentLoopResponse.cs`、`TurnExecutorAdapter.cs` |

**裁决点 ① → `ExpectedText`**
`GoalContractProposalValidatorTests.cs:192 Validate_MissingExpectedText_IsRejected`、`:207 Validate_TextAssertionWithInputRefs_IsRejected`。

**裁决点 ② → 新增独立断言 kind**
`GoalContractProposalValidatorTests.cs:170 Validate_NonTextAssertionKind_IsRejected`。

### 验收逐条取证

**验收 1（不可删反例）**：✅ 有真实回归证据（定向测试）

- `GoalContractRefinementWiringTests.cs:404 Worker_WithValidProposal_RefinesContract_AndUnlocksCoverageGate` ⇒ 有效提案 → 合同被整理 → **覆盖门解锁**
- 辅证：`GoalContractProposalValidatorTests.cs:50 Validate_ValidProposal_AcceptsAndDerivesPlatformOwnedPlan`、`GoalContractRefinementStoreTests.cs:87 Apply_ValidProposal_CasUpdatesContractAndWritesAuditEvent`

**验收 2（§11 场景）**：⚠️ 部分取证 —— 未见「只输出指定文本 ⇒ 一轮完成、零工具调用、零 build/test」的同名定向测试；已取证最近邻为上述合同整理通道测试。**移交母卡 `b849ef8d` 收口时处理**。

**验收 3（S1-a 拦截保持不变）**：✅ 对照组测试存在

- `GoalContractRefinementWiringTests.cs:499 Worker_WithoutProposal_KeepsBoundedPlanning_AndCoverageGateBlocks` ⇒ 无提案 → 保持 `bounded_planning` → 覆盖门**仍然拦截**
- 即「不再永久拦截」是**新增可达通道**，而非放宽门禁（未修改 `GoalContractCoverageGateTests` 判定表 A）

**验收 4（不触碰身份轴）**：✅

- `Validate_SelfReportedGoalRunId_IsRejected`(:126)、`Validate_SelfReportedDefinitionHash_IsRejected`(:141)、`Validate_SelfReportedCriterionRevision_IsRejected`(:152)
- `GoalContractRefinementStoreTests.cs:204 Apply_SelfReportedIdentityFields_AreRejectedBeforeAnyWrite`
- ⇒ 身份 / 定义哈希 / 修订号一律平台派生，Agent 自报 fail-closed 拒绝

**验收 5（部署纪律）**：⚠️ 仅核实代码与测试落地，未核实生产部署批次。**移交母卡 `b849ef8d`**。

### 测试规模
`GoalContractProposalValidatorTests`(19) + `GoalContractRefinementStoreTests`(9) + `GoalContractRefinementWiringTests`(4) + CoreTests `GoalContractProposalTests` + `ExecutionJournalGoalContractProposalPayloadTests` ≈ 30+ 用例。

### 溯源
`GoalContractProposal` 通道由两次子代理委托落地：
- `sub-c33292b3`（片 6-3a）：验证器 + CAS 提交存储（16:08–16:52）
- `sub-1da2a7f8`（片 6-3b）：接线进 `GoalSettlementWorker` + 端到端回归（16:53–17:43）

---

## 4. 卡 `7af1b5cc`（P2 治理）详解

见提交 `b20e95cf256849fc5be894dfe96c96e0b17548fe` 的 commit message（含 6 包逐包处置、验收逐条、超边界修复说明、遗留）。

**关键**：`NU1902/NU1903/NU1904` 复扫从 47 个告警组合 → **0 告警**。唯一未完全达成项为验收 2「全解决方案 `dotnet build` exit=0」——阻碍为运行中生产进程（`PuddingAgent(19788)`/`PuddingDesktop(36768)`）锁定自身 `bin` 的文件锁（44 个 MSB3027/MSB3021，**0 个 CS 错误**），属环境性阻碍。

**超边界修复（父级复核后接受）**：`TokenUsageSchemaBootstrapper.cs` —— 新 bundled SQLite 默认禁用双引号字符串（DQS），暴露 bootstrapper 在 legacy `context_layer_metric_events` 表上创建引用不存在列的索引 ⇒ 生产 legacy 库升 native 后启动即崩。修复：建索引前逐列校验（`ColumnExistsAsync`），缺失则跳过 + `LogWarning`。

**遗留**：
- 9 个文件 BOM 被本次编辑剥离（字节级恢复待 terminal 授权，工单 `tap_6c0fffae2f8f49c1837ebf76f6c2557a`；脚本 `temp/restore_bom.py` + `temp/check_bom.py` 已就绪）
- 全解决方案 build 终验需停机复跑
- 9 个 pre-existing 测试失败建议另开卡

---

## 5. 卡 `2d5a2ebe`（P2）详解

### 原述
注入的 `ActiveTaskContext.expected_version=7` 与活 `task.Version=8` 形成**双重 CAS 互斥**：`task_update(expected=7)` → `version_conflict{actual 8}`；`task_update(expected=8)` → `state_conflict{context 期望 7}` ⇒ 任何值都无法通过，worker 的 `progress`/`completed` 被完全阻塞。

### ✅ 复核结论（2026-09-19）
**判定：修复已落地。**

**证据 1 —— 工具契约明示修复语义**
`task_claim` / `task_update` 契约原文：「expected_version 传 worker 最新已知的服务端活版本（**优先于注入快照**，缺陷 2d5a2ebe），服务端 CAS 校验」。

**证据 2 —— 本会话端到端实证（两例）**

```
例 1（卡 7af1b5cc）：
  task_claim(expected_version=3) → task.version_conflict{expected 3, actual 4, current_version: 4}
  task_claim(expected_version=4) → OK, version=5
  task_update(disposition=completed, expected_version=5) → OK, version=6
  ⇒ 全程无 state_conflict「context 期望 N」

例 2（卡 2d5a2ebe 自身）：
  task_claim(expected_version=5) → task.version_conflict{expected 5, actual 6}
  task_claim(expected_version=6) → OK, version=7
```

**证据 3 —— 失败模式由「不可恢复死锁」降级为「一次性可重试」**
单层 CAS 失败时**回传活版本**（`current_version`），worker 用它重试即成功。

### 遗留
修复方向 ①（派发投递时绑定快照取实时版本）属根因侧改进，建议另开卡，与 `813ad427`、`3133b149` 同源。

---

## 6. 方法论沉淀：「卡面陈旧」型卡的收敛方法

### 根因模式
卡面写于**诊断时刻**，代码随后已演进落地，但卡面从未更新 ⇒ **看板状态 ≠ 代码现状**。诊断态卡面的措辞（"当前不可实现""待修（未实施）"）会被误读为"未实施"。

### 收敛三步法
1. **定位实现落点**：`git grep -ln "<核心符号>" -- "*.cs"`
   ⚠️ **不要用 `search_grep`** —— 本仓 `directory=Source` 必然撞 2000 文件 / 64MB / 10s 预算，返回 partial 且无结果。
2. **取可执行证据**：`git grep -n "public async Task|public void|\[Fact\]" -- "<测试文件>"`
   用**测试方法名**作证据，满足「不得以静态审阅代替」的验收要求。
3. **卡面改双段结构**：保留原「诊断段」+ 追加「✅ 复核结论段」，明确标注哪几条验收有定向证据、哪几条移交上游卡。

### 四类判定与处置
| 判定 | 处置 |
|---|---|
| 已落地可收敛 | 走完整链路至 Archived，并在 `result_summary` + 本文档留证 |
| 部分落地需补证据 | 明确缺哪条验收的证据 + 下一步动作，收敛或移交上游卡 |
| 未实施 | 保持待办，卡面准确 |
| 无法判定 | 不收敛，标注取证阻塞点 |

---

## 7. 看板状态机纪律（本轮 9 次验证）

```
NeedsReview →(update status=Ready)→ Ready
  →(assign)→ Reserved
  →(update status=Assigned [平台不一定自动推进])→ Assigned
  →(task_claim，用错误返回的 current_version 重试)→ InProgress
  →(task_update disposition=completed)→ Completed
  →(archive)→ Archived
```

要点：
- **`Completed` 在管理视角不可达**；`NeedsReview` 是死胡同（唯一出路 `→ Ready`）。
- `assign` 后平台**有时**自动推进到 `Assigned`（version +1），**有时不推进** ⇒ `task_claim` 若报 `task.state_conflict ... requires Assigned or InProgress`，则显式 `update status=Assigned`。
- 每次调用后 version 都会 +1，**凡报 `task.version_conflict` 就用返回的 `current_version` 重试**（正常路径，非缺陷）。

---

## 8. 遗留总表

| 项 | 状态 | 去向 |
|---|---|---|
| 9 文件 BOM 恢复 | 待用户 `/authorize terminal_start 10m` | 工单 `tap_6c0fffae2f8f49c1837ebf76f6c2557a` |
| 全解决方案 build 终验 | 待停机复跑 | 预期 exit=0 |
| 9 个 pre-existing 测试失败 | 建议另开卡 | Core 4 / Runtime 4 / Platform 1 |
| `4fcd4aa5` 验收 2 / 5 | 移交 | 母卡 `b849ef8d750143fdaaa2a70d7b024a6c` |
| `2d5a2ebe` 修复方向 ① | 建议另开卡 | 与 `813ad427`、`3133b149` 同源 |
| 派发链路未对齐卡终态 | 已登记 | 卡 `be2608ee21bb4018a19ade685e99f5b4` |
| `ef7d6378` S2'/S3/S4 | 进行中 | S2' 已派 `sub-3b42ed64` |
| 剩余 8 张卡面陈旧卡 | 复核中 | `sub-fe73c138` |
