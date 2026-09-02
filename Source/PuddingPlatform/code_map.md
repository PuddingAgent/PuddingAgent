# PuddingPlatform CodeMAP

> 平台层 | Session 管理 · API · EF Core 持久化 · 消息网关

## 会话管理

| 文件 | 用途 |
|------|------|
| `Services/SessionStateManager.cs` | 🔑 会话状态管理（88KB，核心） |
| `Services/SessionEventStreamService.cs` | 会话事件流 |
| `Services/SessionStateStore.cs` | 会话状态持久化 |
| `Services/SessionSteeringService.cs` | 当前 Turn Steering durable queue；以不可变 `target_turn_id` 精确消费，支持优先级、consume-once、过期与注入状态持久化 |
| `Services/SessionSteeringSchemaBootstrapper.cs` | existing SQLite 原地补 `target_turn_id` 和新索引；无法绑定 Turn 的历史 pending 行 fail closed 为 expired |
| `Services/SessionCompactionEventEmitter.cs` | 压缩事件发射 |
| `Services/SessionTitleService.cs` | 会话标题 |

## 对话 & 聊天

| 文件 | 用途 |
|------|------|
| `Services/ChatHistoryService.cs` | 聊天历史 |
| `Services/ChatMessageRepository.cs` | 消息仓储；ChatMessageRow 透传 `WorkspaceId/MessageId/TurnId` 与 `ContentPartsJson` canonical 信封；after-Id 增量扫描不因空正文越过纯 typed-parts 消息，支持 Runtime 冷水合与当前 Turn 排除 |
| `Services/ChatMessageSchemaBootstrapper.cs` | 存量 SQLite 幂等补 `ChatMessages.content_parts_json` 列 |
| `Services/AgentChat/ExecutionRunCoordinator.cs` | ADR-077 canonical parts；执行前应用 canonical WorkUnit context，将 Agent/WorkUnit rounds、tools、duration 逐项取最严值并冻结 deadline，按实际 provider/model 冻结价格与 input/output/cost 预算，透传 plan/node identity |
| `Services/AgentChat/TurnOutputChunker.cs` | Delta 聚合分块器；非 delta 事件（工具/step）先 flush 已缓冲正文/思考再透传——「文本 → 工具 → 文本」轮次边界进入 canonical sequence（chat 交错时间线依赖，2026-08-24）；测试 `PuddingPlatformTests/Services/TurnOutputChunkerPayloadOwnershipTests.cs` |
| `Services/AgentChat/AgentConversationProjectionService.cs` | Chat 首屏/活动 run/消息明细投影；活动根 run 以最新根 `turn.started` 锚定，避免子代理 runId 抢占；active/full detail 都把 `message.content.appended` 与思考/工具/委派按真实 sequence 返回，并用 `TurnEventWindow` 显式标记 64 条活动窗口边界 |
| `Services/ChatTranscriptWriter.cs` | 转录写入 |
| `Services/ChatTelemetryRecorder.cs` | 遥测记录 |

## 消息网关

| 文件 | 用途 |
|------|------|
| `Services/MessageGateway/` | 🔑 消息网关投影（FeishuImageArtifactProjection 等） |
| `Services/MessageFabric/MessageQueueProjectionService.cs` | 未认领消息队列统一只读投影；默认仅 `message_deliveries queued/retrying` + `chat_execution_commands pending`，认领/运行后转入会话轨迹；`includeTerminal=true` 才返回完整诊断，`queueKind` 区分事实源 |
| `Services/MessageFabric/MessageRouter.cs` / `MessageFabricStore.cs` | Agent 目标按 intent/requires_response 固化 `execute/notify`；稳定 per-target delivery ID；支持按 handling mode 原子批量领取最多 20 条 |
| `Services/MessageFabric/MessageFabricSchemaBootstrapper.cs` | 旧 SQLite 幂等补 `handling_mode` 与索引，并把普通 `inform/report_result/agent_reply` 历史投递回填为被动通知 |
| `Services/Conversation/ConversationNotificationStore.cs` | 被动 Message Fabric 通知的原子受理：每条独立写 `ChatMessage + message.created + ConversationHead`，不创建 Turn/command，提交后唤醒 SSE |
| `Services/MessageGateway/ConversationReplyProjectionWorker.cs` | 从 committed terminal event 投影 Connector 回信；trusted Message Fabric Agent ingress 仅在显式 reply contract 下，以稳定 MessageId 投影一次被动 `agent_reply`，失败重试不重跑 Agent |
| `Services/Conversation/` | 对话接受/投影/事件存储 |
| `Services/Conversation/CreateSteeringHandler.cs` | Steering 单一受理边界；只接受 canonical Running Turn，校验 Workspace/Agent 后写 Runtime 消费队列 |
| `Controllers/Api/ConversationTurnsController.cs` | canonical Turn HTTP API；Steering 为 `POST /api/v1/conversations/{conversationId}/turns/{turnId}/steering`，202/409 fail closed |
| `Services/ConversationEventStore.cs` | 对话事件存储（18KB） |
| `Services/ConversationProjectionWorker.cs` | 对话投影 Worker；活跃流小积压短 coalescing，批量 checkpoint/catalog，避免每个 raw source event 触发 SQLite/日志紧循环 |
| `Services/Execution/SqliteExecutionJournal.cs` | canonical execution journal；开事务前处理 SQLite pooled-handle 激活异常，且只在尚未写入事件时清池并有限重试，避免瞬时连接故障直接终止 Agent turn |
| `Services/MessageTopicService.cs` | 消息主题 |

