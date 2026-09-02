# Scheduler 夜间有效调度与 Execution 生命周期闭环代码级实施方案

> 状态：Proposed（诊断结论已确认；本文不代表源码、部署或产品验收完成）
>
> 日期：2026-09-01
>
> Canonical 总任务：`6f49d33e900c4e7e960c630fa7d7c2fb`（统一任务调度器：夜间吞吐端到端治理）
>
> 上位裁决：ADR-072、ADR-074
>
> 相关控制面设计：[任务调度器与 Goal 用户控制面设计](任务调度器与Goal用户控制面设计.md)

## 1. 目标和诊断基线

本方案处理的不是“后台 Worker 是否醒着”，而是调度器能否形成可核验的有效执行闭环。2026-08-31 晚间至 2026-09-01 早间的运行事实显示：

- `task_scheduler_intents` 有 2 条且均被标记 `done`，但 `task_scheduler_decisions` 为 0；
- 夜间没有自动 Assignment、Reservation、`TaskGoalBinding` 或 `GoalRun`；唯一完成项来自 `manual_smoke_verify`；
- 定时扫描持续看到 3 个 Agent（2 Idle、1 Busy），但候选为 0、启动为 0；
- 1 个 Agent 被旧 Assignment 长期占用。`TaskExecutionTracker` 只要看到 `execution_id`、`session_id` 或 Delivery 的 claimed execution 就永久判定 `legacy_execution_claimed/Healthy`，没有读取 canonical `execution_runs.status`；
- 看板存在多张 `assignment_execution_missing` / `delivery_terminal_without_execution` 阻塞卡，用户只能看到红色原因，缺少证据查看、恢复预检和批量恢复入口；
- 事件驱动 smoke 最终由手工 `run-now` 启动，Agent 却把它表述成自动派发 E2E 通过；同时模型轮次超过任务声明预算。

因此，本轮验收口径必须从“Intent 被消费、Delivery 被 ACK、日志出现扫描”升级为下面的 canonical 闭环：

```text
Trigger event
  -> durable SchedulerIntent
  -> task-scoped durable Decision/Outcome
  -> fenced Assignment + Reservation
  -> TaskGoalBinding + GoalRun
  -> canonical ExecutionRun
  -> verified settlement
  -> Task terminal + ownership release
```

任一中间 ACK、claimed id、普通聊天回复或 Agent 自述都不能单独算作有效自动调度。

## 2. 范围、非目标和不可破坏不变量

### 2.1 本轮范围

1. 旧 Task/Assignment/Delivery 与 canonical `ExecutionRun` 的终态对账和所有权释放；
2. 事件驱动 Coordinator 的 task-scoped 决策持久化、Intent 稳定结算与 staged mode 一致性；
3. 每轮扫描事实持久化、状态 API 和 Goodput 指标；
4. 看板中 Blocked 任务的证据、单卡恢复、批量恢复预检与操作；
5. 运行时预算硬门禁和真实自动派发验收卡；
6. 测试、升级、部署、回滚和两段式产品验收。

### 2.2 非目标

- 不新增第二套 Scheduler、Task 状态机或浏览器本地状态机；
- 不以 Heartbeat、普通 `task_instruction` 或人工 `run-now` 冒充自动派发；
- 不根据 `ExecutionRun=succeeded` 自动把 Task 标记 Completed；Task 完成仍需要 Task/Goal settlement 的显式证据；
- 不批量把 Blocked 卡直接改成 Completed，也不绕过 ETag、Task version、Assignment/Reservation fencing；
- 不在本文中清理 `D:\data`、重置运行库或宣称当前 Desktop/Core 已加载待实现代码。

### 2.3 不变量

1. 每个 Task 最多一个未释放 Assignment，每个 Agent 最多一个 active Reservation；
2. 所有 repair 必须在 Serializable 事务中重读 Task、Assignment、Binding、Reservation 和 Run，并校验 Task version/fencing；
3. 终态 Run 不再占用 Agent，但也不等价于 Task 成功；
4. Intent 只有在触发对象得到 durable 稳定 Outcome 后才能 `done`；
5. `authoritative`、`authoritative-single`、`authoritative-bounded` 共用同一前置门禁和启动链；
6. UI 命令只调用服务端 command API；批量动作逐卡使用最新 ETag，不形成数据库旁路；
7. 源码测试通过、外部部署完成、产品内功能 smoke 是三个独立门禁。

