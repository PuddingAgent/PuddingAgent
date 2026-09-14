# Task-bound Goal 结算单向死胡同：canonical 通道被结算自身关闭

- 日期：2026-09-14（BJT）
- 关联卡：`3bd2a4b0ef5f4bff8f175fb7655927ad`（P0 统一 Scheduler 内核，当前 `Blocked`）·`e2c35d6eeae244c191ed508ccd85b6fe`（P0 单向闩锁）·`2b60040d6eb94db7800d0522698e3975`（P0 派发未注入 ActiveTask）
- 取证方式：生产库只读投影（`manage_tasks get` 事件链）+ 仓库源码 file:line 交叉核对 + 本轮 `task_claim` 实测错误体
- 结论等级：**代码级可复现**（非猜测），修复方案待实施

---

## 1. 现象（本轮实测，非历史）

### 1.1 事件链（`task_events`，UTC）

卡 `3bd2a4b0`（P0 Scheduler 内核），两轮几乎完全相同：

| seq | event | assignment | UTC 时间 | 距上一里程碑 |
|---|---|---|---|---|
| 42 | `task.reserved` | `dd4cc499…` | 09-13 12:28:08 | — |
| 43 | `task.assigned` | `dd4cc499…` | 09-13 12:28:08 | — |
| 44 | `task.accepted` | `dd4cc499…` | 09-13 12:28:16 | +8 s |
| 45 | `task.blocked`（`event_id=tgb-…-45`） | `dd4cc499…` | 09-13 12:29:50 | **+94 s** |
| 46 | `task.ready` | — | 09-13 19:12:18 | 人工 `manage_tasks resume` |
| 47 | `task.reserved` | `e69470da…` | 09-13 19:20:39 | +8 min（自动派发） |
| 48 | `task.assigned` | `e69470da…` | 09-13 19:20:39 | — |
| 49 | `task.accepted` | `e69470da…` | 09-13 19:21:06 | +27 s |
| 50 | `task.blocked`（`event_id=tgb-…-50`） | `e69470da…` | 09-13 19:22:54 | **+108 s** |

`tgb-{taskId}-{version}` 是 `GoalSettlementStore.AppendTaskEvent` 的固定格式（`Source/PuddingPlatform/Services/Goals/GoalSettlementStore.cs:1070`），
即**两次 Blocked 都是 Goal 结算写的**，不是 repair coordinator（后者签名 `tracker-legacy-blocked-*`，`TaskExecutionRepairCoordinator.cs:415`）。

### 1.2 当前 worker 侧行为（本轮）

```
task_claim(3bd2a4b0…, e69470da…, expectedVersion=48)
→ {"code":"task.active_context_missing",
   "message":"task_claim/task_update requires an Active Task Runtime Context; no task was dispatched to this run.",
   "context_rebuild":{"attempted":true,"stage":"lookup","outcome":"not_visible"}}
```

`task_get` → `task.not_found`；`task_list(mine)` 不含该卡；运行期仍以心跳形态执行该 Task-bound Goal 的迭代（本轮即一次 Goal 迭代）。
即：**卡在 Blocked，worker 既拿不到上下文、也无法 canonical 上报，Goal 迭代却仍在被投递执行。**

---

## 2. 根因链（file:line）

### 2.1 结算侧：非终态迭代结局被 fail-closed 升级为 Task 终态

`ConservativeGoalIterationVerifier.VerifyAsync`（`Source/PuddingPlatform/Services/Goals/ConservativeGoalIterationVerifier.cs:20-31`）：

```csharp
if (!capsule.EvidenceComplete || capsule.HasPendingExecutionFacts)
    decision = Blocked("evidence_incomplete", "Canonical execution evidence is incomplete or has pending facts.", capsule);
else if (!string.Equals(capsule.TerminalKind, "completed", …))
    decision = Blocked($"iteration_{capsule.TerminalKind}", "Goal Iteration ended as …; explicit recovery is required.", capsule);
```

`GoalSettlementStore`（`GoalSettlementStore.cs:809-895`）收到 `Blocked|NeedsUser|Unsafe` 后：

- `goal.Status = Failed`（task-bound）→ 该次执行的 Goal 终态、`binding.Status="terminal"`；
- `ReleaseReservation(..., "goal_blocked")`；
- Task 状态：**只有** `blockerCode == "task_completion_fact_missing"` 才走 `NeedsReview`（`:839-848`），其余一律 `Blocked`（`:849-857`）；
- 随后 `ReleaseAssignment(..., AssignmentAttemptStatus.Failed)`（`:872-881`，实现见 `:995-1011`）：
  `attempt.ReleasedAtUtc = now`、`task.ActiveAssignmentId = null`；