## Agent 管理

| 文件 | 用途 |
|------|------|
| `Services/WorkspaceAgentFileService.cs` | 🔑 Agent 文件服务（65KB） |
| `Services/AgentTemplateFileService.cs` | 模板文件服务（27KB） |
| `Services/AgentTemplateProvider.cs` | 模板提供 |
| `Services/AgentLLMConfigResolver.cs` | LLM 配置解析；把选中模型的协议写入 `LlmConfig` |
| `Services/AgentRuntimeProfileResolver.cs` | Runtime Profile 解析（16KB） |
| `Services/AgentConversationLogService.cs` | 对话日志 |

## 认证与当前用户

| 文件 | 用途 |
|------|------|
| `Controllers/Api/AuthApiController.cs` | 登录、JWT/Session 当前用户投影；认证成功/失败按 Information 记录且不记录用户标识、密码长度或账户存在性；`/api/currentUser` 异步读取 `AppUsers.Avatar`（空值回退自有 `/admin/assets/images/me.png`），刷新/重登后头像保持数据库最新值 |
| `Controllers/Api/UserAvatarApiController.cs` | 头像唯一上传契约 `POST /api/users/{userId}/avatar`（multipart 字段 `file`，返回 `{ avatar }`）；上传自己需登录、为他人上传需 Admin（403）；统一 PNG/JPG/WebP、5 MiB 上限；`SaveForUserAsync` 复用落盘/写库/旧文件清理；`GET` 匿名查任意用户头像 |
| `Controllers/Api/AppUserApiController.cs` | 用户管理 CRUD/密码/角色，收紧为 `[Authorize(Roles = "admin")]`；`AppUserDto` 携带 `Avatar` |
| `Services/UserAvatarStorageService.cs` | 头像落盘 `wwwroot/user-avatars/`（userId 前缀防穿越、原子写、TryDelete 限根内）；允许 MIME 仅 PNG/JPEG/WebP（GIF 已移除） |
| `Services/Sm2JwtSigner.cs` | ECDSA-P256 JWT payload 签名；缺少持久密钥时仅记录进程临时密钥告警与公钥 SHA-256 指纹，禁止日志输出私钥/公钥材料 |

## 子代理 & 诊断

| 文件 | 用途 |
|------|------|
| `Middleware/TraceableExceptionMiddleware.cs` | 未处理异常生成可检索 errorId/500；仅当 `RequestAborted` 已取消时把 `OperationCanceledException` 视为客户端断开（499 + Debug），不污染 Error 日志 |
| `Services/SubAgentManager.cs` | 子代理管理；固化系统预算/收尾宽限；managed `workspace-task-agent`/TaskPlan WorkUnit 强制钳制为最多 40 rounds/120 tools，普通显式大任务仍服从系统 600/2400 护栏；以同一 SubSessionId + 新 runId 透明续跑并重置计数器；终态 usage 只存运行摘要 |
| `Services/SubAgentPool.cs` | Core `ISubAgentPool` 的 Platform 子代理池实现 |
| `Services/SubAgentTransientDirectoryGcService.cs` | 历史临时执行身份空 Skill 脚手架 GC；以精确目录形状 + 子代理池 + durable run 终态多重门禁，先移入 retention-archive 隔离，延迟后再安全删除 |
| `Services/SubAgentDiagnosticsService.cs` | 子代理诊断 |
| `Services/FileSubAgentRunStore.cs` | 子代理运行文件存储；终态 `run.json` 固化轮次、工具、耗时和错误；支持可恢复终态 `budget_exhausted` 与预算通知投影；归档读写同一 per-run gate + sharing violation 退避重试，重试耗尽写 archive-degraded.json 降级（ADR-060 §3.11） |
| `Controllers/Api/SubAgentRunController.cs` | 认证运行检查器 API；详情从归档返回终态统计，events 分页返回可重建历史时间线的完整事件 payload |
| `Services/SessionStateManager.cs` | 会话/子代理持久状态查询；子代理状态 DTO 按可复用 SubSessionId 关联最新 canonical runId，供托盘坞和检查器在漏收事件后恢复运行 |

## 任务系统（Tasks）

