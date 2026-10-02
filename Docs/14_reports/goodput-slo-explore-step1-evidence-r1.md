# Goodput SLO Explore Step-1 证据（r1）

- 卡：P1 调度控制面与 Goodput SLO（Task→Goal→WorkUnit→模型→成本全链路对账）
- 范围：只读；Source/PuddingPlatform/Services/{Goals,Scheduling}、Services 根（Token/Llm*）、Data/Entities、PuddingCore/{Goals,Scheduling,Abstractions}、PuddingRuntime/Services 窗口读。禁止访问 Source/PuddingAgent。
- 检索纪律：未对整个 Source 全量 grep；命中上限处标注 coverage=partial。

## Q1. Heartbeat 分类枚举是否存在

**结论**：不存在 `progressed / awaiting_external / no_ready_work / recovered / failed` 这一枚举；"心跳/迭代结论"分别由「结算处置」「Goal 事件」「调度决策码」「任务健康裁决」四套既有词汇表达。

| 现有表达 | 定义处 | 取值 |
|---|---|---|
| 结算处置（iteration 结论） | `Source/PuddingCore/Goals/GoalVerificationContracts.cs:221-231` | continue_current / repair / advance / verify_goal / complete / wait / needs_user / stop |
| Goal 事件目录（进度/熔断/lifecycle） | `Source/PuddingCore/Goals/GoalEventTypes.cs:14-30`、`:65-77` | goal.completed / goal.blocked / goal.failed / goal.budget_exhausted / goal.progress.recorded / goal.circuit_opened |
| 调度决策码（每次 scan 的结论） | `Source/PuddingPlatform/Services/Scheduling/TaskSchedulerDecisionCodes.cs:12-26` | eligible / task_dependency_waiting / agent_not_idle / no_compatible_agent / execution_window_closed … |
| 任务健康裁决（read-only） | `Source/PuddingCore/Scheduling/TaskExecutionTrackingContracts.cs:10-16` | Healthy / Waiting / Stalled / Inconsistent / CleanupRequired |

**覆盖限定**：在 `Source/PuddingCore`（全 .cs）与 `Services/Goals` 检索 `awaiting_external|no_ready_work|progressed|recovered`，唯一命中为无关的 `Tasks/WorkspaceTaskModels.cs:319 TaskProgressed`（任务事件类型，非心跳分类）。`Source/PuddingPlatform/Services` 未做全量 grep ⇒ coverage=partial，但四套词汇已足以覆盖语义位。

## Q2. 「等待无忙轮询」现状与可观测点

**结论**：Goal 续行与结算均为**固定间隔轮询**（默认 5s），无事件驱动唤醒；等待事实通过 outbox 状态 + lease + deferred 重排 + 熔断事件可观测。

| 项 | 证据 |
|---|---|
| 续行轮询 | `Services/Goals/GoalContinuationWorker.cs:55`（`signal.WaitAsync(ContinuationScanInterval)`）、`:64`（异常后 `Task.Delay`） |
| 结算轮询 | `Services/Goals/GoalSettlementWorker.cs:31`、`:40`（同参数 `Task.Delay`） |
| 间隔/租约默认值 | `Source/PuddingCore/Goals/GoalRunOptions.cs:25`（5s）、`:26`（2min lease） |
| due 队列领取 + 租约恢复 | `Services/Goals/GoalOutboxStore.cs:11-30`（PeekDueAsync）、`:36-71`（TryClaimAsync 含 fencing_token++）、`:73-95`（RecoverExpiredLeasesAsync → pending + `lease_expired_recovered`） |
| 延后重试（繁忙不忙轮） | `GoalContinuationWorker.cs:250-258`（Deferred ⇒ `ConversationBusyRetryDelay` 后重排） |
| 无进展/同阻塞熔断 | `GoalSettlementStore.cs:1517`（`no_progress_circuit_open`）、`:1303-1316`（CircuitOpened 载荷含 consecutive 计数与 threshold）；消费方 `Goals/GoalResumeService.cs:19`、`:117` |
| 可观测事件 | `GoalSettlementStore.cs:734-742`（goal.progress.recorded：waitExcluded / consecutiveNoProgress / ...）、`GoalContinuationWorker.cs:106-113`（suppressed 原因日志） |

## Q3. 每 verified task 的输入/缓存/输出 token 与费用落库位置

**结论**：明细事实表 `platform.TokenUsageEvents`（含 token 与成本列）；Goal 迭代级另有 `goal_iterations.input_tokens/output_tokens`；本地计费账本 `llm_gateway_usage_events`。