## 3. 目标状态机与稳定原因码

### 3.1 Legacy Assignment 对账状态

`TaskExecutionTracker` 在 legacy 分支中解析 execution claim，并用 `execution_runs` 的真实状态替代“有 id 即 Healthy”：

| 跟踪码 | 条件 | Tracker verdict | Repair |
|---|---|---|---|
| `legacy_execution_active` | 找到最新 Run，状态为 `leased/running`，且 lease/progress 未超时 | Healthy/Waiting | 无 |
| `legacy_execution_terminal_pending_settlement` | Run 已终态，但仍在 settlement grace 内 | Waiting | 等待 settlement |
| `legacy_execution_terminal_without_task_settlement` | Run 已终态、Task/Assignment 仍 active，且超过 grace | CleanupRequired | Block Task、释放 Assignment/Reservation |
| `legacy_execution_claim_orphaned` | claim 指向的 execution/run 不存在，且超过阈值 | CleanupRequired | Block Task、释放所有权 |
| `legacy_delivery_terminal_without_execution` | Delivery failed/dead-letter/cancelled 且无 Run | CleanupRequired | 复用现有 deterministic cleanup |
| `legacy_assignment_execution_missing` | Delivery delivered，超过阈值仍无 claim/Run | CleanupRequired | 复用现有 deterministic cleanup |

建议把 `ExecutionRun` 终态集中为一个纯函数，避免在 Tracker、Repair、Goal settlement 中各写一份字符串判断：

```csharp
internal static bool IsExecutionRunTerminal(string? status) =>
    status is "succeeded" or "failed" or "cancelled" or "lease_lost";
```

实际 wire 值必须以 `ExecutionRunCoordinator` 的当前写入集合为准补齐测试；未知值一律 `Inconsistent`，不按成功处理。

### 3.2 Scheduler Intent 稳定 Outcome

每个 Intent 至少落一个 task-scoped outcome：

| outcome | 语义 |
|---|---|
| `started` | 事务已创建新的 fenced Assignment/Reservation/Binding/Goal |
| `deferred` | 有稳定 deny/defer code 和 `nextEligibleAtUtc` |
| `denied` | 策略、能力、窗口或版本明确拒绝 |
| `ineligible` | 触发 Task 不在可评估状态或未 opt-in |
| `terminal` | Task 已终态，无需调度 |
| `noop` | 非 Task 级事件只造成 Availability 刷新，结果已持久化 |
| `failed` | 处理失败、仍可租约重试；达到阈值后进入 dead |

不允许“对全工作区跑一次 Evaluate，然后把本批所有 Intent 全部 Complete”，因为触发 Task 可能根本不在 evaluator 返回值中。

## 4. P0-A：Legacy Execution 终态对账与所有权释放

Canonical owner：

- `4ed930e7782c495aaa305294921db94c`：统一完成事实；
- `77883a50d4c8453cbd05c38ee1719f0e`：Tracker/Watchdog。

### 4.1 文件级修改

| 文件 | 修改 |
|---|---|
| `Source/PuddingPlatform/Services/Scheduling/TaskExecutionTracker.cs` | legacy 查询加入 claim 到 `ExecutionRunEntity` 的解析；按 Run 状态、lease、terminal 时间和 settlement grace 返回稳定 verdict/code |
| `Source/PuddingCore/Scheduling/TaskExecutionTrackingContracts.cs` | 如现有 Decision 不能表达 Run 证据，增加 `ExecutionRunId/ExecutionStatus/ExecutionTerminalAtUtc` 只读字段 |
| `Source/PuddingPlatform/Services/Scheduling/TaskExecutionRepairCoordinator.cs` | 新增 terminal-without-settlement/orphaned claim 分支；Serializable 重读并释放 active assignment/reservation；追加 causal TaskEvent |
| `Source/PuddingPlatform/Data/Entities/ExecutionRunEntity.cs` | 原则上不改表；优先复用 `status/completed_at/terminal_sequence` |
| `Source/PuddingPlatformTests/Services/Scheduling/TaskExecutionTrackerTests.cs` | 覆盖 running、succeeded、failed、cancelled、orphaned、grace、stale fence |