| 文件 | 用途 |
|------|------|
| `Services/Tasks/SqliteWorkspaceTaskStore.cs` | SQLite Task Ledger：`workspace_tasks` + `task_events` 两表、snake_case 列、CAS 乐观并发、Task+Event 原子提交、结构化 taskType/capability/provider/model/fallback 与 auto-dispatch opt-in、硬删语义、keyset 分页 |
| `Services/Tasks/WorkspaceTaskSchemaBootstrapper.cs` | Task 表/事件/评论启动建表；对既有 SQLite 幂等补齐结构化路由与 `auto_dispatch_enabled` 列 |
| `Services/Tasks/TaskAgentCommandService.cs` | task_* 工具命令服务：claim/update 原子写回 Task+Attempt+Event+Binding 四表 |
| `Services/Tasks/TaskCommandService.cs` | PATCH/ApplyCommand 原子语义；无 Assignment 的人工完成写 `TaskCompleted/manual_without_execution`，active Assignment 禁止 PATCH 伪完成，`mark_failed` 原子释放 attempt；`DeleteTaskAsync` 智能删除 |
| `Services/Tasks/TaskDispatcher.cs` | 任务派发（RuntimeDispatchRequest.ActiveTask 注入到派发链）；发送前重验 Task/Assignment owner，stale 与确定性终态冲突 dead-letter，其他失败受 MaxAttempts 限制 |
| `Services/Tasks/TaskDispatchOutboxStore.cs` | 派发 outbox 持久化 |
| `Services/Tasks/TaskDispatchSchemaBootstrapper.cs` | 派发 schema 幂等建表 |
| `Services/Tasks/TaskDispatchSerialization.cs` | 派发序列化 |
| `Services/Tasks/TaskDependencyStore.cs` | finish-to-start Task 依赖图；同 Workspace 校验、幂等增删、环检测与 Satisfied/Waiting/Broken 评估 |
| `Services/Files/SqliteProviderFileRefStore.cs` | ADR-077 V3-S2b-1 `IFileRefStore` SQLite 实现（llm_provider_file_refs）：原始 SQL + 参数化、`ON CONFLICT DO UPDATE` 幂等 upsert、BEGIN IMMEDIATE + status CAS 并发防重复、近过期（<300s）不分配；RemoteFileId 只存不打印 |
| `Services/Files/ProviderFileRefSchemaBootstrapper.cs` | ADR-077 V3-S2b-1 `llm_provider_file_refs` 幂等建表（唯一主键 + status/expires_at 索引）|
| `Services/Tasks/TaskWireMaps.cs` | 枚举↔wire 双向映射 + ErrorCode→wire/HTTP |
| `Services/Tasks/ManualAlwaysAllowFence.cs` | manual always allow fence |
| `Controllers/Api/TaskController.cs` | Control Plane 13 端点 + `GET /tasks/watch` SSE（快照+游标+Last-Event-ID）+ boardColumn 五列过滤；`DELETE` 智能删除返回 200 deleted/archived |
| `Controllers/Api/TaskSchedulingController.cs` | 认证调度诊断：Agent Availability query/rebuild、Auto evaluate-only、Task 依赖增删与评估 |
| `Controllers/Api/TaskDtos.cs` | 8 个 wire DTO |
| `Data/Entities/WorkspaceTaskEntity.cs` | `workspace_tasks` 实体（28 列）|
| `Data/Entities/TaskEventEntity.cs` | `task_events` 实体（long Id 自增 + 18 业务列）|
| `Data/Entities/TaskAssignmentAttemptEntity.cs` | `task_assignment_attempts` 实体 + partial unique index（task_id WHERE released_at_utc IS NULL）|

## Agent Availability 与自动派发（Services/Scheduling/，2026-08-26）

