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

### F3（前置，运行时）：Goal 迭代派发必须端到端携带 task/assignment 元数据

`GoalContinuationWorker.cs:137-160` → `MessageEnvelope.Metadata` → `MessageDeliveryDispatcher.effectiveMetadata`（`MessageDeliveryDispatcher.cs:405-407`）→ `AgentInvocationDispatchFactory.BuildActiveTask`。
任一环丢失即 `ActiveTask=null`。F1/F2 都依赖此链路可用（否则归属者仍无上下文）。**先补单测**：以 Goal 续跑 envelope 为输入断言 `BuildActiveTask` 非空；再补投递层断言。

风险：低（补测试 + 断言，不改语义）。

### 否决项

- 放宽工具层反查（例如允许「已释放 attempt 的最近归属」）：`ApplyDispositionAsync` 仍会 `AssignmentStale`，只会把失败点后移，且扩大伪造面。**否决**。
- 继续人工 `resume`：掩盖缺陷，且下一轮必然复现（上轮 94 s / 本轮 108 s 已证）。**否决**。

---

## 5. 验收（实施 F1+F3 时）

1. 结算单测：恢复性 verdict + task-bound → Task `NeedsReview`、`ActiveAssignmentId` 保留、attempt `ReleasedAtUtc == null`；非恢复性 verdict 行为不变（回归）。
2. 工具层 E2E：该状态下 `task_update(disposition=todo)` 成功（`NeedsReview → Ready`），`progress/completed` 仍 `state_conflict`（fail closed 不变）。
3. 派发链单测：Goal 续跑 envelope → `ActiveTask` 非空且 `task_id/assignment_id/expected_version` 与 binding 一致。
4. 生产复现验证：同一张 P0 卡连续 2 轮「派发→迭代→结算」后**不再**出现 `tgb-*` 的终态 Blocked，且 worker 每轮都能 `task_claim` 成功。

## 6. 未闭合问题（下一轮）

- §2.5 断点定位（只读探针：`task_goal_bindings.assignment_id` / `message_deliveries.metadata`）。
- Iteration 寿命 94–108 s 的真实 terminal kind（`evidence_incomplete` vs `iteration_aborted`）——决定 F2 的放宽边界。
- `tgb-*` 与 `tracker-legacy-blocked-*` 两条闩锁的统一收口（卡 `e2c35d6e`）。