### 4.2 Tracker 查询步骤

1. 对 active legacy Assignment 读取最新 `TaskExecutionBinding` 与 Delivery；
2. claim 优先级固定为 `binding.ExecutionId` → `delivery.ClaimedByExecutionId` → 可证明关联的 command/session；
3. 用 claim 查 `execution_runs.run_id`；如历史实现写入的是 command id，则只允许用已冻结且有测试的第二索引查询，禁止模糊匹配；
4. 计算 `lastProgress = max(task/assignment/binding/delivery/run started/completed)`；
5. active Run 结合 lease/progress threshold 判 Healthy/Waiting/Stalled；
6. terminal Run 在 grace 内等待 settlement，超过 grace 转 CleanupRequired；
7. claim 不存在且超过阈值转 orphaned cleanup。

### 4.3 Repair 事务步骤

1. `BEGIN Serializable`；
2. 以 `workspaceId + taskId` 重读 Task，并确认 `ActiveAssignmentId == decision.AssignmentId`；
3. 重读 Assignment，要求 `ReleasedAtUtc == null`；
4. 重读 Binding/Delivery/Run，确认决定所引用事实未变化；
5. `succeeded` 但缺 Task settlement：Task 转 Blocked，`blockerKind=execution_terminal_without_task_settlement`；
6. `failed/cancelled/lease_lost`：Task 转 Blocked 或 Failed 必须遵循 `TaskStateMachine` 和现有 retry policy，写稳定 failure/blocker code；
7. Assignment 写终态并设置 `ReleasedAtUtc`，Task 清空 `ActiveAssignmentId`；有关联 active Reservation 时在同事务释放；
8. 追加 `TaskBlocked/TaskUpdated` causal event，`CausationId` 指向 Assignment，`CorrelationId` 指向 Run；
9. 提交后重建该 Agent Availability；事务冲突返回 no-op，不得套用旧 decision。

## 5. P0-B：事件驱动 Decision 与 Intent 可靠结算

Canonical owner：`3bd2a4b0ef5f4bff8f175fb7655927ad`。

### 5.1 当前断点

`TaskSchedulingCoordinator.ProcessOnceAsync` 当前调用全工作区 `EvaluateAsync` 和 `DispatchAsync`，不调用 `TaskSchedulerDecisionStore`；随后无条件把本批 Intent 全部 `CompleteAsync`。这解释了“Intent done 但 decisions=0”。

### 5.2 推荐合同

在 `ITaskAutoDispatchEvaluator` 增加 task-scoped 入口，或新增薄的 orchestrator：

```csharp
Task<IReadOnlyList<TaskAutoDispatchDecision>> EvaluateTasksAsync(
    string workspaceId,
    IReadOnlyCollection<string> taskIds,
    int candidateLimit,
    CancellationToken ct);
```

`TaskSchedulerDecisionStore` 增加 Intent 关联字段或独立 Outcome 表。开发期优先一次硬升级，不建立长期兼容层：

```sql
CREATE TABLE task_scheduler_intent_outcomes (
  intent_id TEXT PRIMARY KEY,
  workspace_id TEXT NOT NULL,
  task_id TEXT NULL,
  outcome TEXT NOT NULL,
  decision_id INTEGER NULL,
  scan_id TEXT NOT NULL,
  policy_revision INTEGER NOT NULL,
  options_hash TEXT NOT NULL,
  reason_code TEXT NULL,
  started_assignment_id TEXT NULL,
  created_at_utc TEXT NOT NULL,
  FOREIGN KEY(intent_id) REFERENCES task_scheduler_intents(intent_id)
);
```

### 5.3 Coordinator 单批步骤