| 文件 | 用途 |
|------|------|
| `Services/Scheduling/AgentAvailabilityProjectionStore.cs` | 从配置、Task/Goal、Chat command、Message delivery、SubAgent 与 Reservation 的持久事实保守重建 Agent 状态；仅 canonical active assignment 的非终态 Task 占用 Agent，终态历史脏 attempt 不再造成 false-busy；Unknown/过期不接 Auto |
| `Services/Scheduling/AgentExecutionReservationStore.cs` | 单 Agent/Task active 自动工作槽、lease、fencing token、renew/release/expiry |
| `Services/Scheduling/ConservativeExecutionWindowResolver.cs` | `anytime` allow；`inherit/off_peak_only` 在路由价格档案缺失时 Unknown/fail-closed |
| `Services/Scheduling/ProviderModelExecutionWindowResolver.cs` | 生产 Resolver；按 Agent 实际 provider/model 和 `llm.providers.json` 版本化价格窗口解析时区/跨午夜/边界；`inherit/off_peak_only` 未知即 fail closed |
| `Services/Scheduling/TaskAutoDispatchEvaluator.cs` | 无副作用确定性候选评估；每轮每 Agent 只重建一次 Availability 并让全部候选共享同一 version fence；结构化 TaskTypeRoute/能力/provider/model、首选亲和、显式 fallback、依赖、5 分钟 idle grace、窗口与同轮单 Agent 单任务 |
| `Services/Scheduling/TaskAgentRouteMatcher.cs` | 不读任务标题的确定性 Agent 路由；类型规则与任务显式约束取交集，输出 provider/model/capability 解释和 SHA-256 快照；投影 CreatedAt/UpdatedAt 不进入原子路由指纹 |
| `Services/Scheduling/TaskExecutionPlanCompiler.cs` | 不读任务正文的纯 WorkUnit 计划编译器；按 taskType 生成有界 DAG，将依赖/能力/冲突范围/预算冻结为 SHA-256；未知类型 fail closed |
| `Services/Scheduling/TaskBacklogRefinementEvaluator.cs` | 每五分钟只读检查已 opt-in Backlog 的描述、验收标准、任务类型与兼容 Agent；Shadow 输出 ReadyCandidate/NeedsRefinement，不改状态 |
| `Services/Scheduling/TaskBacklogRefinementStore.cs` | future authoritative 的 Backlog→Ready 唯一 CAS 写入者；重验任务、Agent、TaskTypeRoute 与路由 SHA-256，原子写 canonical `TaskReady/backlog_refined` |
| `Services/Scheduling/TaskExecutionTracker.cs` | 五分钟只读关联 Task/Plan/当前 WorkUnit/Assignment/Reservation fencing/Binding/Goal/Iteration/ExecutionCommand/Run/outbox；同时跟踪 legacy Delivery→Execution 断链；输出 Healthy/Waiting/Stalled/Inconsistent/CleanupRequired；Blocked Goal 仍持 active binding 为 `blocked_binding_still_active`，Delivery 已确认但超时无 execution claim 为 `legacy_assignment_execution_missing`，终态 Delivery 无 execution 为即时 cleanup |
| `Services/Scheduling/TaskExecutionRepairCoordinator.cs` | authoritative 五分钟确定性 repair；Serializable 重读 fence 后清理终态或 Blocked Goal 遗留 binding/assignment/reservation，以及超时未被 execution claim 的 legacy assignment（Task 保持 Blocked）；回收过期 continuation lease、补建安全可证明缺失的 continuation intent；不猜 Task 成功、不续过期 reservation、不合成 Turn |
| `Services/Scheduling/TaskAutoDispatchWorker.cs` + `TaskAutoDispatchScanRunner.cs` | `IOptionsMonitor` 驱动的低频恢复扫描；周期轮次与 Admin 立即扫描复用同一 runner，严格按 tracking/repair → 全 Agent Availability 重建 → Backlog refinement/Ready route → dispatch；按 workspace gate 串行并输出结构化摘要 |
| `Services/Scheduling/TaskSchedulerControlService.cs` + `Controllers/Api/TaskSchedulingController.cs` | Admin 调度控制面：权威 status、revision CAS 策略热加载、workspace pause/resume、立即 scan/repair；原子写回 `<DataRoot>/config/system.json` 的 `taskAutoDispatch`，不创建浏览器状态机，控制端点限 admin |
| `Services/Scheduling/TaskBoundGoalOptions.cs` | Task-bound Goal 独立安全开关、Iteration 预算与 Reservation lease（默认关闭） |
| `Services/Scheduling/TaskSchedulingSchemaBootstrapper.cs` | Availability、Reservation、Task dependency 三表与唯一索引幂等建表 |
| `Services/Scheduling/TaskSchedulerIntentStore.cs` | P0 事件驱动层 durable intent 队列（task_scheduler_intents）：INSERT OR IGNORE 幂等入队、事务内单 UPDATE 抢占式 Dequeue（pending/过期 lease 回收+attempt 自增）、Complete/Fail（超限→dead）、GetTailCursor；时间列固定宽度 UTC TEXT 保证 SQL 字典序=时间序 |
| `Services/Scheduling/TaskSchedulerIntentSchemaBootstrapper.cs` | task_scheduler_intents 幂等建表（UNIQUE(source,source_event_id) 等 4 索引），注册于 PuddingApplicationInitializer |
| `Services/Scheduling/TaskAutoDispatchStarter.cs` | 事件驱动派发启动器：围栏字段校验+二次 window fence+原子 StartAsync+LostRace 容忍；从动态策略读取 MaxStarts/MinimumIdle |
| `Services/Scheduling/TaskEventLedgerTailBridge.cs` | 账本尾游标桥：IntentPollInterval 轮询 task_events/conversation_events 新行（游标=账本 MAX 懒初始化，不回放历史），按事件清单过滤入队 intent；动态响应 enabled/event/mode/pause，shadow 只推进游标不入队 |
| `Services/Scheduling/TaskSchedulingCoordinator.cs` | 事件驱动协调器（authoritative-only）：Dequeue→按 workspace 合并→goal 终态先重建 Availability→Evaluate→Starter 派发→Complete/Fail（超限 dead）；动态 pause 后不消费该 workspace intent |
| `Data/Entities/AgentAvailabilityProjectionEntity.cs` | 持久 Availability 投影实体 |
| `Data/Entities/AgentExecutionReservationEntity.cs` | 自动工作租约与单调 fencing 实体 |
| `Data/Entities/TaskDependencyEntity.cs` | Task finish-to-start 依赖边实体 |

## Goal 持久控制面（Services/Goals/ · ADR-074 G1–G3 + Task-bound 原子启动源码链）