| 层 | 表.列 | 写入点 |
|---|---|---|
| 调用明细 | `platform.TokenUsageEvents.PromptTokens/CompletionTokens/TotalTokens/CacheHitTokens/CacheMissTokens/CacheEligibleTokens/InputCost/OutputCost/CacheHitCost/TotalCost`（`Data/Entities/TokenUsageEventEntity.cs:60-95`；映射 `Data/PlatformDbContext.cs:308-320`） | `Services/TokenUsageRecorder.cs:198-235`（RecordCoreAsync 构实体）、成本归一 `Services/TokenUsageNormalizer.cs:87-99` |
| 迭代级 | `goal_iterations.input_tokens / output_tokens / llm_rounds / tool_calls`（`Data/Entities/GoalIterationEntity.cs:64-71`） | `Services/Goals/GoalSettlementStore.cs:644-645`（源：`:220` 的 SumUsage，`:461-488`） |
| 本地计费账本 | `llm_gateway_usage_events.*`（`Data/Entities/LlmGatewayUsageEventEntity.cs:32-35` 等） | `Services/LlmGatewayUsageRecorder.cs:73-80` |
| 日聚合 | `llm_usage_daily_aggregates`（`Data/Entities/LlmUsageDailyAggregateEntity.cs:20-67`） | `Services/TokenUsageDailyAggregateService.cs`（闭日构建） |

## Q4. `0 usage` / 缺价 / 未知归因 的处理

**结论**：**缺价被静默记为 0 成本**（无价格缺失标记列）；`usage` 为 null 时是「跳过」而非记 0；归因未知只写在内存标记里、**不落库**。

| 场景 | 判定代码 | 后果 |
|---|---|---|
| provider/model 无价格档案 | `Services/TokenUsageRecorder.cs:160-190`（inputPrice/outputPrice/cacheHitPrice 默认 `0m`，仅当匹配到 priceConfig 才赋值）；`Services/LlmGatewayUsageRecorder.cs:54-56`（`model?.InputPricePer1MTokens ?? 0m`） | TotalCost=0，等同"零花费"，**无 unknown_price 标记**，会污染 cost 对账与"节省"叙事 |
| 缓存价缺失 | `TokenUsageNormalizer.cs:75-76`（cacheHitPrice==0 回落 inputPrice） | 静默按输入价计，方向安全但掩盖缺档 |
| usage 为 null（无 usage 事件） | `Services/SessionStateManager.cs:1449`、`Services/Goals/GoalSettlementStore.cs:476`（`continue`，不累加 rounds）、`Services/ConversationProjector.cs:137` | 不计入统计（非 0 污染，但也**不产出"缺用量"告警**） |
| 未知归因 | `Source/PuddingCore/Abstractions/ITokenUsageRecorder.cs:105-113`（Unknown/SessionLatestFallback 常量）、`:132`（默认 `unknown`）、`Services/TokenUsageRecorder.cs:757`（回落标 `session_latest_fallback`） | 标记只影响本次写入的层 token 取值，**无对应落库列**（TokenUsageEventEntity 无 attribution_source 列） |

**覆盖限定**：在 `Services` 下检索 `price_missing|pricing_missing|zero_usage|usage_missing|no_usage` 无命中（coverage=partial：Services 根为分目录定向检索，非全量）。

## Q5. Task ↔ worker / assignment / Run 对账现状

**结论**：已有**只读端到端对账器**，可把 Task→Goal→WorkUnit→Command→Run→Outbox 串成一棵事实树；但 **Run 层无 provider/model 列**，"→模型"只能靠 `goal_runs.route_snapshot_json` 或按 session 关联 TokenUsageEvents，**不能由 Run 直接对账到模型**。

| 关联边 | 证据 |
|---|---|
| 对账入口（按 binding 逐条评估） | `Services/Scheduling/TaskExecutionTracker.cs:21-40`（读 `TaskGoalBindings` where Status=active） |
| 装载的关联集合 | `TaskExecutionTracker.cs:41-130`：WorkspaceTasks、TaskAssignmentAttempts、GoalRuns、AgentExecutionReservations、TaskPlanRuns、TaskNodes(depth=1=WorkUnit)、GoalIterations、GoalOutbox、ChatExecutionCommands、ExecutionRuns |
| 裁决与字段（含 ExecutionRunId/fencing/BindingId/LastProgressAtUtc） | `Source/PuddingCore/Scheduling/TaskExecutionTrackingContracts.cs:18-45` |
| fence 校验（assignment/task/plan/workunit/goal agent） | `TaskExecutionTracker.cs:317-345` |
| 模型归属（间接） | `Data/Entities/GoalRunEntity.cs:85`（route_snapshot_json）、`Data/Entities/GoalVerificationEntity.cs:34`；`ExecutionRunEntity.cs:12-58` **无 provider_id/model_id 列**；`ChatExecutionCommandEntity.cs:31-46` 有 workspace/session/agent_instance 但无模型 |