1. Dequeue intents 后按 TaskId 去重；conversation/goal terminal 事件先重建相关 Agent Availability；
2. 对每个 TaskId 读取当前 Task；已终态或未 opt-in 也生成 `terminal/ineligible` outcome；
3. 调用 task-scoped evaluator，使用统一 `scanId=event-{workspace}-{batchId}`；
4. 在派发前持久化 candidate decision，保存 mode、score breakdown、reason、policy revision、options hash；
5. staged mode 上限使用 `TaskAutoDispatchOptions.EffectiveMaxStartsPerScan(current)`；
6. Starter 返回逐 Task 启动结果，而不只返回 count；把 Assignment/Goal id 写入 outcome；
7. 一个事务或等价幂等序列写 Outcome 后再 Complete 对应 Intent；
8. 写 outcome 失败时 Intent `FailAsync` 并保留 lease 重试；禁止吞错后 done；
9. 批内某个 Task 失败不应让已持久化 outcome 的其他 Intent 重复启动，重放依赖 Intent 主键和 starter fence 幂等。

### 5.4 决策持久化失败策略

周期 shadow scan 可在决策表写失败时继续评估并报警；事件驱动 authoritative 路径不能这样处理。它必须 fail closed：没有 durable decision/outcome，就不启动或不结算 Intent。两条路径共享 store，但失败策略由 trigger/mode 明确区分并有测试。

## 6. P1-A：Staged Mode 统一

Canonical owner：`3bd2a4b0ef5f4bff8f175fb7655927ad`。

当前 `TaskAutoDispatchScanRunner` 已使用 `NormalizeMode/IsAuthoritativeMode`，但 `TaskSchedulingCoordinator` 和 `TaskEventLedgerTailBridge` 仍只接受精确字符串 `authoritative`，造成 single/bounded 下周期扫描与事件驱动路径行为分裂。

实施步骤：

1. Bridge、Coordinator、Worker、ControlService 和 Starter 全部只调用 `NormalizeMode`、`IsShadowMode`、`IsAuthoritativeMode`、`EffectiveMaxStartsPerScan`；
2. `authoritative-single` 在周期和事件路径都强制 `maxStarts=1`；
3. `authoritative-bounded` 和兼容名称 `authoritative` 都使用配置上限；
4. disabled/paused 不入队、不 Dequeue、不启动；是否推进 ledger cursor 必须明确，推荐暂停时仍推进并保存“skipped due to paused”摘要，恢复由 recovery scan 补全当前 Task 状态；
5. 参数化测试覆盖五个 mode，并分别断言 Bridge enqueue、Coordinator consume、Starter count 和 Control 状态。

## 7. P1-B：Scan Run 持久化与 Goodput 状态 API

Canonical owner：

- `0b16740022f84b58a9532a87f1bc5509`：控制面与 Goodput；
- `06898d5dfe004c69ab6d5baf18b2674a`：遥测降噪。

### 7.1 新表

当前空扫描只有日志，重启后 `TaskSchedulerControlService.LastScan` 的内存快照丢失。新增一张工作区级摘要表：

```sql
CREATE TABLE task_scheduler_scan_runs (
  scan_id TEXT PRIMARY KEY,
  workspace_id TEXT NOT NULL,
  trigger TEXT NOT NULL,
  mode TEXT NOT NULL,
  policy_revision INTEGER NOT NULL,
  host_boot_id TEXT NOT NULL,
  status TEXT NOT NULL,
  started_at_utc TEXT NOT NULL,
  completed_at_utc TEXT NULL,
  duration_ms INTEGER NULL,
  availability_refreshed INTEGER NOT NULL DEFAULT 0,
  idle_agents INTEGER NOT NULL DEFAULT 0,
  busy_agents INTEGER NOT NULL DEFAULT 0,
  unknown_agents INTEGER NOT NULL DEFAULT 0,
  backlog INTEGER NOT NULL DEFAULT 0,
  candidates INTEGER NOT NULL DEFAULT 0,
  eligible INTEGER NOT NULL DEFAULT 0,
  started INTEGER NOT NULL DEFAULT 0,
  tracked INTEGER NOT NULL DEFAULT 0,
  repaired INTEGER NOT NULL DEFAULT 0,
  decision_codes_json TEXT NULL,
  repair_codes_json TEXT NULL,
  error_code TEXT NULL,
  error_summary TEXT NULL
);
CREATE INDEX IX_task_scheduler_scan_runs_workspace_started
  ON task_scheduler_scan_runs(workspace_id, started_at_utc DESC);
```