| 文件 | 用途 |
|------|------|
| `Services/Goals/GoalSchemaBootstrapper.cs` | goal_runs/goal_iterations/goal_outbox/goal_verifications/task_goal_bindings 五表幂等建表；含"单会话一个非终态 Goal" partial unique 与 outbox 幂等键索引（G1 冻结全部 schema） |
| `Services/Goals/GoalRunStore.cs` | 聚合写入原语：Create/TryMutate（CAS + Func 卫兵）与 goal.* ConversationEvent 同事务直写；提供按 sequence 选择当前非终态 WorkUnit 的只读查询 |
| `Services/Goals/GoalOutboxStore.cs` + `GoalOutboxSignal.cs` | continuation due/claim/lease/fencing/recovery/defer/suppress/dead-letter；signal 只降延迟 |
| `Services/Goals/GoalContinuationWorker.cs` | durable intent → 受信 synthetic Acceptance；用户 Turn 优先；从 Binding 解析当前 WorkUnit，将 plan/node/fingerprint/预算放入受信 Acceptance 与 prompt；payload JSON 保留可读 Unicode，同时继续转义 HTML 敏感字符以保护 envelope 边界 |
| `Services/Goals/GoalSettlementStore.cs` + `GoalSettlementWorker.cs` | canonical Turn 全窗口终态判定 → 最新 128 条有界 Evidence Capsule → version/epoch/Task/Reservation gates → 下一 outbox 或终态；主 Turn canonical usage 加当前 Turn 时间窗内递归子会话 TokenUsageEvents，连同 Run/耗时/工具聚合到 Iteration/Goal；普通 Goal 阻塞仍可恢复，Task-bound 阻塞尝试以 Failed 保留审计并释放 Binding/Assignment/Reservation |
| `Services/Goals/ConservativeGoalIterationVerifier.cs` | fail-closed 只读 Verifier；自然语言完成无权写终态，Task canonical Completed 才允许 bound Goal 完成 |
| `Services/Goals/TaskGoalDispatchTransactionStore.cs` | Task/ExecutionPlan/WorkUnits/Assignment/Reservation/Binding/Goal/首个 Outbox/事件/Availability 单 Serializable 事务与幂等 replay；事务前重读 Agent/类型规则并重算 route/plan 双 SHA-256，任一漂移 fail closed；派发时原子退役同 Workspace/Agent/会话且 terminal Task binding 的遗留 Blocked Goal（可来自前一 Task），释放 active-Goal 唯一索引，并将 SQLite 约束详情写入诊断日志 |
| `Services/TaskPlanning/TaskPlanningSchemaBootstrapper.cs` + `Data/Entities/TaskPlanRunEntity.cs` / `TaskNodeEntity.cs` / `WorkUnitAwaitHandleEntity.cs` | 复用规划表冻结 WorkspaceTask version 对应的执行快照、WorkUnit budgets/scopes/dependencies/checkpoint 与 durable AwaitHandle；启动初始化器显式幂等升级旧 SQLite |
| `Services/ConversationAcceptanceStore.cs` | Chat/Goal synthetic Turn 原子受理；重验 Goal/outbox/Task/Assignment/Reservation/Plan/当前 WorkUnit 全围栏并原子置 Running；lease 校验/续租统一使用注入 `TimeProvider` |
| `Services/ExecutionCommandReader.cs` | 执行前沿 Command→GoalIteration→Binding→Task/Reservation→Plan/Node 重读 canonical WorkUnit 身份与预算；metadata 只选择、不授权，漂移 fail closed |
| `Services/Goals/GoalCommandService.cs` | /goal 全命令合同：set/edit/replace/pause/resume/cancel/clear/status；conflict、幂等重放（source_command_id 唯一）、expectedVersion、budget_exhausted 不可 resume、feature flag 下保留 status/pause/cancel |
| `Services/Goals/GoalQueryService.cs` | 只读投影（active/latest/iterations） |
| `Services/Goals/GoalRestartReconciler.cs` | 启动 disarm：active→paused（bootId 锚点 + goal.paused 事件），幂等 |
| `Controllers/Api/GoalCommandsController.cs` | POST /api/v1/conversations/{id}/goals/commands（结构化命令） |
| `Controllers/Api/GoalQueriesController.cs` | GET /goal、/api/v1/goals/{id}、/goals/{id}/iterations |
| `Data/Entities/Goal*Entity.cs` + `TaskGoalBindingEntity.cs` | 五张表实体（枚举 int、snake_case、version CAS） |

关联修改：`SystemCommandHandler`（/goal 分支委托 GoalCommandService，不创建 Turn）；`PlatformDbContext`（5 个 DbSet + partial unique 索引）；`PuddingApplicationInitializer`（GoalSchemaBootstrapper + 启动 disarm）。

## 外部访问令牌与 Agent 消息 API（ADR-075 / ADR-082）

