# Task-bound Goal 与 Agent 状态感知自动派发代码级施工计划

> 状态：设计定稿，待实现与验收；不表示代码已交付
> 日期：2026-08-21
> 权威决策：[ADR-074 Goal 持久目标、自主续行与自动压缩](../07架构/89ADR-074Goal持久目标自主续行与自动压缩ADR.md)
> 任务领域边界：[ADR-072 工作区 TODO、峰谷 Auto 派发与定时任务第一阶段](../07架构/86ADR-072工作区TODO峰谷Auto派发与定时任务第一阶段ADR.md)
> Goal 完整设计：[Goal 持久目标、自主续行与自动压缩完整设计方案](Goal持久目标自主续行与自动压缩完整设计方案.md)
> 本轮边界：只形成代码级施工计划，未修改源码、配置、数据库或运行数据

## 1. 施工结论

目标链不是“晚上定时给 Agent 发一条消息”，而是：

```text
Task 进入 Ready/Deferred
  + Agent 的持久 Availability 投影变为 Idle
  + Task 选择的执行窗口当前允许
      -> TaskGoalDispatchCoordinator 原子预留 Task + Agent
      -> StartGoalFromTask 原子建立 TaskGoalBinding + GoalRun + GoalOutbox
      -> GoalContinuationWorker 进入现有 Conversation 可靠执行链
      -> Turn settle -> Goal Verifier -> continue/terminal
      -> Goal/Task 双 CAS 提交终态并释放 Reservation
```

施工顺序是强依赖链：

1. 先完成 ADR-074 GoalRun 持久化和 event-driven continuation；
2. 再建立可持久 Agent Availability Sensor；
3. 再建立 provider/model 价格时段 Resolver；
4. 最后启用 TaskGoalDispatchCoordinator 和低峰窗口触发；
5. Heartbeat 只保留原有周期检查用途，不参与 Task 启动、Goal 续行或终态判定。

## 2. 冻结不变量

1. 一个自动 Task 最多有一个非终态 GoalRun，一个 Task-bound GoalRun 只绑定一个 Task/Assignment。
2. `TaskAutoDispatch.Enabled` 只能在 `GoalRuns.Enabled && TaskBoundGoals.Enabled` 时开启；否则 Host 启动验证 fail fast。
3. `Task.executionWindow` 是任务偏好权威；不创建 `work-policy.json`。
4. 当前低价时段由 Agent 有效 provider/model 路由与现有 LLM 配置中的价格时段共同决定；未知时 fail closed。
5. Agent `idle` 由持久事实保守投影；重启后初值是 `unknown`，不从进程内默认值推导。
6. Task-bound Goal 不进入通用 Message Delivery 的多消息批合并。
7. Agent 自然语言“完成”、Delivery ACK 和 Goal `DONE` 都只是输入事实，不是 Task Completed 命令。
8. Task/Assignment/Reservation/Binding/Goal/Outbox 在一个 SQLite 短事务中全成或全不成；LLM、Message Send、Tool 和网络不在事务内。
9. 用户消息、pause/cancel、approval、权限收紧和 Run Now override 优先于自动续行。
10. 正确性不依赖 signal、Timer、SSE、浏览器、Heartbeat 或进程内 Channel；这些只能降低延迟。

## 3. 现有代码基线与差距

| 现有位置/符号 | 已有能力 | 施工判断 |
|---|---|---|
| `Source/PuddingCore/Tasks/WorkspaceTaskModels.cs` | `TaskExecutionWindow` 及 Task 字段已存在 | 保留字段，不复制到新策略文件 |
| `Source/PuddingCore/Scheduling/IWorkAdmissionFence.cs` | Fence 输入已含 Task/priority/window | 扩展路由/价格档案快照和稳定 reason code |
| `Source/PuddingPlatform/Services/Tasks/TaskCommandService.cs` | 手工 Assign/RunNow 会创建 Assignment/Outbox | 手工闭环保留；Auto 不复用“先发消息”路径 |
| `Source/PuddingPlatform/Services/Tasks/TaskDispatcher.cs` | Hosted scan、lease、Fence stub、Message Fabric 派发 | 类职责限定为 Manual Delivery Dispatcher；新增 Auto Coordinator，不把两个状态机继续塞进同一类 |
| `Source/PuddingPlatform/Services/Tasks/ManualAlwaysAllowFence.cs` | 手工阶段恒 allow | 仅保留在明确 manual/test 组合；Auto 组合不得注册该实现 |
| `Source/PuddingCore/Abstractions/IAgentExecutionAvailabilityProvider.cs` | 查询空闲且带 cooldown | 保留兼容读 Adapter；Auto Claim 改读持久 Projection Store |
| `Source/PuddingRuntime/Services/Messaging/AgentExecutionStateRegistry.cs` | `ConcurrentDictionary` 运行时状态 | 只作 signal/cache；未知 Agent 不得默认 Idle |
| `Source/PuddingRuntime/Services/Messaging/MessageDeliveryDispatcher.cs` | 可额外 claim 9 条并合并正文 | 增加可靠 `ExecutionPurpose` 分流；Task-bound Goal 必须 one delivery/acceptance = one Task/Goal context |
| `Source/PuddingRuntime/Services/GoalMode/GoalModeService.cs` | `goal_queue.json` + 自然语言注入、成功发送后推进游标 | 不满足 ADR-074；待新 GoalRun 门禁通过后退役，不导入旧 JSON 为新权威 |
| `Source/PuddingRuntime/DependencyInjection.cs` | 旧 `IGoalModeService` 默认注册 | 继续默认关闭；切换时删除 Dispatcher 后置注入和旧 DI |
| `Source/PuddingPlatform/Services/ConversationAcceptanceStore.cs` | 可靠受理 Turn/Command/Event | 抽出可供 Goal Worker 调用的原子 synthetic acceptance primitive |
| `Source/PuddingPlatform/Services/AgentChat/ChatExecutionWorker.cs` | 执行命令 lease 和 Runtime 调用 | 继续作为唯一执行链，不增加 Task/Goal 内层循环 |
| `Source/PuddingRuntime/Services/TurnExecutorAdapter.cs` | Platform -> Runtime 执行交界 | 透传受信 Goal/Task context，不从 message metadata 自由字符串猜测 |