- 落事件 `task.blocked`（`AppendTaskEvent`，event_id `tgb-…`）。

### 2.2 服务端：canonical 上报硬依赖 active assignment（矛盾点）

`TaskAgentCommandService.ApplyDispositionAsync`（`Source/PuddingPlatform/Services/Tasks/TaskAgentCommandService.cs:317-326`）：

```csharp
if (task.ActiveAssignmentId != request.AssignmentId)
    throw new TaskStoreException(TaskErrorCode.AssignmentStale, …);
```

**结算把 `ActiveAssignmentId` 置空 ⇒ 归属 Agent 永远无法再对这张卡提交任何 disposition**（含唯一合法的恢复性 disposition `todo`）。
这不是策略分歧，而是两条已合并实现之间的**硬矛盾**：

> 状态机允许 `Blocked → Ready`（`TaskStateMachine.TryInterpretDisposition(Todo)`），
> 但没有任何存活的调用路径能在 `Blocked` 状态下满足 `ActiveAssignmentId == assignment_id`。

### 2.3 工具侧：恢复通道因此不可达

`TaskToolGuard.ValidateActiveTaskOrRebuildAsync`（`Source/PuddingRuntime/Services/TaskTools/TaskToolModels.cs:247-330`）反查重建要求：

1. `service.GetAsync(workspaceId, taskId, AgentInstanceId)` 非空（mine 过滤；assignment 为空即不可见）→ 本轮 `lookup/not_visible`；
2. `lookup.ActiveAssignment.AssignmentId == 入参`；
3. `assignment.AgentId == context.AgentInstanceId`；
4. 状态 ∈ `{InProgress, Blocked}`（`allowBlockedRecovery=true`）。

条件 1、2 在结算后**必然失败**（assignment 已释放、`ActiveAssignmentId=null`）。
因此卡 `813ad427` 上一轮放开的「Blocked 可恢复上报」在生产中**没有可达状态**，只在手工构造的 T12–T15 单测里成立。

### 2.4 并行的第二个单向口（上轮已登记，仍有效）

`TaskExecutionRepairCoordinator.cs:201-271`（`tracker-legacy-blocked-*`，`:415`）同样只写 Blocked、置空 `ActiveAssignmentId`、释放 attempt，**不写** Binding/GoalRun/Outbox re-arm。
即：`tgb-*`（Goal 结算）与 `tracker-legacy-blocked-*`（修复器）是**两个独立的 Blocked 单向闩锁**。

### 2.5 尚存的一个未闭合探针

Goal 迭代运行时的 `ActiveTask` 为 null，说明派发元数据没到位：
`AgentInvocationDispatchFactory.BuildActiveTask`（`Source/PuddingRuntime/Services/AgentInvocationDispatchFactory.cs:134-160`）要求 `invocation.Metadata["task_id"]` + `["assignment_id"]`；
全仓唯一对 Goal 迭代写这两个键的地方是 `GoalContinuationWorker.cs:137-160`，且被 `taskBinding != null && task != null && taskBinding.AssignmentId != null` 三重门禁。
**待查（下一步只读探针）**：`task_goal_bindings.assignment_id` 是否为空 / `message_deliveries.metadata` 是否丢失。当前只能确认「链路末端为空」，不能确认断点在哪一环。

### 2.6 本轮补充静态事实（修正 §2.5 的候选范围）

**事实 A：结算只清 task，不清 binding ⇒ binding.AssignmentId 是陈旧残留。**
`ReleaseAssignment`（`GoalSettlementStore.cs:990-1011`）的门禁是 `binding.AssignmentId` 非空，作用对象只有 `attempt` 与 `task.ActiveAssignmentId`：

```csharp
if (attempt is not null && attempt.ReleasedAtUtc is null) { attempt.Status = terminalStatus; attempt.ReleasedAtUtc = now; … }
if (!string.Equals(task.ActiveAssignmentId, binding.AssignmentId, …)) return false;
task.ActiveAssignmentId = null;   // 仅此处；binding.AssignmentId 未被改写
```