| 文件 | 用途 |
|------|------|
| `Services/Security/ExternalAccessTokenStore.cs` | Token 持久化：`external_access_tokens` + scopes/workspaces/audit 四表、CAS rename/revoke、按 keyId 索引查询、last-used 合并写落库 |
| `Services/Security/ExternalAccessTokenService.cs` | 领域服务：RNG 生成 `pdt_v1_<keyId>.<secret>`、SHA-256 摘要固定时间比较、生命周期规则（默认 90d/上限 365d/每人 Active 上限）、认证 fail-closed（malformed/unknown/bad-secret/revoked/expired/owner-disabled）、auth-fail 节流审计 |
| `Services/Security/ExternalAccessTokenHandler.cs` | `PuddingExternalAccessToken` ASP.NET Core 认证 scheme（AuthenticationHandler）：Header 解析 → 验证 → ClaimsPrincipal（无 admin role）；成功投递 last-used 合并器 |
| `Services/Security/ExternalAccessTokenAuthorization.cs` | ExternalScopeRequirement/ExternalWorkspaceRequirement + Policy 名称；handler 校验 scheme 身份 + scope/workspace claim（ordinal）；ADR-082 增加 workspaces/agents/messages Policies |
| `Services/Security/ExternalAccessTokenUsageCoalescer.cs` | last-used 有界合并写（首次立即、之后每 5 分钟至多一次；停机 force flush）|
| `Services/Security/ExternalAccessTokenSchemaBootstrapper.cs` | 四张 Token 表幂等建表（与 EF 实体列名一致）|
| `Services/Security/ExternalTaskApiOptionsProvider.cs` | `config/system.json` externalTaskApi 节读取（30s 缓存）+ 启动期越界校验 |
| `Controllers/Api/AdminAccessTokenController.cs` | JWT-admin-only 管理 API：status/list/create（明文仅 201 一次）/detail/rename(CAS)/revoke(CAS)；不提供 reveal/unrevoke/删除/扩权 |
| `Controllers/External/V1/ExternalTokenInfoController.cs` | `GET /api/external/v1/token` whoami 自检（ExternalApiGateFilter 门控）|
| `Controllers/External/V1/ExternalTaskController.cs` | External Task API v1（ADR-075 P2 基本功能）：list/get/create/patch(If-Match→CAS，428/412+currentTask 快照)/comments/evaluations/commands(白名单)；Actor=access-token:{tokenId}、Origin=external.api 注入；mutation 要求 Idempotency-Key；无 delete；RateLimiter/SSE Watch/OpenAPI 未实现 |
| `Controllers/External/V1/ExternalWorkspaceAgentController.cs` | ADR-082：授权 Workspace/Agent 安全目录；消息以 connector/access-token actor 进入 Message Fabric，强制 Idempotency-Key；`202 + Location` 与 Token-owned receipt 分离 delivery acceptance 和 canonical Agent terminal reply |
| `Controllers/External/V1/ExternalApiGateFilter.cs` | External API 门控：Enabled=false → 404；非 Loopback 明文 HTTP → 400 |
| `Controllers/External/V1/ExternalTaskDtos.cs` / `ExternalWorkspaceAgentDtos.cs` | V1 稳定 wire DTO（与 Internal DTO/EF Entity 分 namespace）；Workspace/Agent 投影排除成员、Profile、Prompt、MainSessionId 和 Secret |
| `Services/ExternalApi/TaskEvaluationStore.cs` | 追加式评价：task_evaluations + task.evaluated 事件同事务；score/verdict/taskVersionObserved/supersedes 校验；不改 Task 状态/version |
| `Services/ExternalApi/ExternalApiIdempotencyStore.cs` | 简化幂等：key=SHA-256(token+method+route+key)、claim-then-execute、replay/409/失败释放、保留期顺带清理 |
| `Services/ExternalApi/ExternalTaskApiSchemaBootstrapper.cs` | task_evaluations + external_api_idempotency 幂等建表 |
| `Data/Entities/ExternalAccessToken*.cs` | 主表/scope/workspace/audit 四实体（复合主键联结 + append-only 审计）|
| `Data/Entities/TaskEvaluationEntity.cs` / `Data/Entities/ExternalApiIdempotencyEntity.cs` | 评价 + 幂等实体 |
| 测试 | `PuddingPlatformTests/Security/ExternalAccessToken*Tests.cs` + `Controllers/ExternalTaskApiV1Tests.cs` + `Controllers/ExternalWorkspaceAgentApiV1Tests.cs` + 评价/幂等 Store 测试；ADR-082 新增 5 项，External API 相关聚焦回归 45/45 |

## 持久化