## 4. 目标组件与所有权

| 组件 | 所在项目 | 唯一职责 |
|---|---|---|
| `GoalCommandService` | PuddingPlatform | 处理用户 Goal command 和受信 `StartGoalFromTask` |
| `SqliteGoalStore` | PuddingPlatform | Goal/Iteration/Verification/Outbox 持久化、CAS、lease、恢复 |
| `TaskGoalDispatchTransactionStore` | PuddingPlatform | 在同一 `PlatformDbContext`/SQLite transaction 内跨 Task/Reservation/Binding/Goal/Outbox 提交 |
| `GoalContinuationWorker` | PuddingPlatform | claim `goal_outbox`，最终 admission，受理 synthetic Turn |
| `GoalSettlementWorker` | PuddingPlatform | 消费 Turn 终态事实，构造 verification job |
| `GoalVerificationExecutor` | PuddingRuntime | 只读证据验证，不写 Goal/Task |
| `GoalCoordinator` | PuddingPlatform | 用 deterministic gates + verdict 提交 Goal/Task 下一状态 |
| `AgentAvailabilityProjector` | PuddingPlatform | 从 canonical/runtime committed facts 生成持久 Agent 可用性投影 |
| `AgentAvailabilitySignalAdapter` | PuddingRuntime | 把实时 busy/idle 候选变化发给 Projector，不直接宣布可 Claim |
| `ProviderModelExecutionWindowResolver` | PuddingPlatform | 解析 Task 偏好 + Agent route + 价格时段 |
| `ExecutionWindowBoundaryScheduler` | PuddingPlatform | 为已计算 `nextEligibleAt` 的 Deferred Task 生成一次边界 signal |
| `TaskGoalDispatchCoordinator` | PuddingPlatform | 确定性选候选、三次 fence、调用原子启动事务 |
| `TaskGoalProjector` | PuddingPlatform | 投影 Task/Goal 联合状态和可解释的等待原因 |

Runtime 不成为 Task/Goal 事务写入者，Desktop 不承载任务调度业务逻辑，Admin 不维护第二套状态机。

## 5. Core 合同

### 5.1 Goal 合同

新建 `Source/PuddingCore/Goals/`：

- `GoalContracts.cs`：`GoalRunId`、`GoalPhase`、`GoalSourceKind(UserCommand|WorkspaceTask)`、snapshot、command result。
- `GoalStateMachine.cs`：合法 phase/activation/iteration 转换和预算不变量。
- `GoalPersistenceContracts.cs`：`IGoalStore`、CAS/lease/outbox request/result。
- `GoalVerificationContracts.cs`：Evidence Capsule、verdict、gate result。
- `TaskBoundGoalContracts.cs`：`StartGoalFromTaskCommand`、`TaskGoalBindingSnapshot`、Task/Goal 终态映射。
- `GoalEventTypes.cs`：稳定 canonical event name 和 required payload fields。

`StartGoalFromTaskCommand` 必填：

```text
workspaceId
taskId / expectedTaskVersion
assignmentId
agentId / conversationId
reservationId / reservationFencingToken
executionWindow
providerId / modelId / windowKey / windowProfileVersion / validUntilUtc
goalIterationBudget
requestedAtUtc
causationId / correlationId / idempotencyKey
```

`idempotencyKey` 固定为版本化规则，例如 `task-goal:{workspaceId}:{taskId}:{assignmentId}`，不使用随机消息文本哈希。

### 5.2 Availability 合同

新建 `Source/PuddingCore/Scheduling/AgentAvailabilityModels.cs` 和 `IAgentAvailabilityProjectionStore.cs`：

