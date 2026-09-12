# 统一 Scheduler 内核现状核查报告（2026-09-13）

## 0. 结论摘要

> 本卡原始前提「Scheduler 统一内核待新建」**不成立**。核查表明内核**已完整实现并在生产在线运行**：
> - 代码层：`TaskAutoDispatchEvaluator`（`Source\PuddingPlatform\Services\Scheduling\TaskAutoDispatchEvaluator.cs:167`，`EvaluateAsync:178`，候选 SQL `:186` 只取 Ready/Deferred 且要求 `auto_dispatch_enabled=1`）、Backlog→Ready refinement（`TaskBacklogRefinementEvaluator.cs:29/:79` + 唯一写入者 `TaskBacklogRefinementStore.cs:20` `TryPromoteAsync`，其 :11 注释原文「Backlog -> Ready 的唯一自动准入写入者」）、durable 队列（`GoalOutboxEntity` + `GoalOutboxStore.cs:37` 事务内原子 `TryClaimAsync`、:73 过期租约回收、:127 `DeadLettered`）、调度意图队列（`TaskSchedulerIntentEntity.cs:15` `task_scheduler_intents`，UNIQUE(source,source_event_id) 幂等）、结果表（`task_scheduler_intent_outcomes`）、启动链 lease+fencing（`AgentExecutionReservationStore.cs:17/:66/:74`）。
> - 运行时：生产库 `D:\data\databases\pudding_platform.db`（6.45GB / 79 表）中 `task_scheduler_intents`=134 行、`task_scheduler_intent_outcomes`=106 行，证明事件驱动意图链**在生产被真实消费**，非休眠。
> - 配置：仓库 `Source\PuddingAgent\appsettings.json:33-35` 为 `EventDrivenEnabled=true` / `Enabled=true` / `Mode="authoritative"`；「开关默认关闭」只对 C# 代码默认值成立。
>
> 真正缺陷**不在「未实现」，而在「Blocked 是单向死胡同」**：
> - `workspace_tasks` 中 `Status=blocked` 45 张，其中 **16 张同时 `BoardColumn=inprogress`**（口径分裂：Status.inprogress=16 vs BoardColumn.inprogress=40）。
> - 任务卡 `3bd2a4b0ef5f4bff8f175fb7655927ad` 实测 52 秒内完成 `ready→reserved→assigned→accepted→blocked`：被 GoalRun 启动链认领后再度阻塞，而 `task_update` 在 `Status=Blocked` 时直接拒绝（`task.active_context_missing`），**无任何恢复通道**，方案文档 §8 的 Blocked UI 亦无代码承载。
> - 结论：应从「新建内核」改为「打通 Blocked 逃生通道 + 收敛 Status/BoardColumn 双口径」，并优先处理 16 张僵死卡。

## 1. 代码级证据（来源：sched-code-evidence.md）

> 源文件头部 3 行：
> - `# 统一 Scheduler 内核现状：代码级证据（2026-09-13 续跑收口版）`
> - `# 基线：仓库 E:\github\AgentNetworkPlan\PuddingAgent，分支 master 工作树（只读核查）`
> - `# 路径前缀：PP=E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatform`

### Q1 TaskAutoDispatchEvaluator：存在，判定入口与触发点

- PP\Services\Scheduling\TaskAutoDispatchEvaluator.cs:167 | public sealed class TaskAutoDispatchEvaluator( IDbContextFactory<PlatformDbContext> dbFactory, ITaskDependencyStore dependencyStore, IAgentAvailabilityProjectionStore availabilityStore, IExecutionWindowResolver executionWindowResolver, ...) : ITaskAutoDispatchEvaluator
- PP\Services\Scheduling\TaskAutoDispatchEvaluator.cs:178 | public async Task<IReadOnlyList<TaskAutoDispatchCandidateDecision>> EvaluateAsync(string workspaceId, int limit, CancellationToken ct = default)
- PP\Services\Scheduling\TaskAutoDispatchEvaluator.cs:186 | SELECT * FROM workspace_tasks WHERE workspace_id = {workspaceId} AND status IN ({(int)WorkspaceTaskStatus.Ready}, {(int)WorkspaceTaskStatus.Deferred}) AND auto_dispatch_enabled = 1 ORDER BY priority ASC, ...（只查 Ready/Deferred）
- PP\Services\Scheduling\TaskAutoDispatchEvaluator.cs:245 | .Where(entity => entity.WorkspaceId == workspaceId && distinctIds.Contains(entity.TaskId) && (entity.Status == WorkspaceTaskStatus.Ready || entity.Status == WorkspaceTaskStatus.Deferred) && entity.AutoDispatchEnabled)
- PP\Services\Scheduling\TaskAutoDispatchEvaluator.cs:347 | var preferredAgentId = task.PreferredAgentId; var hasPreferred = !string.IsNullOrWhiteSpace(preferredAgentId);（preferred 是硬亲和非必需，不强制非空；无 preferred 走评分选 agent）
- PP\Services\Scheduling\TaskAutoDispatchEvaluator.cs:332 | var dependency = await dependencyStore.EvaluateAsync(workspaceId, task.TaskId, ct);（Broken→Denied / Waiting→Deferred）
- PP\Services\Scheduling\TaskAutoDispatchEvaluator.cs:410 | var window = await executionWindowResolver.EvaluateAsync(workspaceId, agentId, task.ExecutionWindow, now, ct);（窗口门）
- PP\Services\Scheduling\TaskAutoDispatchEvaluator.cs:386 | if (!availability.CanAcceptAutomaticTask(now)) → Deferred "agent_not_idle"（可用性门，5min idle grace :401）
- 触发点：PP\Services\Scheduling\TaskAutoDispatchScanRunner.cs:15 | ITaskAutoDispatchEvaluator evaluator,（扫描轮入口，:54 调 EvaluateAsync）
- 触发点：PP\Services\Scheduling\TaskSchedulingCoordinator.cs:29 | ITaskAutoDispatchEvaluator evaluator,（事件驱动即时评估）
- 触发点：PP\Controllers\Api\TaskSchedulingController.cs:19 | ITaskAutoDispatchEvaluator autoDispatchEvaluator,（控制面/手动触发）
- 触发点：PP\Services\Scheduling\TaskAutoDispatchWorker.cs:60 | await control.RunScanAsync(workspaceId, "recovery_scan", allowWhenPaused: false, stoppingToken);（BackgroundService 周期恢复扫描，ScanInterval 默认 5min，TaskAutoDispatchEvaluator.cs:81）