推论：结算之后 `binding.AssignmentId != null` 仍成立，故 `GoalContinuationWorker.cs:137` 的三重门禁**不会**因「assignment 被清空」而落空——它会继续把**已释放的 attempt id** 写进 metadata。
⇒ 「ActiveTask=null」**不能**再用「门禁判空」解释，断点大概率在**下游投递/投影**环节，或落在 `MessageDeliveryDispatcher.cs:405-407` 的 `claimed.Metadata.Count > 0 ?` 短路分支上（该分支会**整体丢弃**事件侧 metadata）。这也解释了为何 §2.5 只能观察到链路末端为空。

**事实 B：派发链末端已有测试覆盖，F3 缺口只在中间一段。**
`Source/PuddingRuntimeTests/Services/AgentExecutionWakeupActiveTaskPreservationTests.cs:139`（`CreateForWorkspaceAgentAsync_MetadataTaskKeys_BuildActiveTask`，测试 5）已锁定 `WorkspaceAgentInvocation.Metadata → dispatch.Request.ActiveTask` 的字段级映射。因此 F3 缺的测试不是末端，而是：

> `GoalContinuationWorker.DispatchOneAsync` 产出的 `SubmitTurnRequest.Metadata` → envelope/delivery 落库 → `MessageInboxItem.claimed.Metadata` → `effectiveMetadata` 这一段。

**事实 C：`ReleaseAssignment` 的残留语义还会污染既有探针。**
`tracker-legacy-blocked-*`（`TaskExecutionRepairCoordinator.cs:415`）与 `tgb-*` 的判定都读 binding/attempt；binding 上的陈旧 `AssignmentId` 可能让「已释放」被识别为「仍归属」。**实施 F1（保留 assignment）时必须同时给出这条残留的清理或对齐策略**，否则新旧语义叠加会产出第三类误判。

### 2.7 静态定稿（本轮，推翻 §2.6 的「投递丢包」主假设）

**事实 D：`ActiveTask` 在生产代码里只有一个构造点，且只有一条可达路径。**

- 唯一构造点：`AgentInvocationDispatchFactory.BuildActiveTask`（`AgentInvocationDispatchFactory.cs:130-160`），要求 metadata **同时**含 `task_id` 与 `assignment_id`，任一缺失即返回 `null`。
- 唯一生产调用点：`MessageDeliveryDispatcher.cs:665`（`grep CreateForWorkspaceAgentAsync` 在 `Source/PuddingRuntime` 仅命中 2 处，另一处是接口声明 `:28`）。

⇒ 只有**消息投递路径**（MessageDeliveries → inbox claim → dispatcher）能把 metadata 变成 `ActiveTask`。

**事实 E：canonical turn 通道结构上无法携带 `ActiveTask`。**

- `TurnExecutorAdapter.ExecuteAsync`（`TurnExecutorAdapter.cs:27-56`）逐字段拷贝 `TurnExecutionContext` 到 `RuntimeDispatchRequest`，拷贝了 `TaskPlanId/TaskNodeId/ParentTaskNodeId`，**没有 `ActiveTask` 赋值**。
- `TurnExecutionContext`（`Source/PuddingCore/Runtime/ITurnExecutor.cs:26-70`）**本身也没有 ActiveTask 字段**，只有 `TaskPlanId/TaskNodeId/ParentTaskNodeId`（init-only）。

⇒ 凡走 `ITurnExecutor` 的执行——**Goal 续跑正是这条**（`GoalContinuationWorker` → `AcceptBatchAsync` 生成 `ChatExecutionCommand`，不发 envelope/delivery）——`ActiveTask` 恒为 `null`，与 metadata 是否完整**无关**。

**事实 F：task 工具只从 dispatch request 取 ActiveTask。**

- `ToolInvocationService.cs:125: ActiveTask = request.ActiveTask`。

⇒ 事实 E 直接解释工具侧 `task.active_context_missing` + `context_rebuild{stage:"lookup", outcome:"not_visible"}`。

**修正后的因果闭环（取代 §2.5/§2.6 的末端假设）**

1. Task-bound Goal 迭代经 `AcceptBatchAsync` 落 **canonical turn**，命令行携带完整 task metadata（`ConversationAcceptanceStore.cs:144`）。
2. 该 turn 由 `ITurnExecutor` 执行 → `RuntimeDispatchRequest.ActiveTask == null`（事实 E）→ 工具层无归属（事实 F）。
3. 迭代无法 `task_update` 收口（`active_context_missing`）→ ~100 s 后非终态结束。
4. `ConservativeGoalIterationVerifier` → Blocked（`tgb-*`）→ 结算 `ReleaseAssignment`（清 `task.ActiveAssignmentId`）。
5. 通道关闭：`ApplyDispositionAsync` 归属校验无解 → 单向死胡同（§2.5）。

