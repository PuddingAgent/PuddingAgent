# Scheduler 只读勘查证据（r1）

- Observed HEAD: `2bc81fcc2a521c91c9a91aa3cd12789f09f04d83`
  - 取证方式：`.git/HEAD` = `ref: refs/heads/master`；`.git/refs/heads/master` = 上述 SHA。
    **本次无 shell/terminal 工具，未执行 `git rev-parse HEAD`**；未排除 packed-refs 覆盖情形。
- 时间：2026-09-20（宿主日期）｜范围：`Source/**`、`Docs/**`、`**/appsettings*.json`、`default-data/**`（只读）
- 工具：file_read / file_search / search_grep / code_outline。无 DB 访问、无构建、无 git CLI。
- 本文件为**证据记录**，不含设计建议；未验证到的一律标 `NOT_FOUND`。
- 前置提醒：`Source/PuddingAgent/appsettings.json` 的 `LastWriteTimeUtc = 2026-09-20T00:27:10Z`（今日被改写），`code_map.md` 今日 09:28 亦更新 ⇒ 本勘查对应**当前工作区**，非任务卡撰写时刻的仓库状态。

---

## Q1. TaskAutoDispatchEvaluator 是否存在？候选 status 过滤集合？

**结论**：存在。候选查询硬编码 `status IN (Ready, Deferred)`，**Backlog 被排除**；另有 `auto_dispatch_enabled = 1` 与「容器母卡」排除条件。

**证据**：`Source/PuddingCore/Scheduling/TaskAutoDispatchContracts.cs:81`（`ITaskAutoDispatchEvaluator`）；`Source/PuddingPlatform/Services/Scheduling/TaskAutoDispatchEvaluator.cs:167`（`public sealed class TaskAutoDispatchEvaluator(`）；`:196`（SQL status 过滤）；`:242-243`（EF 同口径）。

**关键片段**（`TaskAutoDispatchEvaluator.cs:195-197`）

```sql
WHERE workspace_id = {workspaceId}
  AND status IN ({(int)WorkspaceTaskStatus.Ready}, {(int)WorkspaceTaskStatus.Deferred})
  AND auto_dispatch_enabled = 1
```

**关键片段**（`TaskAutoDispatchEvaluator.cs:242-245`）

```csharp
&& (entity.Status == WorkspaceTaskStatus.Ready
    || entity.Status == WorkspaceTaskStatus.Deferred)
&& entity.AutoDispatchEnabled
&& !db.WorkspaceTasks.Any(child => child.WorkspaceId == entity.WorkspaceId
    && child.ParentTaskId == entity.TaskId)
```

Backlog 只能先经 `TaskAutoDispatchScanRunner.cs:276-286` `PromoteBacklogAsync` → `TaskBacklogRefinementStore.cs:68`（`Status = WorkspaceTaskStatus.Ready`）才可能进候选。

---

## Q2. 候选是否要求 `PreferredAgentId` 非空？

**结论**：**不要求**。候选过滤无任何 `preferred_agent_id` 条件；该字段只承载「硬亲和排序 / 整卡 Deferred」。任务卡 ALREADY_KNOWN ② 被**推翻**。

**证据**：`TaskAutoDispatchEvaluator.cs:190-205`（SQL）与 `:238-246`（EF）无 preferred 条件；`:355-356` 仅取快照与布尔标记。

**关键片段**（`TaskAutoDispatchEvaluator.cs:355-360`）

```csharp
var preferredAgentId = task.PreferredAgentId;
var hasPreferred = !string.IsNullOrWhiteSpace(preferredAgentId);
var preferredIndex = hasPreferred
    ? Array.FindIndex(routedAgents,
        item => string.Equals(item.Agent.AgentId, preferredAgentId, StringComparison.Ordinal))
    : -1;
```

空 preferred 的真实后果：跳过 `preferred_busy` 硬亲和分支（`:390-397`），走通用评分路由；仅当 `routedAgents.Length == 0` 才 Denied（`:371-384`，码 `no_compatible_agent` / `preferred_agent_unavailable_or_incompatible`）。