```text
AgentAvailabilityState = Unknown | Offline | Busy | Reserved |
                         WaitingApproval | Cooling | Idle | Frozen

AgentAvailabilitySnapshot:
  workspaceId, agentId, state, version,
  sourceCursor, observedAtUtc, validUntilUtc,
  activeConversationId?, activeTurnId?, activeExecutionId?,
  reservationId?, cooldownUntilUtc?, reasonCode
```

Store 提供 `GetAsync`、`ApplyFactAsync(expectedVersion, fact)`、`RebuildAsync`、`WatchChangedAsync`。不提供“调用方直接 SetIdle”的绕过方法。

### 5.3 Execution Window 合同

新建：

- `Source/PuddingCore/Scheduling/ExecutionWindowModels.cs`
- `Source/PuddingCore/Scheduling/IExecutionWindowResolver.cs`

`ExecutionWindowDecision` 至少包含：

```text
verdict = Allow | Defer | Unknown
code
providerId / modelId
windowKey / profileVersion
evaluatedAtUtc / validUntilUtc / nextEligibleAtUtc?
isUserOverride
```

修改 `IWorkAdmissionFence.cs`：

- 输入增加 route/window snapshot 和 Availability version；
- 输出保留 `validUntil`，便于 reserve/dispatch/accept 三次重检；
- 新增稳定码 `deferred_execution_window_unknown`、`deferred_window_closed`、`deferred_availability_stale`、`denied_goal_prerequisite_disabled`；
- 显式 `off_peak_only` 不被优先级自动越过。

### 5.4 受信执行上下文

修改 `Source/PuddingCore/Tasks/ActiveTaskRuntimeContext.cs` 或新建组合值 `TaskGoalRuntimeContext`：

```text
taskId, taskVersion, assignmentId, reservationFencingToken,
goalRunId, goalRevision, activationEpoch, iterationNumber,
acceptanceCriteria, remainingBudget, executionPurpose=task_goal
```

该对象来自服务端受理命令和数据库绑定，不允许从 Message `Content` 或模型输出反序列化获得。

## 6. SQLite 模型与事务

### 6.1 新表

`goal_runs`：

```text
goal_id PK
workspace_id, conversation_id, agent_id
source_kind, source_task_id?, source_assignment_id?
objective, acceptance_criteria_json
phase, revision, activation_epoch, aggregate_version, boot_id
max_iterations, accepted_iterations
tool/token/cost/time budget snapshot
policy_snapshot_json
created_at_utc, activated_at_utc?, terminal_at_utc?, updated_at_utc
```

`goal_iterations`：

```text
iteration_id PK, goal_id FK
activation_epoch, iteration_number
status, command_id?, turn_id?, run_id?
accepted_at_utc?, settled_at_utc?
result_code?, evidence_capsule_hash?
UNIQUE(goal_id, activation_epoch, iteration_number)
```

`goal_verifications`：

```text
verification_id PK, goal_id FK, iteration_id FK
attempt, status, verdict?, reason?, evidence_refs_json
verifier_route_snapshot_json, input_hash, error_id?
created_at_utc, completed_at_utc?
UNIQUE(iteration_id, attempt)
```

`goal_outbox`：

```text
outbox_id PK, goal_id FK
kind, activation_epoch, iteration_number
status, idempotency_key UNIQUE
available_at_utc, lease_owner?, lease_until_utc?
attempt_count, last_error_code?
created_at_utc, updated_at_utc
```

`task_goal_bindings`：

```text
binding_id PK
workspace_id, task_id, task_version_at_bind
assignment_id, agent_id, goal_id UNIQUE
reservation_id, reservation_fencing_token
status = active | completed | blocked | cancelled | failed | superseded
created_at_utc, terminal_at_utc?, updated_at_utc
```

索引/Constraint：

- partial unique：同一 `task_id` 最多一个 `status=active` binding；
- partial unique：同一 `assignment_id` 最多一个 active binding；
- `goal_runs(source_kind=WorkspaceTask)` 要求 task/assignment 非空；
- `accepted_iterations <= max_iterations <= 256`；
- outbox 的 goal/epoch/iteration 与幂等键唯一。

ADR-072 已规划的 `agent_availability_projection` 与 `agent_execution_reservations` 补齐实现，不在 Goal 目录复制第二套表。

### 6.2 原子启动事务

`TaskGoalDispatchTransactionStore.StartAsync` 在单一 serializable SQLite transaction 内按顺序：

1. 读取并 CAS Task `Ready/Deferred -> Reserved/Assigned`，校验 `expectedTaskVersion`；
2. 校验 Assignment 当前且 Agent 匹配；
3. 以 Agent 版本和 fencing token 创建/确认 Reservation；
4. 校验 Window decision 尚在 `validUntilUtc` 内；
5. 插入 active `task_goal_bindings`；
6. 插入 Task-bound `goal_runs`；
7. 插入第一个 `goal_outbox`；
8. 追加 Task events 和 canonical Conversation events；
9. commit 后发 signal。

任意 unique/CAS 失败返回稳定的 `LostRace/TaskChanged/AgentChanged/WindowExpired`，不抛出无限重试。

### 6.3 终态事务

`GoalCoordinator.ApplyVerdictAsync` 对 Task-bound Goal 同时校验：