**事实 G（次要但真实，独立缺陷）：消息投递路径存在「事件侧 metadata 被丢弃」缺陷。**

- `MessageDeliveryDispatcher.cs:405-407`：`claimed.Metadata.Count > 0 ? claimed.Metadata : metadata ?? new()` —— claim 侧非空时**整体替换**而非合并。
- `MessageDeliveryEntity` **没有 metadata 列**（`MessageFabricStore.cs:62-78` 落库字段清单）；claim 侧 metadata 来自 `RoomMessages.MetadataJson`（`MessageFabricStore.cs:455,494` ← envelope `MessageRouter.cs:56` / `MessageSystem.cs:97`）。

⇒ envelope 与 room message 的 metadata 一旦不同步，事件侧补充键会被静默丢弃。登记为独立缺陷，**不作为本卡主因**。

---

## 3. 影响

1. **自动调度不可收敛**：每次「自动派发 → accepted → ~100 s → Blocked」循环都以人工 `resume` 收尾；`Task→Goal→Iteration` 链在第二次结算即断裂。
2. **卡 `3bd2a4b0` 的验收标准 #4（authoritative-single 连续跑 ≥10 个安全任务、Heartbeat 数 0）在当前语义下不可达**：恢复性结局一律升级为终态并关闭通道。
3. **Iteration 寿命异常短（94 s / 108 s）**：远低于一次真实 LLM 迭代，指向 `evidence_incomplete` 类「canonical 证据未落地即结算」的路径，本身是第二个独立缺陷（已在 §2.5 备注探针）。

---

## 4. 修复方案（按推荐顺序，含风险）

### F1（推荐，平台·结算语义）：恢复性结局不得关闭 canonical 通道

对 task-bound attempt，当且仅当 verdict 为**恢复性**结局（`evidence_incomplete` 或 `iteration_*` 中 kind ≠ completed）时：

- Task → `NeedsReview`（`:839-848` 已有同形分支，仅需扩展判定），保留 `BlockerKind/BlockerReason`；
- **不调用** `ReleaseAssignment`：保留 `ActiveAssignmentId`，使 `ApplyDispositionAsync` 的归属校验仍可通过，归属 Agent 可用 `todo`（→`Ready`）或 `needs_approval` 自行收口；
- Goal 仍置 `Failed`（保持 `(conversation, agent)` 单 active Goal 不变量，后续 `Resume/Requeue` 可新建 fenced attempt），但 binding 释放需与 assignment 保留对齐（避免 tracker 的 `legacy_*` 探针误判）；
- 非恢复性 verdict（`task_blocked`/`task_terminal_without_completion`/`Unsafe`/`evidence_incomplete` 之外的策略拒绝）**保持现状**（Blocked + 释放），维持 fail-closed。

风险：保留 assignment 会阻止同卡再派发（这是**期望**语义：归属者必须先收口）；需配套 staleness sweep（复用 repair coordinator 的 bounded scan）避免长期占用。中风险，需覆盖 tracker 回归。

### F2（必需，达成验收 #4）：恢复性结局走有界 re-arm 而非终态

在 F1 基础上，恢复性结局应触发**有界重试**（retry penalty + `nextEligibleAtUtc` 退避，复用 `TaskSchedulingCoordinator` intent），把「explicit recovery is required」降级为「同一 Goal 的下一轮 bounded iteration」。否则 #4 永远需要人。

风险：fail-closed 语义（ADR 明确要求 authoritative 决策落库失败必须 fail closed）必须区分「策略拒绝」与「迭代未完成」——F2 只放宽后者。中高风险，必须与 ADR 对齐后再改。

### F3（前置，运行时）：为 canonical turn 补 task 上下文传递位（§2.7 已修正定位）

§2.6 曾把 F3 定位为「补 metadata 断言」，§2.7 事实 E 证明**断言补不出通道**：`ITurnExecutor` 通道没有 `ActiveTask` 传递位，F1/F2 依赖的归属上下文在此路径上永不可得。修正为三步：