---

## Q3. TaskAutoDispatchWorker / bounded recovery scan / 原子启动

**结论**：Worker 存在且**已有 bounded recovery scan**（trigger = `recovery_scan`）。原子启动**不叫** `StartGoalFromTask`（该名仅为命令 record），真正入口是 `TaskAutoDispatchStarter` → `TaskGoalDispatchTransactionStore.StartAsync`，**在 Serializable 事务内**提交。

**证据**：`Source/PuddingPlatform/Services/Scheduling/TaskAutoDispatchWorker.cs:12`（类）、`:25`（启动期 abandoned 恢复）、`:72-75`（recovery scan）；`TaskAutoDispatchStarter.cs:45`（类）、`:127`（原子启动调用）；`Source/PuddingPlatform/Services/Goals/TaskGoalDispatchTransactionStore.cs:47`（Serizable 事务）；`Source/PuddingCore/Goals/TaskBoundGoalContracts.cs:28`（`StartGoalFromTaskCommand`）。

**关键片段**（`TaskAutoDispatchWorker.cs:72-75`）

```csharp
await control.RunScanAsync(
    workspaceId,
    "recovery_scan",
    allowWhenPaused: false,
    stoppingToken);
```

**关键片段**（`TaskGoalDispatchTransactionStore.cs:45-47`）

```csharp
await using var db = await dbFactory.CreateDbContextAsync(ct);
await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
```

类注释 `TaskGoalDispatchTransactionStore.cs:1-5` 自述 "The only authoritative Task -> Goal startup writer… no network, model, tool or message send occurs inside the transaction"。

---

## Q4. TaskDispatcher 现在处理什么？入口在哪？

**结论**：存在，为 **TB-05 手工派发** Hosted Service：消费 `task_dispatch_outbox` pending → fence → Message Fabric 发送 → 回写 delivery/binding → `Reserved→Assigned`。「仅处理手工 outbox」**成立**：全仓唯一写入方是手工 Assign（`Origin=TaskInstructionEnvelope.OriginTaskManual`）。**自动派发不经它**（走 `TaskAutoDispatchStarter → TaskGoalDispatchTransactionStore`）。

**证据**：`Source/PuddingPlatform/Services/Tasks/TaskDispatcher.cs:34`（类）、`:56`（ExecuteAsync）、`:85`（ProcessOnceAsync）、`:166`（DispatchOneAsync）；`TaskDispatchOutboxStore.cs:45`；`TaskDispatchSchemaBootstrapper.cs:21`（建表）；`TaskCommandService.cs:361`（**唯一**写入点）。

**关键片段**（`TaskDispatcher.cs:88-92`）

```csharp
var store = scope.ServiceProvider.GetRequiredService<TaskDispatchOutboxStore>();
var messages = scope.ServiceProvider.GetRequiredService<IMessageSystem>();
await store.RecoverPendingOutboxAsync(now, ct);
var pending = await store.PeekPendingOutboxAsync(now, ct);
```

**关键片段**（`TaskCommandService.cs:358-361`）

```csharp
// ── TB-05：同事务追加 DispatchOutbox（不变量 #6，外部发送不在此发生，不变量 #7）──
var idempotencyKey = TaskDispatchIds.BuildIdempotencyKey(taskId, attempt.AttemptId);
db.TaskDispatchOutbox.Add(new TaskDispatchOutboxEntity
```

同处 `:348` 标注 `Origin = TaskInstructionEnvelope.OriginTaskManual`。

---

## Q5. `TaskAutoDispatch` / `TaskBoundGoals` / `GoalRuns` / `Continuation` 默认启用值

**结论**：**代码默认全关**；但仓库内实际 `appsettings.json` 已把四类全部打开。不存在名为 `Continuation` 的独立配置节——它体现为 `GoalRuns:ContinuationEnabled`。

**代码默认值（权威）**