## Q6. 时间账四类（active work / LLM wait / tool wait / queue）现有记录位

**结论**：**没有统一的四桶时间账**（无 active_work/llm_wait/tool_wait/queue_wait 字段或 metric）。四类时间散落在 telemetry_metric_events、runtime_activity 与 outbox/attempt 的时间戳列上。

| 类别 | 记录位（表/metric） | 写入点 |
|---|---|---|
| LLM wait（限流退避） | metric `llm.rate_limit.wait`（DurationMs + metadata `rate_limit_wait_ms`） | `PuddingRuntime/Services/DirectLlmClient.cs:412-455`；落 telemetry：`:1041-1068`（Name=`llm.{operation}`、Category=Llm） |
| LLM wait（首包） | `chat_stream` 终态 metric DurationMs=`_firstChunkWaitMs` | `DirectLlmClient.cs:1492`（另见已给事实 `:1415`、`:1460`） |
| tool wait / active | metric `tool.call`（DurationMs；dimension `tool_duration_ms`） | `PuddingRuntime/Services/AgentExecutionService.cs:885-940`（RecordToolMetricAsync） |
| tool wait（活动流水） | `runtime_activity.operation='execute_tool'`、`duration_ms`、metadata `tool_duration_ms` | `AgentExecutionService.Buffered.cs:1100-1120`、`:1803-1820`；表 `Data/Entities/RuntimeActivityEntity.cs:6,54-63` |
| active work | `runtime_activity` 各 operation 的 started/ended/duration（如 `execute`/`assemble_context`） | `AgentExecutionService.Buffered.cs:81`、`:229-342` |
| queue（派发） | `task_dispatch_outbox.created_at_utc → sent_at_utc` | `Data/Entities/TaskDispatchOutboxEntity.cs:47-52` |
| queue（Goal 续行） | `goal_outbox.created_at_utc / due_at_utc / completed_at_utc` | `Data/Entities/GoalOutboxEntity.cs:41,59,62-63` |
| 迭代周转 | `goal_iterations.started_at_utc / settled_at_utc` | `Data/Entities/GoalIterationEntity.cs:54-57` |
| 占位（active 占用） | `task_assignment_attempts.active_at_utc / released_at_utc` | `Data/Entities/TaskAssignmentAttemptEntity.cs:74,78` |
| 会话级总时长汇总 | Trace report TotalDurationMs | `Services/SessionStateManager.cs:1419`、`:1569` |

**覆盖限定**：PuddingRuntime/Services 为定向 grep（`tool.wait|tool_wait|queue_wait|active_work` 无命中 ⇒ 未命中为真未命中，非截断）；PuddingPlatform/Services 的 `*.cs` grep 命中上限 60 行（coverage=partial）。

## Q7. 是否已有「按成功工作单元聚合的成本/质量统计」

**结论**：**没有 cost per verified unit，也没有质量前后对比聚合**；只有「日 × 来源 × provider × 模型」的 token/成本聚合，以及 per-iteration 的 token/轮次计数。成本感知路由是**声明式选路**（用价格档案排序），不是实测成本分摊。

| 现有聚合 | 定义处 | 缺什么 |
|---|---|---|
| 日/来源/provider/model 的 token+成本+请求数 | `Data/Entities/LlmUsageDailyAggregateEntity.cs:20-67` | 无 taskId/goalRunId/workUnitId 维度 |
| 调用明细 | `Data/Entities/TokenUsageEventEntity.cs:11-60` | 有 SessionId/ParentSessionId/SubAgentId，**无 TaskId/GoalRunId** |
| per-iteration tokens/rounds/tool_calls | `Data/Entities/GoalIterationEntity.cs:64-71` | 无成本列、无验收通过与否的联合聚合 |
| 成本感知路由（声明式） | `Services/Scheduling/ModelRoutePolicyContracts.cs:20-31`（InputCostPerMillionTokens/OutputCostPerMillionTokens/QualityScore）、`:36-47` RoutePolicy、`RoutePolicyCatalog`；排序 `Services/Scheduling/ModelRoutePolicyEvaluator.cs:149-158` | 只用**声明价**与**声明质量分**，不消费实测成本/通过率 |

**覆盖限定**：`Source` 下 grep `goodput|cost_per|verified_unit` 仅命中测试名 `Source/PuddingPlatformTests/Services/Goals/GoalSettlementAcceptanceRegressionTests.cs:506`（VerifiedUnit_WithNextUnit_AdvancesWithoutCompletingPlan），无生产聚合代码；该次 grep 触发 2000 文件枚举上限 ⇒ coverage=partial（已扫 890/2000 文件）。