```text
goal.aggregateVersion
goal.activationEpoch
task.version
binding.status == active
reservation.fencingToken
iteration/verification identity
```

映射：

- `complete` + Task acceptance gates pass -> Goal Completed + Task Completed + binding completed + release reservation；
- `blocked/needs_user` -> Goal Blocked/Paused + Task Blocked/NeedsReview + release auto reservation；
- `budget_exhausted` -> Goal BudgetExhausted + Task NeedsReview；
- `cancelled` -> 依用户起因一致提交 Task/Goal cancellation；
- transient execution failure -> 保留 Goal/Task 证据，根据有界策略创建同 Goal 下一 outbox 或进入 NeedsReview；
- stale result -> 只追加审计，不改变当前状态。

## 7. 事件与触发

### 7.1 新增事件

```text
task.goal.bind_requested
task.goal.bound
task.goal.binding_terminal
task.auto_dispatch.deferred
task.auto_dispatch.suppressed

agent.availability.changed
agent.reservation.created
agent.reservation.released
agent.reservation.expired

execution_window.opened
execution_window.closed
execution_window.profile_changed

goal.created / activated / paused / resumed / cancelled / completed
goal.iteration.accepted / started / settled
goal.verification.requested / completed / failed
goal.continuation.requested / dispatched / suppressed
```

所有跨域事件至少含 `eventId/workspaceId/agentId/taskId?/assignmentId?/goalId?/causationId/correlationId/occurredAtUtc`。Availability 事件还必须含 source cursor/version，旧事件不得覆盖新投影。

### 7.2 触发源

`TaskGoalDispatchCoordinator` 响应：

- `task.ready`；
- `task.auto_dispatch.deferred` 的 `nextEligibleAtUtc` 到期；
- `agent.availability.changed` 进入 Idle；
- `execution_window.opened/profile_changed`；
- `agent.reservation.expired/released`；
- 用户 Turn settle 后产生的 availability 变更；
- commit signal 丢失时的低频恢复扫描。

恢复扫描有结构边界：只扫描已有 Ready/Deferred Task、pending GoalOutbox 和 expired lease 索引，限制 batch/page，不全表 COUNT，不遍历 Agent 目录。

## 8. 代码文件施工矩阵

### 8.1 PuddingCore

| 文件 | 操作 | 关键改动 |
|---|---|---|
| `Source/PuddingCore/Goals/GoalContracts.cs` | 新增 | Goal ID/phase/source/snapshot/command result |
| `Source/PuddingCore/Goals/GoalStateMachine.cs` | 新增 | phase/epoch/iteration/budget 纯状态机 |
| `Source/PuddingCore/Goals/GoalPersistenceContracts.cs` | 新增 | Store/CAS/outbox/lease 契约 |
| `Source/PuddingCore/Goals/GoalVerificationContracts.cs` | 新增 | Evidence Capsule 与 verifier verdict |
| `Source/PuddingCore/Goals/TaskBoundGoalContracts.cs` | 新增 | `StartGoalFromTask` 和 binding/result mapping |
| `Source/PuddingCore/Goals/GoalEventTypes.cs` | 新增 | 事件名和 payload 字段 |
| `Source/PuddingCore/Scheduling/AgentAvailabilityModels.cs` | 新增 | 持久 Availability 状态/快照/事实 |
| `Source/PuddingCore/Scheduling/IAgentAvailabilityProjectionStore.cs` | 新增 | query/apply/rebuild/watch |
| `Source/PuddingCore/Scheduling/ExecutionWindowModels.cs` | 新增 | 价格时段快照和 decision |
| `Source/PuddingCore/Scheduling/IExecutionWindowResolver.cs` | 新增 | 纯解析契约 |
| `Source/PuddingCore/Scheduling/IWorkAdmissionFence.cs` | 修改 | route/window/availability version 和 reason code |
| `Source/PuddingCore/Platform/ConversationEventContracts.cs` | 修改 | Goal/TaskGoal source/event payload |
| `Source/PuddingCore/Tools/ToolAuthorization.cs` | 修改 | `/goal` grammar；内部 Task command 不暴露为 slash command |
| `Source/PuddingCore/Configuration/PuddingConfigModels.cs` | 修改 | provider/model 可选价格时段字段 |

### 8.2 PuddingPlatform