| 配置节 | 字段 | 默认 | 证据 |
|---|---|---|---|
| `TaskAutoDispatch` | `Enabled` | `false`（注释 "Default false: no background scans"） | `TaskAutoDispatchEvaluator.cs:17` |
| `TaskAutoDispatch` | `Mode` | `"shadow"` | `TaskAutoDispatchEvaluator.cs:35` |
| `TaskAutoDispatch` | `EventDrivenEnabled` | `false` | `TaskAutoDispatchEvaluator.cs:87` |
| `TaskAutoDispatch` | `MaxStartsPerScan` / `ScanInterval` | `2` / `00:05:00` | 同文件 `:74` / `:65` |
| `TaskBoundGoals` | `Enabled` | `false` | `Source/PuddingPlatform/Services/Scheduling/TaskBoundGoalOptions.cs:12` |
| `TaskBoundGoals` | `GoalIterationBudget` / `ReservationLease` | `32` / `2h` | 同文件 `:14-15` |
| `GoalRuns` | `Enabled` / `ContinuationEnabled` | `false` / `false` | `Source/PuddingCore/Goals/GoalRunOptions.cs:16` / `:25` |

**实际配置文件值** —— `Source/PuddingAgent/appsettings.json`：`:16` `"GoalRuns"`、`:17` `"Enabled": true`、`:19` `"ContinuationEnabled": true`、`:26` `"TaskBoundGoals"`、`:27` `"Enabled": true`、`:31` `"TaskAutoDispatch"`、`:33` `"EventDrivenEnabled": true`、`:34` `"Enabled": true`、`:35` `"Mode": "authoritative"`。

**`default-data/config` 检查**：`Source/PuddingHost/default-data/config/system.json`（29 行）**不含**任一调度节（对 `Source/PuddingHost` 全目录 grep 四个关键字 = 0 匹配）。

**绑定与优先级**：`Source/PuddingHost/Extensions/PuddingServiceCollectionExtensions.Platform.cs:206-218`（三节各自 `Bind(GetSection(...))`）；`Source/PuddingHost/Hosting/PuddingApplicationHost.cs:47` 加 `appsettings.json` → `:61` 加 `dataPaths.SystemConfigFile("system.json")`（**后加载覆盖前者**）；运行时 `Source/PuddingAgent/bin/Debug/net10.0/data/config/system.json`（20 行）亦无调度节 ⇒ 生效值来自 `appsettings.json`（true / authoritative）。

---

## Q6. TaskSchedulingCoordinator / scheduler_outbox / task_scheduler_intent_outcomes

**结论**：`TaskSchedulingCoordinator` **已存在**（事件驱动层已落地）；`task_scheduler_intent_outcomes` 表与 store 存在；`scheduler_outbox` **NOT_FOUND**——同角色持久化由 `task_scheduler_intents` 承担，手工侧另有 `task_dispatch_outbox`。

**证据**：`TaskSchedulingCoordinator.cs:27`（类）、`:43`（BackgroundService）、`:50`、`:77`（ProcessOnceAsync）、`:148`（ProcessBatchAsync）；`TaskSchedulerIntentOutcomeSchemaBootstrapper.cs:23`（建表）；`TaskSchedulerIntentOutcomeStore.cs:30`（record）/`:46`（interface）；`TaskSchedulerIntentSchemaBootstrapper.cs:20`（建表）；`scheduler_outbox` 在 `Source/**/*.cs` 为 **0 匹配**。

**关键片段**（`TaskSchedulingCoordinator.cs:46-50`）

```csharp
if (current.Enabled
    && current.EventDrivenEnabled
    && TaskAutoDispatchOptions.IsAuthoritativeMode(current.Mode))
    await ProcessOnceAsync(stoppingToken);
```

**关键片段**（`TaskSchedulerIntentSchemaBootstrapper.cs:38`）

```sql
CREATE UNIQUE INDEX IF NOT EXISTS UX_task_scheduler_intents_source_event
  ON task_scheduler_intents(source, source_event_id);
```