- **F3a（必需，运行时）**：为 `TurnExecutionContext` 增加任务上下文传递位（`ActiveTaskRuntimeContext? ActiveTask`，或结构化的 `TaskId/AssignmentId/ExpectedVersion` 三键），由 canonical turn 构建方从 `ChatExecutionCommandEntity.MetadataJson` 填充——`ExecutionCommandReader` 已在解析同一份 metadata，是天然复用点；并在 `TurnExecutorAdapter.cs:27-56` 透传至 `RuntimeDispatchRequest.ActiveTask`。判据与 `BuildActiveTask` 同源（`task_id`+`assignment_id` 双键缺失即 `null`，保持 fail-safe）。
- **F3b（可选）**：若要求 ActiveTask 随 turn 冻结，则同时进 journal anchor；`AgentExecutionService.cs:354` 的 wakeup 路径已支持 `anchor.ActiveTask` 透传，无需改动。
- **F3c（独立缺陷）**：`MessageDeliveryDispatcher` 的 metadata 由「整体替换」改为「合并」（event 侧补齐 claim 侧缺失键），登记单独卡，不阻塞本卡。

风险：F3a 涉及 `TurnExecutionContext` 这一 ABI 级共享契约，**新增字段前必须先统计构造点影响面**（FastPath / 子代理 / 测试夹具）；判据 fail-safe，低风险。F3c 需确认 envelope 与 room message metadata 是否设计上同源，避免掩盖真实丢失。

### 否决项

- 放宽工具层反查（例如允许「已释放 attempt 的最近归属」）：`ApplyDispositionAsync` 仍会 `AssignmentStale`，只会把失败点后移，且扩大伪造面。**否决**。
- 继续人工 `resume`：掩盖缺陷，且下一轮必然复现（上轮 94 s / 本轮 108 s 已证）。**否决**。

---

## 5. 验收（实施 F1+F3 时）

1. 结算单测：恢复性 verdict + task-bound → Task `NeedsReview`、`ActiveAssignmentId` 保留、attempt `ReleasedAtUtc == null`；非恢复性 verdict 行为不变（回归）。
2. 工具层 E2E：该状态下 `task_update(disposition=todo)` 成功（`NeedsReview → Ready`），`progress/completed` 仍 `state_conflict`（fail closed 不变）。
3. 派发链单测：Goal 续跑 envelope → `ActiveTask` 非空且 `task_id/assignment_id/expected_version` 与 binding 一致。
4. 生产复现验证：同一张 P0 卡连续 2 轮「派发→迭代→结算」后**不再**出现 `tgb-*` 的终态 Blocked，且 worker 每轮都能 `task_claim` 成功。
5. canonical turn 通道单测（§2.7 事实 E 的回归锁）：给定含 `task_id/assignment_id` 的 command metadata → 生成的 `RuntimeDispatchRequest.ActiveTask` 非空且 `workspace/task/assignment/expected_version` 与 binding 一致；缺失任一键 → `null`。

## 6. 未闭合问题（下一轮）

- ~~§2.5 断点定位（只读探针：`task_goal_bindings.assignment_id` / `message_deliveries.metadata`）~~ → **本轮已由静态定稿取代**（§2.7 事实 D/E/F）。DB 只读探针的剩余用途**仅**于确认 iteration terminal kind，不再用于 ActiveTask 断点。
- `TurnExecutionContext` 新增字段的构造点影响面统计（FastPath / 子代理 / 测试夹具 / journal anchor 兼容）。
- Iteration 寿命 94–108 s 的真实 terminal kind（`evidence_incomplete` vs `iteration_aborted`）——决定 F2 的放宽边界。
- `tgb-*` 与 `tracker-legacy-blocked-*` 两条闩锁的统一收口（卡 `e2c35d6e`）。

---

## 2.8 事实 H（2026-09-14 11:40 BJT 增补，本缺陷的关键收口证据）

**命令型执行路径的 metadata 已完整生成；缺的不是数据，而是「载体」。**

`GoalContinuationWorker.DispatchOneAsync`（`Source/PuddingPlatform/Services/Goals/GoalContinuationWorker.cs:137-158`）在构造 Goal 迭代命令时，
**已经写入完整的 ADR-072 §9.1 ActiveTask 键集**：