| 文件 | 用途 |
|------|------|
| `Data/` | EF Core DbContext、实体、迁移 |
| `Data/PlatformSqliteConnectionInterceptor.cs` | Platform SQLite 连接初始化；第一条安装 30 秒 `busy_timeout`，再执行其余连接 PRAGMA，避免连接设置本身在 writer 竞争时过早失败 |
| `Migrations/` | EF Core 迁移 |
| `DesignTimeDbContextFactory.cs` | 设计时工厂 |
| `Services/Orchestration/AgentOrchestrationSchemaBootstrapper.cs` | 通用编排 graph/revision/layout/run/run-input/node-run/event SQLite 表与索引幂等初始化；幂等补齐 node-run `outputs_json` 按端口输出列 |
| `Services/Orchestration/SqliteAgentOrchestrationStore.cs` | Graph/Run 分页发现、修订与独立布局 CAS、无 Run Graph 的 Head-CAS 删除、Run Input/按端口 Output 冻结、真实 child Run/SubSession、原子 claim/fence、lease 恢复和事件读取；terminal commit 会按无 predicate 的边原子推进后继 Ready/Skipped，并把最后节点与 Run 终态事件同事务提交 |
| `Services/Orchestration/AgentOrchestrationAuthoringService.cs` | Admin Revision 写入编排；校验 graphId/base/head，调用 Core compiler 规范化定义，以 Head CAS 保存新不可变 Revision，审计字段由服务端生成 |
| `Services/Orchestration/AgentOrchestrationManualRunService.cs` | Admin 手动运行命令；要求显式不可变 revisionId，把类型化输入冻结后幂等 Create/Activate，不解析 Graph Head |
| `Services/Orchestration/AgentOrchestrationHttpHookService.cs` | Admin 调试型 HTTP Hook：显式固定不可变 Revision，受限 JSON path 映射为 Graph Inputs，以 sourceEventId 生成确定性 Run 并幂等 Create/Activate；不解析 Head、不冒充 Deployment |
| `Services/Orchestration/AgentOrchestrationCommittedEventSignal.cs` | committed-after-transaction 进程内唤醒；业务数据仍从 SQLite 读取 |
| `Services/Orchestration/AgentOrchestrationEventFollower.cs` | 持久化高水位 replay → retained signal → live 的连续事件读取，检测 sequence gap |
| `Controllers/Api/AgentOrchestrationApiController.cs` | 登录态只读 Graph/Run 发现、catalog/revision/run/event API 与 `Last-Event-ID` SSE Watch |
| `Controllers/Api/AgentOrchestrationLayoutApiController.cs` | 布局读取与 Admin-only CAS 写入；不持有运行写命令端点 |
| `Controllers/Api/AgentOrchestrationManagementApiController.cs` | Admin-only Graph 新建/删除；支持 blank 占位图与 `生成图片 → 展示图片` image-generation 模板，删除拒绝清理任何有 Run 历史的 Graph |
| `Controllers/Api/AgentOrchestrationRevisionApiController.cs` | Admin-only Draft validate 与 Revision PUT CAS；请求先以编排专用 Web/string-enum JSON 契约反序列化，校验返回稳定 elementType/elementId/portId 诊断，冲突返回当前 Revision 事实 |
| `Controllers/Api/AgentOrchestrationRunCommandApiController.cs` | `POST /api/orchestrations/runs`；Admin-only、1 MiB 请求上限、显式 Revision/type-safe inputs、201/200 幂等回执与稳定 400/404/409 错误 |
| `Controllers/Api/AgentOrchestrationHttpHookApiController.cs` | `POST /api/orchestrations/hooks/{graphId}/{triggerId}?revisionId=...`；Admin-only、1 MiB 请求上限、201/200 幂等回执与稳定 400/404/409 错误 |
| `Services/RetentionPruningService.cs` | platform.db 唯一在线保留期裁剪 BackgroundService；覆盖 telemetry_metric_events/runtime_activity/conversation_events，证据事件先归档后删除，表名/列名白名单防注入；100 行小批、批间让步、单轮批数上限，VACUUM 默认关闭，ChatMessages 永不裁剪 |

## 多媒体

| 文件 | 用途 |
|------|------|
| `Services/ImageGenerationService.cs` | 图片生成 |
| `Services/VisionArtifactStorageService.cs` | 视觉存储；ADR-077：magic bytes/真实尺寸（PNG/JPEG/WebP 头部嗅探）、SHA-256 与字节数入 metadata、50MiB 上限、内容身份为准 |
| `Services/VisualArtifactObservationService.cs` | 视觉观察 |
| `Services/AudioArtifactStorageService.cs` | 音频存储 |
| `Services/AudioTranscriptionService.cs` | 音频转录 |
| `Services/VoiceSynthesisService.cs` | 语音合成 |

## 提供商配置

| 文件 | 用途 |
|------|------|
| `Services/LlmProviderFileService.cs` | LLM Provider/模型文件配置；协议只存在于模型 DTO 与模型写入请求；`GetBalanceAsync` 余额查询——解析 apiKey（ApiKey/${ENV}/{{vault:NAME}}/ApiKeyRef→KeyVault）后按 `ILlmBalanceProvider` 注册表 CanHandle 分发，未注册适配器返回「暂不支持」DTO（apiKey 不进日志） |
| `Services/ILlmBalanceProvider.cs` | 服务商余额查询适配器契约（多服务商计费抽象）：`CanHandle(provider)` + `QueryAsync(provider, apiKey, ct)`；网络错误抛 HttpRequestException（控制器映射 502），上游业务错误返回 IsAvailable=false DTO |
| `Services/DeepSeekLlmBalanceProvider.cs` | DeepSeek 适配器：GET {baseUrl 剥掉尾部 /v1}/user/balance + Bearer；解析 is_available/balance_infos（字符串金额兼容）与 error.message；CanHandle=providerId 含 deepseek 或 baseUrl 指向 deepseek.com；命名 HttpClient `LlmBalanceQuery`（30s） |
| `Controllers/Api/LlmProviderApiController.cs` | Provider CRUD/配额/余额 HTTP 出口；`GET api/llm/providers/{providerId}/balance`（KeyNotFound→404 / InvalidOperation→400 / HttpRequestException→502） |
| `Services/ChannelConfigurationFileService.cs` | 渠道配置（21KB） |
| `Services/VoiceProviderFileService.cs` | 语音提供商（18KB） |