| 文件 | 操作 | 关键改动 |
|---|---|---|
| `Data/Entities/GoalRunEntity.cs` 等 5 个 entity | 新增 | GoalRun/Iteration/Verification/Outbox/TaskGoalBinding 映射 |
| `Data/Entities/AgentAvailabilityProjectionEntity.cs` | 新增 | Availability 持久投影 |
| `Data/Entities/AgentExecutionReservationEntity.cs` | 新增 | Agent 唯一 auto reservation/fencing token |
| `Data/PlatformDbContext.cs` | 修改 | DbSet、partial unique index、check constraint |
| `Services/Goals/GoalSchemaBootstrapper.cs` | 新增 | schema/index/version 启动校验 |
| `Services/Goals/SqliteGoalStore.cs` | 新增 | Goal CAS/outbox lease/recovery |
| `Services/Goals/GoalCommandService.cs` | 新增 | 用户 Goal command |
| `Services/Goals/TaskBoundGoalCommandHandler.cs` | 新增 | 仅接受受信 `StartGoalFromTask` |
| `Services/Goals/TaskGoalDispatchTransactionStore.cs` | 新增 | 跨 Task/Reservation/Binding/Goal/Outbox 单事务 |
| `Services/Goals/GoalContinuationWorker.cs` | 新增 | durable claim -> synthetic acceptance |
| `Services/Goals/GoalSettlementWorker.cs` | 新增 | terminal event -> verification job |
| `Services/Goals/GoalCoordinator.cs` | 新增 | deterministic gates + verdict + dual CAS |
| `Services/Goals/GoalRestartReconciler.cs` | 新增 | boot fence，active -> paused/disarmed |
| `Services/Scheduling/AgentAvailabilityProjector.cs` | 新增 | committed facts -> persistent projection |
| `Services/Scheduling/AgentAvailabilityProjectionStore.cs` | 新增 | CAS/TTL/rebuild |
| `Services/Scheduling/AgentExecutionReservationStore.cs` | 新增 | TryReserve/Renew/Release/Expire |
| `Services/Scheduling/ProviderModelExecutionWindowResolver.cs` | 新增 | provider/model route + 价格时段 |
| `Services/Scheduling/ExecutionWindowBoundaryScheduler.cs` | 新增 | 有界 nextEligible boundary signal |
| `Services/Tasks/TaskGoalDispatchCoordinator.cs` | 新增 | candidate/fence/reserve/start goal |
| `Services/Tasks/TaskDispatcher.cs` | 修改 | 明确仅 manual delivery；不承担 Auto Goal |
| `Services/Tasks/TaskCommandService.cs` | 修改 | Auto 不生成普通 Task instruction outbox；手工语义保留 |
| `Services/Conversation/SystemCommandHandler.cs` | 修改 | 委托 `IGoalCommandService` |
| `Services/ConversationAcceptanceStore.cs` | 修改 | 可幂等 synthetic Goal acceptance primitive |
| `Controllers/Api/GoalCommandsController.cs` | 新增 | Goal 结构化命令 |
| `Controllers/Api/GoalQueriesController.cs` | 新增 | Goal/iteration/verification/binding 查询 |
| `Controllers/Api/ExecutionWindowDiagnosticsController.cs` | 新增 | 只读 evaluate，无第二份策略 PUT |
| `Services/LlmProviderFileService.cs` | 修改 | 价格时段 schema 验证、round-trip、原子写 |

### 8.3 PuddingRuntime

| 文件 | 操作 | 关键改动 |
|---|---|---|
| `Services/Goals/GoalContextContributor.cs` | 新增 | 受信 Goal/Task 动态 context layer |
| `Services/Goals/GoalVerificationExecutor.cs` | 新增 | 只读 verifier route 和 strict schema |
| `Services/Goals/GoalEvidenceSanitizer.cs` | 新增 | 有界 capsule、artifact refs、secret 防护 |
| `Services/Messaging/MessageDeliveryDispatcher.cs` | 修改 | `ExecutionPurpose=task_goal/goal` 时不 batch；删除 settle 后旧 GoalMode 注入 |
| `Services/Messaging/AgentExecutionStateRegistry.cs` | 修改 | 未知不默认 Idle；发布候选事实/signal |
| `Services/Messaging/DefaultAgentExecutionAvailabilityProvider.cs` | 修改 | 改为读持久 projection 的兼容 Adapter |
| `Services/TurnExecutorAdapter.cs` | 修改 | 透传 `TaskGoalRuntimeContext` |
| `Services/ContextPipeline.cs` | 修改 | 增动态 goal-state/task-state layer，不破坏稳定 prefix |
| `Services/GoalMode/*` | 退役 | 新 GoalRun 切换后删除旧队列服务/选项/模型 |
| `DependencyInjection.cs` | 修改 | 注册新 Runtime Goal 服务，移除旧 `IGoalModeService` |

### 8.4 PuddingHost 与 Admin

| 项目/文件 | 关键改动 |
|---|---|
| `Source/PuddingHost/Extensions/PuddingServiceCollectionExtensions.Platform.cs` | 注册 Goal stores/workers、Availability projector、Window resolver、Coordinator；校验 flag 依赖 |
| `Tests/PuddingHost.Tests/Hosting/PuddingApplicationHostCompositionTests.cs` | ValidateOnBuild 覆盖 enabled/disabled/shadow 组合，确保 Hosted Service 不漏注册 |
| `Source/PuddingPlatformAdmin/src/pages/chat/components/GoalBanner.tsx` | 显示用户/Task-bound source、iteration、budget、pause/cancel |
| `Source/PuddingPlatformAdmin/src/pages/workspace-tasks/TaskDetailsDrawer.tsx` | 显示 Goal binding、等待窗口/Agent 原因、证据和深链 |
| `Source/PuddingPlatformAdmin/src/pages/workspace-tasks/ExecutionWindowInspector.tsx` | 显示 Task 偏好、route/profile/version/nextEligible，只读评估 |
| `Source/PuddingPlatformAdmin/src/services/platform/api.ts` | Goal/binding/availability/window DTO 和 API |