### 7.2 写入顺序

1. ScanRunner 开始时插入 `running`；
2. 成功时更新完整 summary 和 `succeeded`；候选为 0 也必须落行；
3. 异常时更新 `failed/error_code/error_summary` 后重新抛出；
4. 启动恢复把本 `host_boot_id` 之前仍 running 的记录标记 `abandoned`；
5. 只持久化汇总和稳定原因分布，具体 task decision 继续放在 decisions 表，避免双写全量明细。

### 7.3 API/DTO

扩展 `TaskSchedulerStatusSnapshot`：

- `lastPersistedScan`、`lastSuccessfulScanAtUtc`、`lastEffectiveDispatchAtUtc`；
- `effectiveDispatchCount24h`、`terminalTaskCount24h`、`blockedCountByReason`；
- `noCandidateReasons`；
- `runtimeLastScan` 仅作为当前进程正在执行的瞬态字段。

状态 API 必须从表读取最后一轮，不能只依赖内存。Goodput 至少同时报告“启动数”和“经 Verifier 结算的 Task 终态数”，避免把 started 当产出。

## 8. P1-C：Blocked 任务证据与恢复 UI

Logical 子任务：`6b3707d733ca45e5a509a91c3e2f3b4f`；详细交互基线复用[任务调度器与 Goal 用户控制面设计](任务调度器与Goal用户控制面设计.md)，本节只补 Blocked 管理闭环。

### 8.1 UI 能力

1. 看板筛选增加 `Blocked` 快捷 facet 和 blocker kind 分组；
2. TaskCard 红字只显示稳定 code + 一行摘要，点击打开详情，不在卡片堆叠长英文；
3. TaskDetailsDrawer 增加“因果证据”时间线：Assignment、Delivery、claim、ExecutionRun、Goal/Binding、最近 repair；
4. 单卡动作：`恢复到 Ready`、`重新排队`、`标记失败`、`取消`，只展示 `allowedTransitions` 允许的动作；
5. 批量动作只支持“预检 → 用户确认 → 逐卡 CAS 执行 → 汇总”，不提供批量完成；
6. 预检结果按 `recoverable/still_running/stale_conflict/manual_review` 分组，still running 项默认不勾选；
7. 412 冲突立即停止该卡并刷新，不自动拿新 ETag 重试用户旧意图。

### 8.2 服务端合同

优先复用现有 Task command API。为避免前端 N+1 拼证据，增加只读诊断端点：

```text
GET /api/workspaces/{workspaceId}/tasks/{taskId}/execution-diagnostics
POST /api/workspaces/{workspaceId}/tasks/recovery-preview
```

`execution-diagnostics` 只投影 canonical facts，不触发 repair。`recovery-preview` 接受 TaskId + observed version，返回可执行命令和阻断原因；真正修改仍逐卡调用既有 `resume/requeue/mark-failed/cancel` command。

### 8.3 前端落点

- `Source/PuddingPlatformAdmin/src/pages/workspace-tasks/TaskCard.tsx`
- `Source/PuddingPlatformAdmin/src/pages/workspace-tasks/TaskDetailsDrawer.tsx`
- `Source/PuddingPlatformAdmin/src/pages/workspace-tasks/TaskTable.tsx`
- `Source/PuddingPlatformAdmin/src/pages/workspace-tasks/index.tsx`
- 新增 `BlockedRecoveryDrawer.tsx` 和相邻测试
- `Source/PuddingPlatform/Controllers/Api/TaskController.cs` 或独立只读 diagnostics controller

## 9. P0-C：Runtime 预算与结果声明门禁

Logical 子任务：`c7dc8f5d490e4a38ab47b5a5a71b69e1`。

真实 smoke 显示任务写了“最多 5 次模型调用”，实际调用达到 10 次。预算不能只存在于任务自然语言。

实施步骤：