同层已存在类型：`TaskSchedulerDecisionStore.cs:19`、`TaskSchedulerScanRunStore.cs:78`、`TaskEventLedgerTailBridge.cs:22`、`TaskExecutionTracker.cs:16`、`TaskExecutionRepairCoordinator.cs:19`、`TaskSchedulerControlService.cs:18`。

---

## Q7. NormalizeMode / IsAuthoritativeMode / EffectiveMaxStartsPerScan

**结论**：三者均已存在，定义集中在 **`TaskAutoDispatchOptions`（文件 `TaskAutoDispatchEvaluator.cs`）**，不在 Coordinator 内；调用点覆盖扫描器、协调器、控制面、Goal 启动门与事件桥。

**定义**：`TaskAutoDispatchEvaluator.cs:38`（`NormalizeMode`）、`:41`（`IsDisabledMode`）、`:44`（`IsShadowMode`）、`:48`（`IsAuthoritativeMode`）、`:54`（`EffectiveMaxStartsPerScan`）。

**关键片段**（`TaskAutoDispatchEvaluator.cs:48-49`）

```csharp
public static bool IsAuthoritativeMode(string? mode)
    => NormalizeMode(mode) is "authoritative" or "authoritative-single" or "authoritative-bounded";
```

**关键片段**（`TaskAutoDispatchEvaluator.cs:56-60`）

```csharp
var configured = Math.Clamp(options.MaxStartsPerScan, 1, 32);
return string.Equals(NormalizeMode(options.Mode), "authoritative-single", StringComparison.Ordinal)
    ? Math.Min(configured, 1)
    : configured;
```

**调用点**：`TaskAutoDispatchEvaluator.cs:107, :143`｜`TaskAutoDispatchScanRunner.cs:39, :40, :41, :153`｜`TaskEventLedgerTailBridge.cs:116`｜`TaskSchedulerControlService.cs:71, :75, :82, :188, :279`｜`TaskSchedulingCoordinator.cs:49, :82, :157, :254, :377, :379`｜`Source/PuddingPlatform/Services/Goals/TaskGoalLaunchService.cs:129`｜测试 `TaskSchedulerDecisionsAndStagedModeTests.cs:97-107`、`TaskSchedulingCoordinatorTests.cs:289, :315`。

---

## Q8. PuddingApplicationInitializer 与建表接线入口

**结论**：存在。类在 `Source/PuddingHost/Hosting/PuddingApplicationInitializer.cs:31`，唯一入口 `InitializeAsync` 在 `:33`；调度 bootstrap 已接线于 `:61-65`。新增一张表 = 新建 `XxxSchemaBootstrapper.EnsureCreatedAsync` 并**在该方法内追加一行**（同时同步 wiring 断言测试）。

**证据**：`PuddingApplicationInitializer.cs:31`（类）、`:33`（方法）；调用方 `Source/PuddingHost/Hosting/PuddingApplicationHost.cs:281`（定义）、`:286`（调用）。

**关键片段**（`PuddingApplicationInitializer.cs:45, 61-65`）

```csharp
await platformDb.Database.EnsureCreatedAsync(cancellationToken);
...
await TaskSchedulingSchemaBootstrapper.EnsureCreatedAsync(platformDb, schemaLogger, cancellationToken);
await TaskSchedulerIntentSchemaBootstrapper.EnsureCreatedAsync(platformDb, schemaLogger, cancellationToken);
await TaskSchedulerIntentOutcomeSchemaBootstrapper.EnsureCreatedAsync(platformDb, schemaLogger, cancellationToken);
await TaskSchedulerDecisionSchemaBootstrapper.EnsureCreatedAsync(platformDb, schemaLogger, cancellationToken);
await TaskSchedulerScanRunSchemaBootstrapper.EnsureCreatedAsync(platformDb, schemaLogger, cancellationToken);
```