PuddingDesktop 不增任务业务代码；它通过现有 WebView 消费 Admin/Core 投影。

## 9. 施工卡与依赖

### P0-1 Goal 合同与旧语义隔离

依赖：无。

- 增加 Core Goal/TaskBoundGoal 合同和纯状态机。
- 为 `/goal` parser 建立命令测试；`StartGoalFromTask` 不走用户 slash parser。
- 将旧 `GoalModeService` 标注为 LegacyInjection，继续默认关闭，不增字段或功能。

出口：状态机/256/epoch/CAS 属性测试通过；普通聊天不变。

### P0-2 Goal SQLite 事实与命令

依赖：P0-1。

- 建 Goal 4 表 + binding 表、constraint 和 schema bootstrapper。
- 实现 Goal Store、GoalCommandService、idempotency、snapshot/query/events。
- 实现 restart reconciler：新 boot 将 active Goal disarm/pause。

出口：create/status/pause/resume/cancel 可重启重放；重复 request 不重复建 Goal。

### P0-3 Durable continuation 与 synthetic acceptance

依赖：P0-2。

- 实现 outbox claim/lease/fencing/signal/recovery。
- 抽取 ConversationAcceptanceStore 原子受理 primitive。
- 端到端走 `ChatExecutionWorker -> TurnExecutorAdapter -> AgentExecutionService`。
- 用户消息优先，pause/cancel/stop 使旧 epoch 失效。

出口：`heartbeat=0` 连续 20 个 iteration；故障点不丢不重；无第 257 个 accepted iteration。

### P0-4 Verifier 与 Goal/Task 终态门禁

依赖：P0-3。

- 实现 settlement worker、evidence capsule、deterministic gates 和只读 verifier。
- Agent `DONE` 只建 proposal；Coordinator 是唯一 Goal 终态写入者。
- 先完成非 Task Goal 验收，再开放 Task-bound Goal。

出口：ADR-074 G0–G3 通过，`GoalRuns.Enabled` 可单独灰度。

### P1-1 Availability Sensor

依赖：Task/Conversation/Runtime canonical facts 可读；可与 P0-2–P0-4 代码并行，但不能先启用 Auto。

- 建 projection/reservation 表与 store。
- 从 active Turn/Tool/SubAgent、approval、user queue、cooldown、lifecycle 事实重建。
- 把 Runtime registry 改为低延迟事实发布器，不当权威。
- 先 shadow 比对 old/new availability，记录 false-idle/false-busy。

出口：重启后 unknown -> rebuild；所有负状态均不 Claim；直接用户消息可抢占 Reservation。

### P1-2 Execution Window Resolver

依赖：Core 配置合同。

- 扩展现有 provider/model 配置的可选价格时段。
- 实现 Windows/IANA timezone、DST/跨日/边界、effectiveAt/profileVersion。
- Agent route 未知、profile 缺失和无效配置 fail closed。
- 只读 Evaluate API 和 Admin Inspector。

出口：相同快照确定性一致；`off_peak_only` 精确返回 next eligible；没有 `work-policy.json`。

### P1-3 Task-bound Goal 原子启动

依赖：P0-4、P1-1、P1-2。

- 实现 binding entity/store、`TaskBoundGoalCommandHandler`、跨域事务。
- 实现 Task/Goal 双 CAS 终态映射。
- Runtime 透传 `TaskGoalRuntimeContext`。
- MessageDeliveryDispatcher 排除 task_goal/goal 批处理。

出口：手工调用 `StartGoalFromTask` 可完成一项 Task；所有崩溃点全成或全不成；无重复 binding/Goal。

### P1-4 Event-driven Dispatch Coordinator

依赖：P1-3。

- 确定性候选顺序：Task priority -> due/notBefore -> readyAt -> sortOrder -> taskId；Agent 按 preferred/capability/idleSince/agentId。
- reserve/dispatch/accept 三次 Fence，每次重校验 route/window/availability version。
- 注册事件 signal 与有界恢复扫描。
- 先 evaluate-only，再单 Workspace/单 Agent 启用。

出口：低价窗口打开后无需 Heartbeat 即开始；窗口关闭或用户消息到达后不误启动。

### P2 UI、观测与旧路径退役

依赖：P1-4 稳定。

- Goal Banner、Task binding、Availability、窗口原因和 deep link。
- 指标/日志/诊断页连接 Task -> Goal -> Iteration -> Turn -> Run -> Verification -> Usage。
- 停止旧 GoalMode 后置注入，删除 DI 和测试替换。
- 旧 `goal_queue.json` 不导入、不自动删除；若存在只记一次脱敏警告，由用户决定是否归档。

出口：新路径是唯一 Goal/Auto Task Owner，旧 JSON 不再被读写，回滚不会删除新事实。