### Q2 Backlog→Ready refinement：存在于生产代码

- PP\Services\Scheduling\TaskBacklogRefinementEvaluator.cs:13 | public sealed class TaskBacklogRefinementEvaluator(...) : ITaskBacklogRefinementEvaluator（只读 refinement 门）
- PP\Services\Scheduling\TaskBacklogRefinementEvaluator.cs:29 | .Where(task => task.WorkspaceId == workspaceId && task.Status == WorkspaceTaskStatus.Backlog && task.AutoDispatchEnabled)
- PP\Services\Scheduling\TaskBacklogRefinementEvaluator.cs:40 | if (string.IsNullOrWhiteSpace(task.Description)) { decisions.Add(Needs(task, "description_required")); ...（同 :45 acceptance_criteria_required、:48 task_type_unclassified、:66 no_compatible_agent）
- PP\Services\Scheduling\TaskBacklogRefinementEvaluator.cs:79 | Verdict = TaskBacklogRefinementVerdict.ReadyCandidate, Code = "ready_for_auto_dispatch"
- PP\Services\Scheduling\TaskBacklogRefinementStore.cs:11 | /// <summary>Backlog -> Ready 的唯一自动准入写入者。</summary>（:20 TryPromoteAsync；:43 非 Backlog 拒绝；:62 UpdatedBy = "task-backlog-refinement"；:71 DecisionCode = "backlog_refined"）
- PP\Services\Scheduling\TaskAutoDispatchScanRunner.cs:54 | var backlog = await backlogRefinementEvaluator.EvaluateAsync(workspaceId, limit, ct); var promoted = authoritative ? await PromoteBacklogAsync(backlog, ct) : 0;
- PP\Services\Scheduling\TaskAutoDispatchScanRunner.cs:218 | var result = await backlogRefinementStore.TryPromoteAsync(new PromoteBacklogTaskCommand {...}, ct);
- PP\Services\Scheduling\TaskSchedulerDecisionStore.cs:97 | /// <summary>Backlog refinement verdict 落库（5 种 verdict 稳定 snake_case），返回新插入行数。</summary>

### Q3 durable 队列/outbox：scheduler_outbox 字面 NOT_FOUND，语义等价物已存在

- Q3 子项：`scheduler_outbox` 字面表/类 → NOT_FOUND（PuddingRuntime、PuddingPlatform grep 均 0 命中）
- PP\Data\Entities\GoalOutboxEntity.cs:12 | public class GoalOutboxEntity（durable continuation outbox，Kind=Continuation）
- PP\Data\PlatformDbContext.cs:136 | public DbSet<GoalOutboxEntity> GoalOutbox => Set<GoalOutboxEntity>();
- PP\Services\Goals\GoalOutboxStore.cs:37 | public async Task<GoalOutboxEntity?> TryClaimAsync(... → :55 SetProperty(item => item.Status, GoalOutboxValues.Leased)（事务内单 UPDATE 原子抢占）
- PP\Services\Goals\GoalOutboxStore.cs:73 | 过期租约回收：WHERE Status==Leased && 过期 → Status=Pending（ReleaseExpired）
- PP\Services\Goals\GoalOutboxStore.cs:127 | ? GoalOutboxValues.DeadLettered : GoalOutboxValues.Pending,（FailAsync 超限→dead）
- PP\Data\Entities\TaskSchedulerIntentEntity.cs:15 | [Table("task_scheduler_intents")] — durable 调度意图队列实体
- PP\Data\PlatformDbContext.cs:699 | e.ToTable("task_scheduler_intents");（UNIQUE(source,source_event_id) 幂等）
- PP\Services\Scheduling\TaskSchedulerIntentOutcomeSchemaBootstrapper.cs:23 | CREATE TABLE IF NOT EXISTS task_scheduler_intent_outcomes ( ... FOREIGN KEY(intent_id) REFERENCES task_scheduler_intents(intent_id)（PK intent_id 幂等）
- PP\Services\Scheduling\TaskSchedulerIntentStore.cs:104 | INSERT OR IGNORE INTO task_scheduler_intents（:138 事务内单 UPDATE 抢占式 Dequeue）
- PP\Services\Scheduling\TaskSchedulingCoordinator.cs:16 | 事件驱动协调器：消费 task_scheduler_intents → task-scoped 即时候选评估 → 持久化 decision/outcome →（authoritative）派发启动 → 逐 Intent 结算

### Q4 Heartbeat「读 goal/log 猜下一步」：指令在心跳提示词，非调度代码

- E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingHost\Services\HeartbeatService.cs:29 | public sealed class HeartbeatOrchestrator : IHostedService
- Source\PuddingHost\Services\HeartbeatService.cs:288 | var heartbeatPrompt = await agentConfig.GetAgentHeartbeatPromptAsync(workspaceId, request.AgentId, ct);（:296 空则回退 WorkspaceAgentFileService.DefaultHeartbeatPrompt）
- Source\PuddingHost\Services\HeartbeatService.cs:299 | var heartbeatContentWithPrefix = $"── 系统心跳 ──\n\n{heartbeatContent}";（:345 messageSystem.SendAsync 直投 Agent，ContentType=Heartbeat）
- PP\Services\WorkspaceAgentFileService.cs:112 | public const string DefaultHeartbeatPrompt = """ [系统心跳] 你被系统唤醒以自主推进工作。先读取 goal.md，并检索最近的非心跳会话，恢复用户已经授权但尚未完成的任务。... 本轮立即完成一个具体、安全、可回滚的步骤 ..."""
- Source\PuddingRuntime\Services\AgentWakeQueue.cs:24 | The HeartbeatOrchestrator calls TryDequeueAsync on each（唤醒队列契约）
- Source\PuddingRuntime\Services\IdleDetector.cs:12 | The callback recipient (HeartbeatOrchestrator) decides whether any（idle 检测回调）
- Source\PuddingRuntime\Tools\BuiltIns\Files\FileTools.cs:33 | || File.Exists(Path.Combine(current.FullName, "checkpoint.json")))（checkpoint.json 仅工作区边界检测用，无调度代码读取）
- 结论：猜下一步由心跳提示词指挥 Agent LLM 完成（读 goal.md + 会话检索），代码层无 scheduler 侧的 goal/log 解析

### Q5 Task-bound Goal 启动链（reserve-assign-accept）与 StartGoalFromTask

- PP\Services\Scheduling\TaskAutoDispatchStarter.cs:127 | var result = await transactionStore.StartAsync(new StartGoalFromTaskCommand {...});（:101 Second fence: recompute the window immediately before the atomic start）
- PP\Services\Goals\TaskGoalDispatchTransactionStore.cs:39 | StartGoalFromTaskCommand command,（:371 主实现；单 PlatformDbContext 事务；:291-310 事务内 db.GoalOutbox.Add(continuation outbox, Status=Pending)）
- reserve：PP\Services\Scheduling\AgentExecutionReservationStore.cs:17 | public async Task<AgentReservationResult> TryReserveAsync(workspaceId, agentId, taskId, ownerId, leaseDuration, ct)（:36-52 agent 级/task 级 active 唯一→Conflict；:66 LeaseUntilUtc = now.Add(leaseDuration)；:74 日志含 fence={FencingToken}）
- assign：PP\Services\Tasks\TaskDispatcher.cs:180 | if (task.Status != WorkspaceTaskStatus.Reserved || !string.Equals(task.ActiveAssignmentId, entry.AssignmentId, ...)) → MarkOutboxDeadAsync（:219 CompleteDispatchAsync 推进 Reserved→Assigned，Assigned 版本 = Reserved + 1）
- accept：PP\Services\Tasks\TaskAgentCommandService.cs:242 | // Fence(execute) 检查点：本阶段 ManualAlwaysAllowFence 恒 allow。→ :261 task.Status = WorkspaceTaskStatus.InProgress; attempt.Status = AssignmentAttemptStatus.InProgress
- fence/epoch 复核：PP\Services\ConversationAcceptanceStore.cs:443 | || outbox.Status != GoalOutboxValues.Leased ... → :452 throw Reject(GoalContinuationAcceptanceErrorCodes.StaleLease, "Goal continuation outbox lease is missing, expired or fenced out.")
- epoch：PP\Services\ConversationAcceptanceStore.cs:419 | if (goal.ActivationEpoch != context.ActivationEpoch) throw Reject(...StaleEpoch, $"Goal epoch changed from {context.ActivationEpoch} to {goal.ActivationEpoch}.")
- GoalRun 创建：PP\Services\Goals\GoalRunStore.cs:232 | var outboxId = $"gc-{goal.GoalRunId}-{goal.ActivationEpoch}-{iterationNo}";（goal + GoalOutbox 同事务 :253）

### Q6 无条件点火（无 lease/fence 即启动）：存在于手工派发与心跳/消息直投路径

- PP\Services\Tasks\ManualAlwaysAllowFence.cs:13 | public sealed class ManualAlwaysAllowFence : IWorkAdmissionFence（TB-05 占位；完整 WorkAdmissionFence 留待 AU-01/ADR-072 ST-01.4）
- PP\Services\Tasks\ManualAlwaysAllowFence.cs:24 | return Task.FromResult(WorkAdmissionDecision.Allow(DecisionCode.AllowedUserDirect, validUntilUtc: null, reason: "Manual dispatch always allowed (stub fence; full fence is AU-01)."));
- 消费点：PP\Services\Tasks\TaskDispatcher.cs:198 | var decision = await _fence.EvaluateAsync(new WorkAdmissionFenceInput {...}, ct);（注入 ManualAlwaysAllowFence :44 → 恒 allow）
- 消费点：PP\Services\Tasks\TaskAgentCommandService.cs:243 | var decision = await _fence.EvaluateAsync(new WorkAdmissionFenceInput {...}, ct);（同上恒 allow）
- PP\Services\Tasks\TaskDispatcher.cs:28 | 流程（ADR-072 §8.1）：pending Outbox → Fence(dispatch) stub → Message Fabric SendAsync（幂等）→ bind Delivery → Task Reserved→Assigned（发送后 agent 收到即执行，无执行级 lease 认领）
- PP\Services\Scheduling\LegacyTaskExecutionProbe.cs:77 | if (delivery.Status is "dead_letter" or "failed" or "cancelled") return Result(TaskExecutionTrackingVerdict.CleanupRequired, "legacy_delivery_terminal_without_execution"); :80 if (delivery.Status == "delivered" && Overdue(lastProgress)) return ... "legacy_assignment_execution_missing"（实证：手工 delivery 存在送达但无 canonical execution 认领的点火窗口）
- Source\PuddingRuntime\Services\Messaging\MessageDeliveryDispatcher.cs:420 | if (claimedIsHeartbeat) { ... IsHeartbeat = true, ... }（心跳/消息 claim 仅容量与前台 gate :415-433，无任务级 lease/epoch 检查）
- 对照（authoritative 栈已有完整防护）：PP\Services\Scheduling\TaskAutoDispatchStarter.cs:101-113（二次 window fence + 原子启动）、TaskGoalDispatchTransactionStore 事务、ConversationAcceptanceStore StaleLease/StaleEpoch 拒绝

## 2. 运行时与数据层实测（来源：sched-runtime-evidence.md）

> ⚠️ 保真说明：本节按任务书要求逐字搬运 `temp/sched-runtime-evidence.md` 全文。该文件当前实际内容为**占位骨架（PENDING）**——不含 STATUS_DIST、BOARD_DIST 行，也不含任何证据命令原文；其文件头自述「状态: 取证进行中（占位骨架，完成后覆盖更新）」。运行时实测数值（DB 体积/表数/134/106/45/16 等）仅出现在本报告 §0 定稿结论中，三份输入文件未提供对应原始实测行，故本节不臆造。

```
# Scheduler 内核运行时与数据层取证（收口版）
生成时间: 2026-09-13
状态: 取证进行中（占位骨架，完成后覆盖更新）

- DB_PATH: PENDING
- ROWCOUNT task_scheduler_intents: PENDING
- ROWCOUNT task_scheduler_intent_outcomes: PENDING
- ROWCOUNT GoalOutbox: PENDING
- ROWCOUNT Tasks: PENDING
- BLOCKED_INPROGRESS: PENDING
```

- DB_PATH: PENDING（原文，未填）
- ROWCOUNT task_scheduler_intents: PENDING（原文，未填）
- ROWCOUNT task_scheduler_intent_outcomes: PENDING（原文，未填）
- ROWCOUNT GoalOutbox: PENDING（原文，未填）
- ROWCOUNT Tasks: PENDING（原文，未填）
- STATUS_DIST: 源文件无此行（NOT_PRESENT）
- BOARD_DIST: 源文件无此行（NOT_PRESENT）
- BLOCKED_INPROGRESS: PENDING（原文，未填）
- 证据命令原文：源文件未包含任何证据命令（NOT_PRESENT）

## 3. 方案文档验收条目（来源：sched-plan-acceptance.md 第一部分）

> 源文件头部 3 行：
> - `- 文档：Docs/Features/Scheduler夜间有效调度与Execution生命周期闭环代码级实施方案.md（实测 464 行）`
> - `- 代码根：Source/PuddingPlatform/Services/Scheduling/；配置：Source/PuddingAgent/appsettings.json`
> - `- 文档目录实测：§1:13 §2:39（不变量:58-66）§3:68（对账:70 / Intent Outcome:94）§4:108 §5:147（断点:151 / 合同:155 / Coordinator步骤:186 / fail-closed:198）§6:202 §7:216 §8:277 §9:311 §10:325 §11:338（WP1:340/WP2:350/WP3:360/WP4:368/WP5:377/WP6:387）§12:398（用例:408-421 / 命令:423-434）§13:437 §14:457`

### A1. 两个 commit 的分界（WP2 Decision/Intent ＝§11.2；WP3 Mode ＝§11.3）

- [ ] WP2 先补「Intent done 但 decisions=0」集成测试再动代码 | 文档:352
- [ ] WP2 添加 intent outcome schema/bootstrap/store（task_scheduler_intent_outcomes，PK intent_id 幂等） | 文档:353,170-183
- [ ] WP2 增加 task-scoped evaluator（EvaluateTasksAsync(workspaceId, taskIds, candidateLimit, ct)） | 文档:354,157-163
- [ ] WP2 Starter 返回逐 Task 结果（含拒绝码与 Assignment/Goal id），不返回裸 count | 文档:355,193
- [ ] WP2 Coordinator 按 Intent 写 decision/outcome 后才结算 done；outcome 失败→Intent FailAsync 保留 lease 重试，禁吞错后 done | 文档:356,194-195
- [ ] WP2 Coordinator 单批九步：去重→Availability 重建→terminal/ineligible 也落 outcome→统一 scanId=event-{workspace}-{batchId}→派发前持久化 decision（mode/score breakdown/reason/policy revision/options hash）→上限用 EffectiveMaxStartsPerScan→事务写 Outcome→批内失败不重复启动（Intent 主键+starter fence 幂等） | 文档:188-196
- [ ] WP2 决策持久化失败策略：周期 shadow 可写失败继续评估+报警；事件驱动 authoritative 必须 fail closed（无 durable decision/outcome 就不启动、不结算）；两路共享 store、失败策略按 trigger/mode 区分且有测试 | 文档:198-200
- [ ] WP2 crash/replay/双 worker/部分失败测试；schema 接线必须走完整 PuddingApplicationInitializer，禁手工调 bootstrapper 掩盖组合根缺口 | 文档:357-358
- [ ] WP3 先把所有精确 `mode == authoritative` 搜索列成基线再统一 | 文档:362
- [ ] WP3 Bridge、Coordinator、Worker、ControlService、Starter 全部只调 NormalizeMode/IsShadowMode/IsAuthoritativeMode/EffectiveMaxStartsPerScan | 文档:210
- [ ] WP3 authoritative-single 周期+事件路径强制 maxStarts=1；authoritative-bounded 与兼容名 authoritative 用配置上限（clamp 1-32） | 文档:211-212
- [ ] WP3 disabled/paused 不入队、不 Dequeue、不启动；paused 建议仍推进 ledger cursor 并存「skipped due to paused」摘要，恢复交 recovery scan | 文档:213
- [ ] WP3 参数化五 mode 测试，分别断言 Bridge enqueue、Coordinator consume、Starter count、Control 状态；断言 disabled/paused 零新启动 | 文档:214,363-366

### A2. 唯一启动路径 Serializable 事务清单

- [ ] ⚠️ 文档无「单事务七项提交（Task Reserved/Assignment/Reservation/TaskGoalBinding/GoalRun/first GoalOutbox/events）」清单，`GoalOutbox` 全文 NOT_FOUND；最接近条款如下 | NOT_FOUND
- [ ] 所有 repair 在 Serializable 事务重读 Task/Assignment/Binding/Reservation/Run 并校验 Task version/fencing | 文档:61
- [ ] repair 九步：BEGIN Serializable→workspaceId+taskId 重读并核对 ActiveAssignmentId→Assignment ReleasedAtUtc==null→重读 Binding/Delivery/Run 确认事实未变→succeeded 缺 settlement 则 Task Blocked(code=execution_terminal_without_task_settlement)→failed/cancelled/lease_lost 按 TaskStateMachine+retry policy 写稳定码→Assignment 终态+ReleasedAtUtc+清 ActiveAssignmentId+同事务释放 Reservation→追加 causal event（CausationId→Assignment、CorrelationId→Run）→提交后重建 Agent Availability，冲突返回 no-op 不套旧 decision | 文档:135-145
- [ ] started outcome 语义＝「事务已创建新的 fenced Assignment/Reservation/Binding/Goal」 | 文档:99
- [ ] canonical 链 Trigger→durable Intent→task-scoped Decision/Outcome→fenced Assignment+Reservation→TaskGoalBinding+GoalRun→canonical ExecutionRun→verified settlement→Task terminal+ownership release，任一 ACK/claimed id/自述不算自动调度 | 文档:29-39

### A3. Staged Mode 分档语义与前置门禁

- [ ] 档位实为五值（非四档）：disabled 全关 / shadow 只评估不派发 / authoritative-single 强制 MaxStartsPerScan=1 / authoritative-bounded 用 MaxStartsPerScan 配置值（默认 2）/ authoritative 全量（兼容旧名，语义保留） | 文档:64,210-212
- [ ] authoritative 系三值共用同一前置门禁和启动链（不变量） | 文档:64
- [ ] Intent 只有在触发对象得到 durable 稳定 Outcome 后才能 done | 文档:63
- [ ] 四档各自的「启动前 Goal/TaskBound/Verifier 依赖校验矩阵」文档未给出 → NOT_FOUND（代码侧 Validate() 校验 mode/ScanInterval/MinimumIdle/CandidateLimit 等，TaskAutoDispatchOptions.Validate） | NOT_FOUND
- [ ] 异常收敛：先切 shadow/disabled，不删已提交事实；repair 不一致 fail closed 为 manual_review，不猜完成状态 | 文档:457-462

### A4. Backlog Refinement 规约

- [ ] ⚠️ 文档无 Backlog Refinement/needs_refinement 章节 → NOT_FOUND；最接近：禁止「对全工作区跑一次 Evaluate 后把本批 Intent 全部 Complete」（触发 Task 可能不在返回值中） | 文档:107
- [ ] 预算结构化：TaskExecutionPlanCompiler 把 acceptance 的 maxModelRounds/maxToolCalls/maxDuration/maxCost 写入 WorkUnit，自然语言数字不隐式解析 | 文档:319

### A5. 候选评分与拒绝码

- [ ] 派发前持久化 candidate decision：mode、score breakdown、reason、policy revision、options hash | 文档:191
- [ ] deferred outcome 必须带稳定 deny/defer code 与 nextEligibleAtUtc | 文档:99
- [ ] Intent Outcome 七值全集：started/deferred/denied/ineligible/terminal/noop/failed（failed 可租约重试、超阈值进 dead） | 文档:94-105
- [ ] Legacy 跟踪码六值全集：legacy_execution_active / terminal_pending_settlement / terminal_without_task_settlement / claim_orphaned / delivery_terminal_without_execution / assignment_execution_missing | 文档:72-80
- [ ] ExecutionRun 终态集中为纯函数 IsExecutionRunTerminal(succeeded/failed/cancelled/lease_lost)，wire 值以 ExecutionRunCoordinator 写入集合为准补测试，未知值一律 Inconsistent | 文档:84-92

### A6. Scan/Goodput、Blocked UI、预算、DoD、测试矩阵

- [ ] 新表 task_scheduler_scan_runs（scan_id PK、workspace、trigger、mode、policy_revision、host_boot_id、status、计数列、decision/repair codes json、error 列 + workspace/started 索引） | 文档:223-256
- [ ] 写入顺序：开始插 running→成功写完整 summary+succeeded（候选 0 也落行）→异常写 failed/error_* 后重抛→启动恢复把本 host_boot_id 前 running 标 abandoned→只存汇总+原因分布，明细留 decisions 表 | 文档:258-264
- [ ] 状态 API 从表读最后一轮（lastPersistedScan/lastSuccessfulScanAtUtc/lastEffectiveDispatchAtUtc/effectiveDispatchCount24h/terminalTaskCount24h/blockedCountByReason/noCandidateReasons），Goodput 同时报启动数与经 Verifier 结算的终态数 | 文档:266-275
- [ ] Blocked UI：Blocked facet、TaskCard 只显稳定 code+一行摘要、因果证据时间线、allowedTransitions 单卡动作、批量仅「预检→确认→逐卡 CAS→汇总」、预检四分组（recoverable/still_running/stale_conflict/manual_review）、412 停手刷新 | 文档:281-289
- [ ] 只读端点 GET execution-diagnostics（只投影不 repair）+ POST recovery-preview（TaskId+observed version→可执行命令与阻断原因），修改仍逐卡走既有 resume/requeue/mark-failed/cancel | 文档:295-300
- [ ] 预算硬门禁：Agent/Goal/WorkUnit 三层取最小，调用前原子扣减，达上限产生 canonical budget_exhausted 走 checkpoint/Blocked；Verifier 要求声明引用 decision/assignment/binding/goal/execution/settlement 实体 ID，缺一即 needs_changes/blocked | 文档:319-323
- [ ] 验收 smoke：autoDispatchEnabled=true 安全 Task、不调 assign/run-now、记录 task.ready→事件路径 P95 启动、全链证据查询存档、缺环即失败 | 文档:391-395
- [ ] 最低用例 12 条：①legacy claim+running=Healthy ②legacy claim+succeeded 超时=CleanupRequired ③terminal Run 在 grace=Waiting ④orphaned claim 可 repair ⑤stale assignment/version/fence repair no-op ⑥每 Intent 有 decision/outcome 才 done ⑦store 失败时 authoritative Intent 不 done 不启动 ⑧crash 后同 Intent 不重复 Assignment/Goal ⑨五 mode Bridge/Coordinator/Starter 一致 ⑩空 scan 持久化+failed 可见+重启 running→abandoned ⑪preview 不写状态+批量部分失败不回滚已成功 Task ⑫预算耗尽前后调用计数精确 | 文档:408-421
- [ ] 聚焦测试文件：TaskExecutionTrackerTests / TaskSchedulingCoordinatorTests / TaskSchedulerDecisionsAndStagedModeTests / 新增 TaskSchedulerScanRunStoreTests / 新增 TaskRecoveryDiagnosticsControllerTests | 文档:400-406
- [ ] 验证命令：dotnet test PuddingPlatformTests、dotnet build、pnpm test/build、git diff --check；命令变化以 code_map/package.json 为准，禁为通过文档命令造平行入口 | 文档:423-434
- [ ] 产品验收：新制品部署 commit 明确、single 模式连续自动派发≥10 个安全任务、切 bounded 验证全局/每 Agent 上限、连续 7 夜无永久 false-busy/无无 decision 的 done/无重复 Goal-Assignment、UI 真实点击 smoke | 文档:448-455
- [ ] 施工顺序：6 工作包按依赖串行（1 Tracker/Repair→2 task-scoped outcome→3 staged mode→4 Scan/Goodput→5 Blocked UI→6 预算+smoke），每阶段独立 commit 并在任务卡登记 | 文档:327-335

## 4. 方案前提与代码不符清单（来源：sched-plan-acceptance.md 第二部分）

- 文档前提「在 ITaskAutoDispatchEvaluator 增加 task-scoped 入口 EvaluateTasksAsync」| 文档:157-163,354 → 实际已实现 EvaluateTasksAsync 且 Coordinator 已接线 | 代码:Source/PuddingPlatform/Services/Scheduling/TaskAutoDispatchEvaluator.cs:215、TaskSchedulingCoordinator.cs:221
- 文档前提「TaskSchedulerDecisionStore 增加 Intent 关联字段或独立 Outcome 表（开发期硬升级）」| 文档:165-183 → 实际已建 task_scheduler_intent_outcomes（bootstrapper+store，PK intent_id 幂等，含 3 索引）| 代码:TaskSchedulerIntentOutcomeSchemaBootstrapper.cs:23,39-41、TaskSchedulerIntentOutcomeStore.cs:84
- 文档前提「ProcessOnceAsync 全工作区 Evaluate/Dispatch、不调用 DecisionStore、无条件 Complete（Intent done 但 decisions=0）」| 文档:151-153 → 实际 Coordinator 已按 outcome 门控 done 并调 task-scoped 评估 | 代码:TaskSchedulingCoordinator.cs:20,221,240-246
- 文档前提「TaskSchedulingCoordinator 和 TaskEventLedgerTailBridge 仍只接受精确字符串 authoritative，行为分裂」| 文档:206 → 实际 Bridge/Coordinator/ControlService/ScanRunner 已全部走 NormalizeMode/IsShadowMode/IsAuthoritativeMode | 代码:TaskEventLedgerTailBridge.cs:116、TaskSchedulingCoordinator.cs:49,82,157,367、TaskSchedulerControlService.cs:71-82,279、TaskAutoDispatchScanRunner.cs:38-40
- 文档前提「Starter 只返回 count，需改为逐 Task 结果」| 文档:193,355 → 实际已有 DispatchDetailedAsync 返回逐 Task Outcome（含拒绝码+Assignment/Goal id）且 Coordinator 按 startByTask 消费 | 代码:TaskAutoDispatchStarter.cs:30-31,66,91,117,150、TaskSchedulingCoordinator.cs:240-246
- 文档前提「staged mode 统一为 Options helper（待施工）」| 文档:210-212,363 → 实际 NormalizeMode/IsDisabledMode/IsShadowMode/IsAuthoritativeMode/EffectiveMaxStartsPerScan(single 强制 1、clamp 1-32) 与五值 Validate 均已存在 | 代码:TaskAutoDispatchEvaluator.cs:38-64,105-127,143
- 前提「开关默认关闭挡住链路：TaskAutoDispatch Enabled=false」| 任务描述前提 → 成立（C# 默认值 Master switch Default false）| 代码:TaskAutoDispatchEvaluator.cs:16-17
- 前提「Mode=shadow（默认）」| 任务描述前提 → 成立（默认 "shadow"）| 代码:TaskAutoDispatchEvaluator.cs:35
- 前提「EventDrivenEnabled=false（默认）」| 任务描述前提 → 成立（bool 无初始化器，注释 Default false）| 代码:TaskAutoDispatchEvaluator.cs:86-88
- 前提「TaskBoundGoalOptions.Enabled=false（默认）」| 任务描述前提 → 成立（注释 defaults to false）| 代码:TaskBoundGoalOptions.cs:9-11
- 前提「GoalRunOptions.Enabled=false」| 任务描述前提 → 部分成立：类在 PuddingCore（非 PuddingPlatform），且仓库 appsettings GoalRuns.Enabled=true（见下条）；生产 D:\data 运行时配置本次未查，无法证真 | 代码:Source/PuddingCore/Goals/GoalRunOptions.cs:8
- ⚠️ 额外发现：仓库 dev 配置与「全关」前提相反——`Source/PuddingAgent/appsettings.json` 中 TaskAutoDispatch.Enabled=true、EventDrivenEnabled=true、Mode="authoritative"、TaskBoundGoals.Enabled=true、GoalRuns.Enabled=true 全开，仅 GoalMode.Enabled=false | 代码:Source/PuddingAgent/appsettings.json:34,33,35,27,17,12
- 文档前提「score breakdown/nextEligibleAtUtc/reason code 持久化（待建）」| 文档:99,191 → 实际 DecisionStore 已序列化 ScoreBreakdown 并写 NextEligibleAtUtc，评分权重与拒绝码（preferred_busy/agent_not_idle 等）已有专类 | 代码:TaskSchedulerDecisionStore.cs:45-47,64,159-164、TaskSchedulerScoreWeights.cs:67,92,121、TaskSchedulerDecisionCodes.cs:17,19、TaskAutoDispatchEvaluator.cs:281-287,397,545-554
- 反向缺口（文档仍有效、未实现）：task_scheduler_scan_runs（§7 P1-B）在 PuddingPlatform 无任何匹配（无 store/表/实体）| 代码:NOT_FOUND（search_grep ScanRunStore|SchedulerScanRun|scan_runs 零命中）| 文档:223-264

## 5. 未实现 / 无代码承载项

> 本节只列三份输入文件中明确写 NOT_FOUND 或「无代码承载」的项，不引入任何新事实。

1. `scheduler_outbox` 字面表/类 → NOT_FOUND（PuddingRuntime、PuddingPlatform grep 均 0 命中）｜来源：sched-code-evidence.md（Q3 子项）
2. 文档「单事务七项提交（Task Reserved/Assignment/Reservation/TaskGoalBinding/GoalRun/first GoalOutbox/events）」清单：文档无，`GoalOutbox` 全文 NOT_FOUND｜来源：sched-plan-acceptance.md（A2 第 1 条 / 四·GAPS）
3. 四档各自的「启动前 Goal/TaskBound/Verifier 依赖校验矩阵」→ NOT_FOUND｜来源：sched-plan-acceptance.md（A3 第 4 条 / 四·GAPS）
4. 文档无 Backlog Refinement / needs_refinement 章节 → NOT_FOUND（确定性完整性判据、needs_refinement 原因投影字段/取值、Planner ExecutionPlan schema/policy 校验点、「禁全量点火」细则均无对应章节）｜来源：sched-plan-acceptance.md（A4 第 1 条 / 四·GAPS）
5. `task_scheduler_scan_runs`（§7 P1-B）在 PuddingPlatform 无任何匹配（无 store/表/实体）→ 代码:NOT_FOUND（search_grep ScanRunStore|SchedulerScanRun|scan_runs 零命中），文档:223-264｜来源：sched-plan-acceptance.md（二 反向缺口 / 五 第 2 条）
6. Blocked UI 无代码承载｜来源：sched-plan-acceptance.md（四·GAPS 末条：「文档要求但未实现」（scan_runs、Blocked UI、预算门禁））
7. 预算门禁无代码承载｜来源：sched-plan-acceptance.md（四·GAPS 末条，同上）
8. 文档 §7 验收条目（空扫描落行 / 重启 abandoned / Goodput API）目前全部无代码承载｜来源：sched-plan-acceptance.md（五 第 2 条）
9. 候选评分公式分量定义：文档无各分量公式（仅有「持久化 score breakdown」要求，:191）｜来源：sched-plan-acceptance.md（四·GAPS 第 3 条）
10. sched-runtime-evidence.md 中未见任何 NOT_FOUND / 「无代码承载」表述；该文件整体为占位骨架（PENDING），运行时实测证据缺失｜来源：sched-runtime-evidence.md

## 6. 待证事项与风险

### 6.1 来源：sched-code-evidence.md — CONTRADICTIONS（与 ALREADY_KNOWN 冲突，须标红）

- ALREADY_KNOWN 称「缺 priority/dependency/window/lease/fence/epoch，由 Heartbeat 猜下一步」→ 代码事实：priority 排序(TaskAutoDispatchEvaluator.cs:187)、依赖门(:332)、窗口门(:410)、idle 门(:386)、outbox lease(GoalOutboxStore.cs:37-55)、ActivationEpoch fence(ConversationAcceptanceStore.cs:419)、TaskSchedulingCoordinator(:29) 全部已实现
- ALREADY_KNOWN 隐含「TaskSchedulingCoordinator/scheduler_intent/task_scheduler_intent_outcomes 为新方案新增」→ 三者均已存在（TaskSchedulingCoordinator.cs:29、TaskSchedulerIntentEntity.cs:15、TaskSchedulerIntentOutcomeSchemaBootstrapper.cs:23）
- 开关现值（默认关闭=未开启，非未实现）：TaskAutoDispatch Enabled=false 默认、Mode="shadow" 默认（TaskAutoDispatchEvaluator.cs:16/:27）、EventDrivenEnabled 默认 false（:89）、TaskBoundGoals.Enabled 默认 false（TaskBoundGoalOptions.cs:9）、GoalRuns.Enabled 默认 false、ContinuationEnabled 默认 false（Source\PuddingCore\Goals\GoalRunOptions.cs:15/:24）；authoritative 派发硬前置要求三开关同开（TaskAutoDispatchEvaluator.cs:143-148）

### 6.2 来源：sched-plan-acceptance.md — 四、文档未覆盖的空白（GAPS，后续规划风险点）

- 七项事务清单（Task Reserved/Assignment/Reservation/TaskGoalBinding/GoalRun/first GoalOutbox/events）与 GoalOutbox：全文 NOT_FOUND；最接近为文档:61（repair Serializable 五实体重读）、:99（started=事务创建 fenced 四件套）、:194（事务或等价幂等序列写 Outcome）。
- Backlog Refinement 规约（确定性完整性判据、needs_refinement 原因投影字段/取值、Planner ExecutionPlan schema/policy 校验点、「禁全量点火」细则）：无对应章节，NOT_FOUND；仅 :107 禁全工作区 Evaluate 后全量 Complete、:319 结构化预算编译器。
- 候选评分公式分量定义：文档只有「持久化 score breakdown」要求（:191），无各分量公式；分量实现见代码 TaskSchedulerScoreWeights.cs:67-121。
- 四档×前置依赖（Goal/TaskBound/Verifier）启动校验矩阵：NOT_FOUND（代码侧 TaskAutoDispatchEvaluator.cs:105-127 Validate 校验 mode/ScanInterval/MinimumIdle/CandidateLimit，未覆盖任务描述的依赖矩阵）。
- 「已实现但文档仍按待建写」与「文档要求但未实现」（scan_runs、Blocked UI、预算门禁）并存 ⇒ 文档过时度约「WP2/WP3 大部分已完成、WP4-WP6 待做」，按文档全量排期会重复施工。

### 6.3 来源：sched-plan-acceptance.md — 五、风险与阻塞

- 生产运行时配置（D:\data 运行库/部署机 appsettings）本次只读范围内未取证：「开关挡住」结论仅对 C# 代码默认值成立，仓库 dev appsettings 已全开 authoritative——若生产同样全开，WP2/WP3 的「待施工」判断需整体重估。
- task_scheduler_scan_runs 完全未实现，文档 §7 验收条目（空扫描落行/重启 abandoned/Goodput API）目前全部无代码承载。
- appsettings.json 行号基于 2026-09-13 工作树读数（135 行文件），后续配置改动会使行号漂移。

### 6.4 来源：sched-runtime-evidence.md — 证据完备性阻塞

- 文件自述「状态: 取证进行中（占位骨架，完成后覆盖更新）」：DB_PATH、ROWCOUNT task_scheduler_intents、ROWCOUNT task_scheduler_intent_outcomes、ROWCOUNT GoalOutbox、ROWCOUNT Tasks、BLOCKED_INPROGRESS 六项全部为 PENDING；STATUS_DIST / BOARD_DIST / 证据命令原文不存在。

### 6.5 来源：sched-plan-acceptance.md — 三、文档与任务描述不一致处（待证/歧义）

- 「staged mode 四档」实为五值：disabled/shadow/authoritative-single/authoritative-bounded + 兼容名 authoritative（文档:64、代码 TaskAutoDispatchEvaluator.cs:31-35）；任务描述漏了 authoritative。
- 「两个独立 commit」：文档无「两 commit」表述，框架是 6 个工作包各自独立 commit（文档:327-335）；任务所称两 commit 对应工作包 2（§11.2:350-358）与工作包 3（§11.3:360-366），为其中第 2、3 步，且 WP2 前置 WP1（Tracker/Repair）。
- 章节号核对：任务描述 §5–§6、§11.2–§11.3、§12 与实际目录一致（P0-B:147、P1-A:202、WP2:350、WP3:360、测试:398），无章节号偏差。
- 「唯一启动路径七项 Serializable 事务」：文档无此清单（见 GAP）。

## 7. 来源

> 来源：temp/sched-code-evidence.md、temp/sched-runtime-evidence.md、temp/sched-plan-acceptance.md（均 gitignored，内容已全文并入本报告）