相邻 Task 族：`:56` `TaskDispatchSchemaBootstrapper`、`:57` `WorkspaceTaskSchemaBootstrapper`、`:58` `TaskPlanningSchemaBootstrapper`、`:59` `GoalSchemaBootstrapper`。
**登记点（准确方法名与行位）**：`PuddingApplicationInitializer.InitializeAsync`（`PuddingApplicationInitializer.cs:33`）内 `:45`–`:68` 的 bootstrap 序列末尾追加一行；wiring 断言见 `Source/PuddingPlatformTests/Services/Scheduling/TaskSchedulerIntentOutcomeStoreTests.cs:118-119`（源码字符串须包含 `TaskSchedulerIntentOutcomeSchemaBootstrapper.EnsureCreatedAsync`）。

---

## Q9. 设计文档存在性与节行号

**结论**：存在：`Docs/Features/Scheduler夜间有效调度与Execution生命周期闭环代码级实施方案.md`，**总行数 464**（file_read meta `lines=464`）。下表为**标题所在行号**。

**§5–§6**：`:147 ## 5. P0-B：事件驱动 Decision 与 Intent 可靠结算` ｜ `:151 ### 5.1 当前断点` ｜ `:155 ### 5.2 推荐合同` ｜ `:186 ### 5.3 Coordinator 单批步骤` ｜ `:198 ### 5.4 决策持久化失败策略` ｜ `:202 ## 6. P1-A：Staged Mode 统一`

**§11.2–§11.3**：`:350 ### 11.2 工作包 2：Decision/Intent` ｜ `:360 ### 11.3 工作包 3：Mode`（相邻 `:340 ### 11.1 工作包 1：Tracker/Repair`、`:368 ### 11.4 工作包 4：Scan/Goodput`）

**§12**：`:398 ## 12. 测试矩阵` ｜ `:400 ### 12.1 后端聚焦测试` ｜ `:423 ### 12.2 验证命令`（相邻 `:437 ## 13. Definition of Done`、`:457 ## 14. 回滚与失败收敛`）

---

## Q10. Availability / Reservation / Lease / Fence / Epoch 的持久化类型或表

**结论**：**五类全部已有持久化落点**。不存在名为 `task_reservations` 的表——等价物是 `agent_execution_reservations`。

| 概念 | 落点 | file:line |
|---|---|---|
| Availability | 表 `agent_availability_projection` | `TaskSchedulingSchemaBootstrapper.cs:16` |
| Reservation | 表 `agent_execution_reservations` | `TaskSchedulingSchemaBootstrapper.cs:41` |
| 依赖边 | 表 `task_dependencies` | `TaskSchedulingSchemaBootstrapper.cs:63` |
| Lease（reservation 租约） | `agent_execution_reservations.lease_until_utc` | `TaskSchedulingSchemaBootstrapper.cs:49` |
| Lease（intent / continuation） | `IntentLease`；`GoalRuns:ContinuationLeaseDuration` | `TaskAutoDispatchEvaluator.cs`（Options）；`GoalRunOptions.cs:27` |
| Fence（reservation） | `agent_execution_reservations.fencing_token`（自增 PK） | `TaskSchedulingSchemaBootstrapper.cs:42` |
| Fence（消费/比对） | `FencingToken` | `AgentExecutionReservationStore.cs:79, :93, :121, :135, :149, :161, :211`；`ExecutionCommandReader.cs:129` |
| Fence（编排层） | `agent_orchestration.fencing_token` | `AgentOrchestrationSchemaBootstrapper.cs:105`；`SqliteAgentOrchestrationStore.cs:1037` |
| Epoch | `goal_runs.activation_epoch`（默认 1） | `Source/PuddingPlatform/Data/Entities/GoalRunEntity.cs:55-56` |
| Epoch（校验） | `StaleEpoch` | `Source/PuddingPlatform/Services/ConversationAcceptanceStore.cs:413, :419-421` |

**关键片段**（`TaskSchedulingSchemaBootstrapper.cs:41-49`）