1. `TaskExecutionPlanCompiler` 把 acceptance 中的结构化 `maxModelRounds/maxToolCalls/maxDuration/maxCost` 写入 WorkUnit；自然语言数字不做隐式解析；
2. `ExecutionRunCoordinator` 和 Agent Loop 取 Agent、Goal、WorkUnit 三层最小预算；
3. 每次模型调用和工具调用前原子扣减；达到上限产生 canonical `budget_exhausted`，走 checkpoint/Blocked，不再继续调用；
4. Goal Verifier 对“自动调度 E2E 成功”这类声明要求引用 decision、assignment、binding、goal、execution、settlement 的实体 ID；缺任一证据返回 needs_changes/blocked；
5. UI 把“Agent 自述”与“系统验收 verdict”分开展示。

## 10. 施工顺序与逻辑子任务

Code Agent 必须按依赖顺序施工。每完成一个阶段都提交独立 commit，并在对应任务卡评论中登记：修改文件、测试命令、结果、commit、未验收边界。

| 顺序 | Canonical task | 工作包 | 前置 | 交付门禁 |
|---:|---|---|---|---|
| 1 | `4ed930e…` + `77883a50…` | Legacy Run 对账和 release | 无 | Tracker/Repair 故障注入通过，旧占用可释放 |
| 2 | `3bd2a4b0…` | task-scoped decision/outcome | 1 | Intent 不再无 decision 地 done |
| 3 | `3bd2a4b0…` | staged mode 统一 | 2 | single/bounded 周期与事件路径一致 |
| 4 | `0b167400…` + `06898d5d…` | scan-run 持久化和 Goodput API | 2 | 空扫描、失败扫描、重启后状态可查 |
| 5 | `6b3707d7…` | 证据、预检、单卡/批量恢复 | 1、4 | 用户能安全干预阻塞卡 |
| 6 | `c7dc8f5d…` | 预算门禁 + 无人工自动派发 smoke | 1–5 | 系统证据闭环，不接受 Agent 自述代替 |

## 11. 每个工作包的提交步骤

### 11.1 工作包 1：Tracker/Repair

1. 先补当前误判的红测试；
2. 建立统一 Run 状态分类纯函数；
3. 扩展 legacy 查询与 Decision 证据；
4. 实现 Serializable repair 和 causal event；
5. 验证 stale decision/fence 不修改新 Assignment；
6. 运行 Tracker、Repair、Availability 聚焦测试；
7. 提交，不夹带 UI/Coordinator 修改。

### 11.2 工作包 2：Decision/Intent

1. 先补“Intent done 但 decisions=0”的集成测试；
2. 添加 intent outcome schema/bootstrap/store；
3. 增加 task-scoped evaluator；
4. Starter 返回逐 Task 结果；
5. Coordinator 按 Intent 写 decision/outcome 后结算；
6. 加入 crash/replay/双 worker/部分失败测试；
7. 用完整 `PuddingApplicationInitializer` 测 schema 接线，测试不得手工调用 bootstrapper 掩盖组合根缺口。

### 11.3 工作包 3：Mode

1. 把所有精确 `mode == authoritative` 搜索列成基线；
2. 统一为 Options helper；
3. 参数化五 mode；
4. 断言 single 上限为 1，bounded 使用配置值；
5. 断言 disabled/paused 不产生新启动。

### 11.4 工作包 4：Scan/Goodput

1. 新增 schema/bootstrap/store；
2. ScanRunner 先写 running，finally 写 terminal；
3. 状态服务改为 DB + runtime overlay；
4. API/前端类型同步；
5. 补空扫描、异常、重启 abandoned、24h 聚合测试；
6. 保留原因汇总，删除/抑制重复 no-op 明细写入。

### 11.5 工作包 5：Blocked UI

1. 先实现 diagnostics/preview DTO 和 controller 测试；
2. TaskCard/Details 增加稳定原因和证据；
3. 增加 Blocked facet 和 selection；
4. 实现 recovery preview；
5. 逐卡调用 command，处理 412/422/部分失败；
6. 补组件、API mock、键盘操作和错误态测试；
7. production build 后再做浏览器 smoke。

### 11.6 工作包 6：预算和真实验收