## 10. 测试矩阵

### 10.1 PuddingCoreTests

新增：

- `Goals/GoalStateMachineTests.cs`：合法/非法 phase、epoch、255/256/257、resume 不重置。
- `Goals/TaskBoundGoalContractsTests.cs`：必填字段、幂等键、Task/Goal 映射。
- `Scheduling/ExecutionWindowResolverContractTests.cs`：unknown/fail-closed 和稳定 code。
- `SystemCommandParserTests.cs`：Goal grammar、越界、空 objective、引号/多行。

### 10.2 PuddingPlatformTests

新增：

- `Services/Goals/GoalSchemaBootstrapperTests.cs`：表、索引、constraint、重复启动。
- `Services/Goals/SqliteGoalStoreTests.cs`：CAS、lease、epoch、idempotency、recovery。
- `Services/Goals/GoalCommandServiceTests.cs`：多入口命令和 restart disarm。
- `Services/Goals/GoalContinuationWorkerTests.cs`：事务边界崩溃、用户抢占、stale suppression。
- `Services/Goals/GoalCoordinatorTests.cs`：DONE proposal、gates、verdict、Task/Goal 双 CAS。
- `Services/Scheduling/AgentAvailabilityProjectorTests.cs`：unknown 默认、rebuild、TTL、approval/user queue/tool/subagent。
- `Services/Scheduling/AgentExecutionReservationStoreTests.cs`：一 Agent 一 Reservation、renew/release/expiry/fencing。
- `Services/Scheduling/ProviderModelExecutionWindowResolverTests.cs`：时区、跨日、边界、route/profile 改变。
- `Services/Tasks/TaskGoalDispatchTransactionStoreTests.cs`：每个 SQL 故障点的全成/全不成。
- `Services/Tasks/TaskGoalDispatchCoordinatorTests.cs`：确定排序、竞争、defer、用户优先。
- 更新 `LlmProviderFileServiceTests.cs`：价格时段 round-trip，未知字段/无效时区 fail closed。

### 10.3 PuddingRuntimeTests

- `Services/GoalContextContributorTests.cs`：受信 context、稳定 prefix 边界、不从文本猜 ID。
- `Services/GoalVerificationExecutorTests.cs`：strict schema、无工具、timeout/retry/fail-closed。
- 更新 `MessageDeliveryDispatcherTests.cs`：Task-bound Goal 不 batch；普通消息批处理不回归。
- 更新 `AgentExecutionStateRegistryTests.cs`：unknown 不默认 Idle，事实顺序不回退。
- 更新 `TurnExecutorAdapterTests.cs`：Task/Assignment/Goal/epoch/fencing 完整透传。
- 新增 `Services/TaskE2E/TaskBoundGoalE2ETests.cs`：Task -> Goal -> 20 Iteration -> verifier -> Task terminal。
- 删除/替换 `GoalModeServiceTests.cs`：切换后不再以 JSON 注入作为 Goal 验收。

### 10.4 Host/Admin/E2E

- Host composition：三个 flag 组合、每个 Hosted Worker 唯一、缺依赖 fail fast。
- Admin：Goal Banner、Task binding、waiting reason、window profile、SSE replay、pause/cancel。
- 故障注入：reserve 后、binding 后、outbox commit 前后、accept 前后、verdict 前后崩溃。
- 时间边界：窗口开启前 1ms/开启时/关闭前 1ms/关闭时，以及 route/profile 临界变更。
- 用户竞态：Ready + Idle 后用户消息抵达，Auto 必须释放或 defer，不抢占 foreground。

## 11. 可观测性

日志共同字段：

```text
workspaceId, agentId, taskId, taskVersion, assignmentId,
reservationId, reservationFencingToken, availabilityVersion,
providerId, modelId, windowKey, windowProfileVersion,
goalId, goalRevision, activationEpoch, iterationNumber,
commandId, turnId, runId, verificationId,
decisionCode, nextEligibleAtUtc, correlationId, causationId, errorId
```

指标：

```text
agent_availability_total{state}
agent_availability_transition_total{from,to,reason}
agent_idle_without_runnable_task_seconds
agent_reservation_total{result}

task_auto_dispatch_total{result,reason}
task_auto_dispatch_queue_age_seconds
task_goal_binding_active_total
task_goal_start_latency_seconds
task_low_price_window_missed_total{reason}

execution_window_decision_total{verdict,reason,provider,model}
execution_window_open_total{provider,model,window_key}

goal_outbox_pending
goal_continuation_lag_seconds
goal_iteration_total{result,source_kind}
goal_terminal_total{phase,source_kind}
```

`task_low_price_window_missed_total` 只在一个已配置的低价窗口结束时，仍有本可运行 Task 却因非窗口原因未启动时增加；标签使用有界 reason code，不把 taskId 放入指标高基数标签。

## 12. Feature Flag、切换和回滚

```text
GoalRuns.Enabled
TaskBoundGoals.Enabled
TaskAutoDispatch.Enabled
AgentAvailabilityProjection.Mode = disabled | shadow | authoritative
ExecutionWindowFence.Mode = disabled | shadow | enforce
```