```csharp
metadata["origin"] = "task.auto";
metadata["task_id"] = task.TaskId;
metadata["assignment_id"] = taskBinding.AssignmentId;
metadata["expected_version"] = taskBinding.ExpectedTaskVersion?.ToString() ?? task.Version.ToString();
metadata["priority"] = task.Priority.ToString().ToLowerInvariant();
metadata["execution_window"] = task.ExecutionWindow switch { … };   // inherit|anytime|off_peak_only
metadata["dispatch_idempotency_key"] = taskBinding.IdempotencyKey ?? taskBinding.BindingId;
metadata["reservation_fencing_token"] = …;                          // 有值时
metadata[TaskPlanId / TaskPlanFingerprint / TaskNodeId / ParentTaskNodeId] = …;
```

这与**投递路径消费端** `AgentInvocationDispatchFactory.BuildActiveTask`（`Source/PuddingRuntime/Services/AgentInvocationDispatchFactory.cs:140-167`）读取的键**完全一致**
（`task_id` / `assignment_id` / `origin` / `priority` / `execution_window` / `expected_version` / `policy_version` / `dispatch_idempotency_key` / `reservation_fencing_token`，且 `task_id`+`assignment_id` 缺一即返回 null）。

**断点因此被精确框定为「命令路径缺 3 个载体」，而非「metadata 未生成」：**

| # | 位置 | 现状 | 缺口 |
|---|---|---|---|
| 1 | `ChatExecutionCommands.metadata_json` | 已含完整 ActiveTask 键 | — |
| 2 | `ExecutionCommandReader.MapAsync`（`Source/PuddingPlatform/Services/ExecutionCommandReader.cs:68-179`） | `:81` 已 `ParseMetadata(entity.MetadataJson)`，但只消费 workunit 白名单键（plan/fingerprint/node/parent_node），`return result with { WorkUnit = … }` | **丢弃** ActiveTask 键 |
| 3 | `ExecutionCommandRecord`（`Source/PuddingCore/Platform/IExecutionCommandReader.cs:4-20`） | 有 `WorkUnit`，**无** ActiveTask/Assignment 字段 | 无字段 |
| 4 | `TurnExecutionContext`（`Source/PuddingCore/Runtime/ITurnExecutor.cs:26-70`） | 有 `TaskPlanId`/`TaskNodeId`/`ParentTaskNodeId`，**无** `ActiveTask` | 无成员 |
| 5 | `TurnExecutorAdapter.cs:27-56` | 逐字段映射 `RuntimeDispatchRequest`，**未赋值 `ActiveTask`** | 未透传（而 `MessageContracts.cs:206` **已有** `RuntimeDispatchRequest.ActiveTask`） |
| 6 | 下游 | **已就绪**：`AgentExecutionService.cs:354 ActiveTask = anchor.ActiveTask`；`AgentExecutionService.Buffered.cs:1051 / :1596 / :1751`、`AgentExecutionService.Streaming.cs:1402` 均 `ActiveTask = request.ActiveTask` → `ToolInvocationService.cs:125` | — |

**结论（本缺陷可编码级收口）**：F3a 是**纯管道插入，不需要任何新语义**，共 5 个插入点（见 §7）。
且**下游 4 处赋值证明「载体补上即通」**——`ToolInvocationRequest.ActiveTask` 的唯一生产者就是 `request.ActiveTask`。

**可复现的三条断言**（供验收）
1. `grep ActiveTask Source/PuddingCore/Runtime/ITurnExecutor.cs` → 0 命中（无成员）。
2. `grep ActiveTask Source/PuddingRuntime/Services/TurnExecutorAdapter.cs` → 0 命中（未透传）。
3. `grep ActiveTask Source/PuddingCore/Platform/MessageContracts.cs` → `:206` 命中（目标字段已存在，无需新增契约）。

---

## 7 附录 A：F3a 实施规格（可编码级，逐点锚点）

> 目标：让 canonical 命令路径（Goal 迭代）携带 `ActiveTask`，使 `task_claim`/`task_update` 在**首次**执行期即可 canonical 收口，从源头阻断「~100 s 非终态 → `tgb-*` Blocked → 结算清 ActiveAssignmentId」。

### P1 `Source/PuddingCore/Runtime/ITurnExecutor.cs`
- 文件头 using 增加 `using PuddingCode.Tasks;`（`ActiveTaskRuntimeContext` 定义于 `Source/PuddingCore/Tasks/ActiveTaskRuntimeContext.cs:12`，同程序集，无循环依赖）。
- `TurnExecutionContext` 的 `{ get; init; }` 区（`ParentTaskNodeId` 之后、`UsageBudget` 之前）新增：
```csharp
/// <summary>ADR-072 §9.1/§9.2：派发链注入的 Active Task 上下文（canonical 命令路径同样必须携带）。</summary>
public ActiveTaskRuntimeContext? ActiveTask { get; init; }
```