余额链路测试：`PuddingPlatformTests/Services/DeepSeekLlmBalanceProviderTests.cs`（8 用例：解析//v1 剥离/Bearer/非 2xx/网络错误/CanHandle 矩阵）+ `LlmProviderBalanceDispatchTests.cs`（4 用例：暂不支持/委托与密钥/404/400）；扩展步骤见 `Docs/Features/服务商余额查询与多服务商计费适配器设计方案.md`。

## Token 计量

| 文件 | 用途 |
|------|------|
| `Services/TokenUsageRecorder.cs` | Token 用量记录；持久化 RuntimeExecutionIdentity 提供的 parent/sub-agent、零基 round、本轮 canonical 工具及 context layer Token/UTF-8/GZIP/hash/cache 诊断；prefix-v2 在 system/tool 不变而 PrefixHash 变化时归因为 `history_anchor_changed`，版本切换归因为 `serialization_version_changed`；不复制 prompt 正文 |
| `Services/CacheDiagnosticsService.cs` | 会话级 Cache Miss Inspector 后端；汇总 token-weighted hit/miss、prefix churn、首次变化原因与逐轮事实 |
| `Services/ConversationProjector.cs` | Conversation Event 增量投影；usage 仅在 direct `session:trace:round` 行缺失时补记，SQLite 查询先按稳定 route/token 指纹取最近 32 行、再在内存应用 DateTimeOffset 窗口，避免查询翻译失败后双记账；父子身份来自持久关系、零基 round 来自 invocation index，未知工具数保持 NULL |
| `Services/TokenUsageEventRepository.cs` | Token 事件持久化与最近层级/熵诊断查询；向 Runtime 返回 Core 诊断 DTO |
| `Services/LlmGatewayUsageRecorder.cs` | Provider 成功边界逐请求计费账本；与会话归因投影解耦 |
| `Data/Entities/LlmGatewayUsageEventEntity.cs` | `llm_gateway_usage_events` 本地计费事实；sourceId 唯一 |
| `Services/TokenUsageSchemaBootstrapper.cs` | 旧 SQLite 的 Token 字段/索引、context-layer UTF-8/GZIP 指标列与网关账本幂等升级 |
| `Services/AppUserSchemaBootstrapper.cs` | 旧 SQLite 的 `AppUsers.Avatar` 幂等补列；避免头像实体升级后登录查询因 schema 漂移返回 500 |
| `Services/TokenUsageRebuildService.cs` | 从成功网关活动 + session usage 帧重建计费事实，并保留无法覆盖的实时行；提交后按月失效按日聚合缓存 |
| `Controllers/Api/StatsApiController.cs` | 月度/趋势优先网关计费账本，无网关历史月份回退会话投影；context-layer API 聚合 Token、UTF-8/GZIP 字节、压缩比、缓存与变化指标；三接口（monthly/series/context-layers）走闭日缓存 + 当天实时渐进加载 |
| `Services/TokenUsageDailyAggregateService.cs` | Token 统计按日聚合缓存：已结束 UTC 日聚合一次落 `llm_usage_daily_aggregates`（day × source × provider × model），当天实时；Rebuild 后按月失效 |
| `Services/ContextLayerDailyRollupService.cs` | Context-layer 按日 rollup 缓存：`context_layer_daily_rollups` 存 JSON 分布（token/命中率数组 + 去重哈希集合），跨日精确合并 median/P95/distinctHashes；非对齐边界日直查明细 |
| `Services/DailyCacheUtility.cs` | 按日缓存共用工具：cache_key、UTC 日枚举、闭日标记读取、SQLite DateTimeOffset 文本范围格式（EF 无法翻译 DateTimeOffset 参数比较） |
| `Data/Entities/LlmUsageDailyAggregateEntity.cs` | `llm_usage_daily_aggregates` 闭日 Token 聚合行 |
| `Data/Entities/ContextLayerDailyRollupEntity.cs` | `context_layer_daily_rollups` 闭日层级分析 rollup |
| `Data/Entities/StatsDailyCacheDayEntity.cs` | `stats_daily_cache_days` 闭日完成标记（cache_key × day，含零数据日） |
| `Services/TokenCostService.cs` | 成本计算 |

## 测试

`../PuddingPlatformTests/` — 渠道配置、Artifact、消息与通用编排；2026-08-11 Orchestration 定向测试 62/62 ✅，覆盖 Graph/Run 发现、Revision/Layout CAS、Draft validate、Graph 生命周期、冻结 Run Inputs、手动运行、后继 Ready/失败 Skipped 与 Run 原子终态、两节点图片模板、HTTP Hook 映射/幂等冲突；2026-08-22 新增 StatsApiController/TokenUsageDailyAggregate/ContextLayerDailyRollup/TokenUsageRebuild 定向 18/18 ✅（闭日缓存命中、空日完成标记、当天实时不落缓存、按月失效、rollup 跨日精确合并、非对齐边界直查）