启用序：

1. Schema + read-only query；
2. GoalRuns 内部用户灰度，Auto 关闭；
3. Availability shadow + Window shadow；
4. Availability authoritative，Auto 仍关闭；
5. TaskBoundGoals 手工单 Task smoke；
6. TaskAutoDispatch 单 Workspace/单 Agent；
7. 扩大 Agent/工作区，观测至少一个完整低价窗口；
8. 退役 Legacy GoalMode。

回滚：

- 先关 `TaskAutoDispatch.Enabled`，只停止新 binding；
- 再将 TaskBoundGoals 改为 read/pause/cancel only，不删除事实；
- 已 accepted Turn 允许 settle，旧 epoch 不再 continuation；
- 释放未 dispatch Reservation，保留 Task/Goal/Verification/Usage 审计；
- 不把新 GoalRun 倒灌回 `goal_queue.json`，不启用双 Owner。

## 13. 构建、部署与验收命令

每张施工卡先运行最小定向测试，分项目串行扩大。建议命令：

```powershell
dotnet test Source\PuddingCoreTests\PuddingCoreTests.csproj --no-restore --nologo
dotnet test Source\PuddingPlatformTests\PuddingPlatformTests.csproj --no-restore --nologo
dotnet test Source\PuddingRuntimeTests\PuddingRuntimeTests.csproj --no-restore --nologo
dotnet test Tests\PuddingHost.Tests\PuddingHost.Tests.csproj --no-restore --nologo
dotnet build Source\PuddingRuntime\PuddingRuntime.csproj --no-restore --nologo
git diff --check
```

若实际 `.csproj` 名称不同，以 `rg --files -g '*.csproj'` 结果为准，不把输出写入 `D:\data`。

源码修改后的产品验收分两段：

1. 内部开发 Agent 完成定向测试并交付 `ready-for-external-deploy`；
2. 进程外控制器部署并启动明确新构建，校验新 PID、产物时间/哈希、Ready/Health 与旧失败窗口后存活；
3. 在新 Pudding 产品会话中做真实 Task-bound Goal 功能 smoke，再交付 `in-product-functional-complete`；
4. Desktop/Core 重启、崩溃恢复和退出回收由进程外控制器最终判定。

## 14. 生产验收门禁

### A0 合同

- Goal/Task/Availability/Window 枚举和错误码在 Core/OpenAPI/Admin 一致。
- 256 外层 Iteration 与内层 Agent Loop Round 不共用字段。
- 没有 `work-policy.json` 或第二个前端状态机。

### A1 Goal 前置

- ADR-074 G0–G3 全部通过；Heartbeat 全关时 Goal 仍续行。
- DONE 不直接完成；verifier 失败 fail closed。
- 重启默认 disarm，用户 resume 才重新授权。

### A2 Availability

- 重启后从 unknown 重建，stale/unknown 不 Claim。
- active Turn/Tool/SubAgent、approval、user queue、reservation、cooldown 任一存在均不 Idle。
- 并发 Coordinator 对同一 Agent 只有一个 Reservation 成功。

### A3 Execution Window

- `off_peak_only` 在路由/价格档案未知时不执行。
- reserve/dispatch/accept 跨过窗口边界时重新判定。
- 用户 Run Now override 有权限、原因、事件和单次范围。

### A4 Task-bound Goal

- Assignment/Reservation/Binding/Goal/Outbox 事务故障注入全成或全不成。
- 一 Task 一 active Goal，一 Agent 一 auto Reservation。
- Task-bound Goal 不 batch，Runtime context 的 task/assignment/goal/epoch/fence 全部一致。
- Goal 终态与 Task 终态双 CAS，Delivery ACK 不误完成。

### A5 低峰实效

- 在至少一个完整真实低价窗口中，Ready + Idle 任务无需 Heartbeat 自动启动。
- 窗口内没有可运行 Task 时 Agent 保持空闲是正常结果，不空转发提醒。
- 窗口结束后能解释未派发的确切原因，并能从 Task 追溯到 Goal/Turn/Run/Usage。

## 15. 完成定义

只有同时满足以下条件，才能宣布“Agent 感知与低峰自动派发已实现”：

1. GoalRun 是 SQLite/canonical event 权威，旧 JSON GoalMode 不再运行。
2. Availability 是持久可重建投影，进程内 registry 不能误决定 Claim。
3. Task 偏好与 provider/model 价格时段没有重复配置权威。
4. Auto Dispatcher 的唯一启动产物是 Task-bound Goal，不是普通提醒消息。
5. Task/Goal 的启动、续行、终态、取消、抢占和恢复都有故障注入证据。
6. 真实低价窗口 smoke 证明任务会及时启动，且窗口外不误启动。
7. 新构建的进程外部署、Ready/Health、新 PID/产物和崩溃恢复已验收。
8. ADR 状态、完整设计、本施工计划、Docs 索引和 `code_map.md` 与实际实现状态一致。