```sql
CREATE TABLE IF NOT EXISTS agent_execution_reservations (
    fencing_token   INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    reservation_id  TEXT    NOT NULL,
    ...
    status          TEXT    NOT NULL DEFAULT 'active',
    lease_until_utc TEXT    NOT NULL,
```

`task_reservations`：**NOT_FOUND**（无该表名/类型）。

---

## 附：任务卡特别验证项 —— 「task_type 与执行计划生成」判定逻辑

**结论**：判定**不是** `task_type == "implementation"` 散点特判，而是集中在纯编译器 `TaskExecutionPlanCompiler`：`task_type` → 路由表 `TaskAutoDispatch:TaskTypeRoutes` → WorkUnit 种类序列。`implementation` 映射到 5 个 kind，正好产出 **5 步执行计划**，这解释了本卡为何能生成 5 步计划。

**证据**：`Source/PuddingPlatform/Services/Scheduling/TaskExecutionPlanCompiler.cs:17-18`（白名单）、`:20`/`:26`（`TryCompile`）、`:116`（`TryResolveKinds`）、`:120-128`（kind 映射）、`:139`（`NormalizeTaskType`）；调用点 `TaskAutoDispatchEvaluator.cs:319-320`、`TaskGoalDispatchTransactionStore.cs:95`。

**关键片段**（`TaskExecutionPlanCompiler.cs:17-18`）

```csharp
private const string SupportedTaskTypes =
    "implementation, operations, deployment, test, research, review, documentation, general";
```

**关键片段**（`TaskExecutionPlanCompiler.cs:120-123`）

```csharp
kinds = NormalizeTaskType(taskType) switch
{
    "implementation" or "operations" or "deployment" =>
        [TaskWorkUnitKind.Explore, TaskWorkUnitKind.Plan, TaskWorkUnitKind.Change,
            TaskWorkUnitKind.Test, TaskWorkUnitKind.Review],
```

**关键片段**（`TaskAutoDispatchEvaluator.cs:319-320`）

```csharp
_options.TaskTypeRoutes.TryGetValue(task.TaskType, out var typeRoute);
if (!TaskExecutionPlanCompiler.TryCompile(task, typeRoute, out var executionPlan, out var planCode))
```

不支持类型稳定码：`TaskExecutionPlanCompiler.cs:43-46`（`execution_plan_task_type_unsupported`）。路由表实参见 `Source/PuddingAgent/appsettings.json:39-62`（`implementation` 要求 `cap-file-write` + `cap-shell`，`AllowedRoles: ["Service"]`）。事务内二次编译指纹门：`TaskGoalDispatchTransactionStore.cs:95` → `TaskBoundGoalStartCodes.PlanChanged`（`TaskBoundGoalContracts.cs:17` = `execution_plan_changed`）。

---

## NOT_FOUND / 存疑清单

1. **`scheduler_outbox`** — 全仓 `Source/**/*.cs` 0 匹配，**不存在**（同角色为 `task_scheduler_intents`；手工侧为 `task_dispatch_outbox`）。
2. **独立 `Continuation` 配置节** — `SectionName = "Continuation` 0 匹配，**不存在**。
3. **`task_reservations` 表/类型** — 不存在；等价物 `agent_execution_reservations`。
4. **`StartGoalFromTask` 方法** — 不存在；只有 record `StartGoalFromTaskCommand`（`TaskBoundGoalContracts.cs:28`）与其 `StartAsync` 服务（`TaskGoalDispatchTransactionStore.cs:39`）。
5. **生产库「四类表均为 0」** — **无法验证**（本环境无 DB 查询通道），只能给表名与建表语句。
6. **`git rev-parse HEAD`** — **未执行**（无 shell）；HEAD 取自 `.git/HEAD` + `.git/refs/heads/master`。
7. **`appsettings.{Environment}.json`** — 未发现（`Source/PuddingHost` 下 `appsettings` 检索 0 匹配）；`bin/**` 同名副本未逐份比对。