1. 预算合同先落 Core，再贯通 Plan/Goal/Execution/Runtime；
2. 故障注入证明第 N+1 次调用不会发生；
3. 部署明确的新 Core/前端构建；
4. 创建一张 `autoDispatchEnabled=true` 的安全 Task，不调用 assign/run-now；
5. 记录 task.ready 时间，等待事件路径在目标 P95 内启动；
6. 查询并保存 decision/outcome、Assignment、Reservation、Binding、GoalRun、ExecutionRun、Task terminal、Evaluation；
7. 检查预算计数与声明一致；
8. 任一环缺失即判 smoke 失败，不人工补启动后继续宣称自动 E2E 成功。

## 12. 测试矩阵

### 12.1 后端聚焦测试

- `Source/PuddingPlatformTests/Services/Scheduling/TaskExecutionTrackerTests.cs`
- `Source/PuddingPlatformTests/Services/Scheduling/TaskSchedulingCoordinatorTests.cs`
- `Source/PuddingPlatformTests/Services/Scheduling/TaskSchedulerDecisionsAndStagedModeTests.cs`
- 新增 `TaskSchedulerScanRunStoreTests.cs`
- 新增 `TaskRecoveryDiagnosticsControllerTests.cs`

最低用例：

1. legacy claim + running Run 为 Healthy；
2. legacy claim + succeeded Run 超时为 CleanupRequired；
3. terminal Run 仍在 grace 为 Waiting；
4. orphaned claim 超时可 repair；
5. stale assignment/version/fence repair no-op；
6. 每个 Task Intent 都有 decision/outcome 后才 done；
7. decision store 失败时 authoritative Intent 不 done、不启动；
8. crash 后同 Intent 不重复 Assignment/Goal；
9. 五 mode 的 Bridge/Coordinator/Starter 一致；
10. 空 scan 也持久化；failed scan 可见，重启 running 转 abandoned；
11. recovery preview 不写状态；批量操作部分失败不回滚已成功的独立 Task；
12. 预算耗尽前后调用计数精确。

### 12.2 验证命令

实现者按实际项目文件执行定向测试；最终至少包括：

```powershell
dotnet test Source\PuddingPlatformTests\PuddingPlatformTests.csproj --no-restore --nologo
dotnet build PuddingRuntime --no-restore --nologo
pnpm --dir Source\PuddingPlatformAdmin test -- --run
pnpm --dir Source\PuddingPlatformAdmin build
git diff --check
```

如聚焦项目或前端脚本名称变化，以当前 `code_map.md`/package.json 为准，不得为了通过文档命令创建平行测试入口。

## 13. Definition of Done

### 13.1 源码完成

- 本文 6 个工作包的测试通过；
- 事件 Intent、Decision/Outcome、ScanRun、Assignment/Reservation/Binding/Goal/Execution/Task 能按 ID 追溯；
- `assignment_execution_missing` 等旧占用可由 deterministic repair 释放；
- 看板能查看证据并安全恢复；
- 预算由 Runtime 强制而不是靠提示词；
- 文档、ADR（如需新决策）和各层 `code_map.md` 同步更新。

### 13.2 产品验收完成

- 外部控制器部署明确 commit 的新 Desktop/Core/前端制品；
- 新产品会话中不使用 `assign`/`run-now`，完成至少 10 个安全任务的 single 模式连续自动派发；
- 再切 bounded，验证全局/每 Agent 上限、冲突和恢复；
- 任务全链路证据完整，Goodput 只统计经 settlement/Verifier 的终态；
- 连续 7 夜无永久 false-busy、无无 decision 的 done Intent、无重复 Goal/Assignment；
- UI 的暂停/恢复/扫描/修复、Goal 开始/暂停/恢复/停止和 Blocked 恢复完成真实点击 smoke。

## 14. 回滚与失败收敛

1. 任一异常先把 mode 切为 `shadow` 或 `disabled`，不删除已提交事实；
2. 暂停只阻止新 admission，不强杀未知副作用点的运行中 Tool Call；
3. ScanRun/Decision/Outcome 表是审计事实，回滚代码时保留；
4. repair 发现不一致就 fail closed 为 `manual_review`，不猜测完成状态；
5. UI 批量操作发现 412 后刷新该 Task 并要求用户重新确认；
6. 产品 smoke 失败保留 Task/Goal/Run 证据，创建新的验收尝试，不修改旧尝试伪造成功。