### P2 `Source/PuddingCore/Platform/IExecutionCommandReader.cs` + 新增唯一映射器
- `ExecutionCommandRecord` 增加 `public ActiveTaskRuntimeContext? ActiveTask { get; init; }`（补 `using PuddingCode.Tasks;`）。
- 新增 `Source/PuddingCore/Tasks/ActiveTaskMetadata.cs`，提供**唯一**映射器
  `public static ActiveTaskRuntimeContext? TryBuild(string workspaceId, string agentId, IReadOnlyDictionary<string,string> metadata)`，
  语义与 `AgentInvocationDispatchFactory.BuildActiveTask:140-167` **逐键一致**：三别名（snake/camel/Pascal）、`task_id` 或 `assignment_id` 空 → `null`、`origin`/`priority`/`execution_window` 缺省 `string.Empty`、`DeliveryId` 不填。
- 随后把 `AgentInvocationDispatchFactory.BuildActiveTask` 改为**委托**该映射器（等价重构，必须保留原行为；若担心回归可后置到 P2b，但不得留在两个实现各自演化）。

### P3 `Source/PuddingPlatform/Services/ExecutionCommandReader.cs`
- `MapAsync` 已在 `:81` 取到 `metadata`；在**通过 fence 校验之后**的 `return result with { WorkUnit = … }`（`:161-179`）中追加：
```csharp
ActiveTask = ActiveTaskMetadata.TryBuild(entity.WorkspaceId, entity.AgentInstanceId, metadata),
```
- 约束：必须在 fence 校验**之后**（校验失败仍抛 `task_execution_fence_changed`，语义不得变化）；不得改动 `WorkUnit` 组装。

### P4 `Source/PuddingPlatform/Services/AgentChat/ExecutionRunCoordinator.cs:178`
- `new TurnExecutionContext(...) { ExecutionDeadlineUtc = …, TaskPlanId = …, TaskNodeId = …, ParentTaskNodeId = …, … }` 的 init 块追加：
```csharp
ActiveTask = command.ActiveTask,
```

### P5 `Source/PuddingRuntime/Services/TurnExecutorAdapter.cs:27-56`
- `new RuntimeDispatchRequest { … }` 追加 `ActiveTask = context.ActiveTask,`（字段已存在于 `MessageContracts.cs:206`，**不新增契约**）。

### 验收（T1–T4）
- **T1** Core 单测：`ActiveTaskMetadata.TryBuild` 三别名/缺键→null/空串缺省；`BuildActiveTask` 委托后行为不变。
- **T2** Runtime 单测：stub `IRuntimeAgentDispatcher` 捕获 `RuntimeDispatchRequest`，断言 `TurnExecutionContext.ActiveTask` → `request.ActiveTask` 原样透传（含 null 场景）。
- **T3** Platform 单测：binding/plan/task 全齐时 `ExecutionCommandRecord.ActiveTask` 字段与 metadata 一致；fence 失败仍抛 `task_execution_fence_changed`。
- **T4** 构建：`dotnet build Source/PuddingRuntime/PuddingRuntime.csproj` 与 `Source/PuddingPlatform/PuddingPlatform.csproj` 零错误；`dotnet test Source/PuddingRuntimeTests` 相关过滤集通过。

### 非目标 / 边界
- 只补载体。**不改** `GoalSettlementStore` / `TaskExecutionRepairCoordinator`（F1/F2，属卡 `e2c35d6e`）。
- **不改** `MessageDeliveryDispatcher.cs:405-407` 的 metadata「整体替换」语义（独立缺陷 G）。
- 不新增 metadata 键、不改现有键语义、不动 LLM 路由/预算逻辑。
- **遗留边界（已登记）**：`ExecutionCommandReader` 的 fence 会在 `binding.Status != "active"` 或 `task.ActiveAssignmentId != binding.AssignmentId`（即结算之后）抛 `task_execution_fence_changed` ⇒ F3a 只保证**首次迭代**可 canonical 收口；结算后的 stale-ActiveTask 仍由 F1 波形统一裁决。
