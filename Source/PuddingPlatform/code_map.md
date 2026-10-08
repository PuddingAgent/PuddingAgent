# PuddingPlatform CodeMAP

> 平台层 | Session 管理 · API · EF Core 持久化 · 消息网关

## 会话管理

| 文件 | 用途 | 关键符号 | 关联 | 约束 |
|------|------|------|------|------|
| `Services/SessionStateManager.cs` | 会话状态与事件扇出 | `SessionStateManager` / `SessionChannelFanout` / `SessionEventBatchBuffer` | `Services/SessionStateStore.cs` / `Services/SessionEventStreamService.cs` | 88KB 核心（1757 行），状态与扇出混在一处；ctor 末两个依赖是可选参数（`rawLogMirror` / `configuration`），不得改成必填 |
| `Services/SessionEventStreamService.cs` | 会话事件流的订阅起点与有界追赶 | `SessionEventStreamService` / `IConversationEventStore` / `ICommittedEventSignal` | `Services/SnapshotRequiredCheck.cs` | 无游标 → live-only（历史交给 `/bootstrap` 快照），显式游标含 0 → 回放；追赶有界：先订阅再读 head，冻结 `replayThrough`/`drainThrough` 后批内不刷新 head |
| `Services/SnapshotRequiredCheck.cs` | 快照必需判定 | `SnapshotRequiredCheck.HasMissingEvents` | `Services/SessionEventStreamService.cs` | 判据是 `cursor + 1 < minAvailableSequence`（序号从 1 起，显式游标 0 与 min=1 之间不算缺失）；不得改回「低于最小可用序号」，否则合法的全量回放会被判成需要快照 |
| `Services/SessionStateStore.cs` | 会话状态持久化 | `SessionStateStore` | `Services/SessionStateManager.cs` | — |
| `Services/SessionSteeringService.cs` | 当前 Turn 的 Steering 队列 | `SessionSteeringService` / `CreateSessionSteeringMessage` | `Services/SessionSteeringSchemaBootstrapper.cs` | 以不可变 `target_turn_id` 精确消费、同一条只被目标 Turn 取走一次；带优先级与过期，注入状态需持久化 |
| `Services/SessionSteeringSchemaBootstrapper.cs` | Steering 表的存量库迁移 | `SessionSteeringSchemaBootstrapper` | `Services/SessionSteeringService.cs` | 在既有 SQLite 上原地补 `target_turn_id` 与索引；无法绑定 Turn 的历史 pending 行 fail closed 为 expired，不回填、不猜目标 |
| `Services/SessionCompactionEventEmitter.cs` | 压缩事件发射 | `SessionCompactionEventEmitter` | `Services/ConversationEventStore.cs` | — |
| `Services/SessionTitleService.cs` | 会话标题生成 | `SessionTitleService` | — | — |

## 对话 & 聊天

| 文件 | 用途 |
|------|------|
| `Services/ChatHistoryService.cs` | 聊天历史 |
| `Services/ChatMessageRepository.cs` | 消息仓储；ChatMessageRow 透传 `WorkspaceId/MessageId/TurnId` 与 `ContentPartsJson` canonical 信封；after-Id 增量扫描不因空正文越过纯 typed-parts 消息，支持 Runtime 冷水合与当前 Turn 排除 |
| `Services/ChatMessageSchemaBootstrapper.cs` | 存量 SQLite 幂等补 `ChatMessages.content_parts_json` 列 |
| `Services/AgentChat/ExecutionRunCoordinator.cs` | ADR-077 canonical parts；执行前应用 canonical WorkUnit context，将 Agent/WorkUnit rounds、tools、duration 逐项取最严值并冻结 deadline，按实际 provider/model 冻结价格与 input/output/cost 预算，透传 plan/node identity；V5：构建 `CallerLlmSnapshot` 时下发 `snapshot.VisionPolicy`（:212），视觉策略随执行快照单源冻结 |
| `Services/Snapshot/AgentExecutionSnapshotFactory.cs` | ADR-059/077 执行快照组装；V5-T2：视觉策略不再硬编码 `VisionRequestPolicy.Default`，按「配置合同 ∩ 模型类别」解析（embedding / image-generation / 无 vision 标签一律 null 不误标；vision 模型无合同回退产品默认策略），策略值与版本进快照哈希与创建日志（`visionPolicy=` / `visionPolicySource=`）；旧快照直接复用，冻结语义不变 |
| `Services/AgentChat/TurnOutputChunker.cs` | Delta 聚合分块器；非 delta 事件（工具/step）先 flush 已缓冲正文/思考再透传——「文本 → 工具 → 文本」轮次边界进入 canonical sequence（chat 交错时间线依赖，2026-08-24）；测试 `PuddingPlatformTests/Services/TurnOutputChunkerPayloadOwnershipTests.cs` |
| `Services/AgentChat/AgentConversationProjectionService.cs` | Chat 首屏/活动 run/消息明细投影；活动根 run 以最新根 `turn.started` 锚定，避免子代理 runId 抢占；active/full detail 都把 `message.content.appended` 与思考/工具/委派按真实 sequence 返回，并用 `TurnEventWindow` 显式标记 64 条活动窗口边界 |
| `Services/ChatTranscriptWriter.cs` | 转录写入 |
| `Services/ChatTelemetryRecorder.cs` | 遥测记录 |

## 消息网关

| 文件 | 用途 | 关键符号 | 关联 | 约束 |
|------|------|------|------|------|
| `Services/MessageGateway/` | Connector 回信与飞书输出的投影目录 | `FeishuImageArtifactProjection` / `FeishuTtsProjection` / `ConversationTerminalMessageFormatter` | `Services/MessageGateway/ConversationReplyProjectionWorker.cs` | — |
| `Services/MessageFabric/MessageQueueProjectionService.cs` | 未认领消息队列的只读投影 | `MessageQueueProjectionService` / `MessageQueueKinds` | `Services/MessageFabric/MessageFabricStore.cs` | 默认只投影 `queued`/`retrying` 投递与 `pending` 命令；`includeTerminal=true` 才返回完整诊断；`queueKind` 区分事实源 |
| `Services/MessageFabric/MessageRouter.cs` / `Services/MessageFabric/MessageFabricStore.cs` | 投递路由与固化 | `MessageRouter` / `MessageFabricStore` | `MessageQueueProjectionService` | 按 intent/requires_response 固化为 `execute`/`notify`；per-target delivery ID 稳定；按 handling mode 原子批量领取，单批上限 20 条 |
| `Services/MessageFabric/MessageFabricSchemaBootstrapper.cs` | 存量库的 handling_mode 迁移 | `MessageFabricSchemaBootstrapper` | `Services/MessageFabric/MessageFabricStore.cs` | 在旧 SQLite 上幂等补列与索引；把普通 `inform`/`report_result`/`agent_reply` 的历史投递回填为被动通知 |
| `Services/Conversation/ConversationNotificationStore.cs` | 被动通知的原子受理 | `ConversationNotificationStore` | `Services/Conversation/` | 每条通知独立写 `ChatMessage` + `message.created` + `ConversationHead`，不创建 Turn/command；提交后唤醒 SSE |
| `Services/MessageGateway/ConversationReplyProjectionWorker.cs` | Connector 回信投影 | `ConversationReplyProjectionWorker` | `ConversationNotificationStore` | 只从 committed terminal event 投影；trusted Agent ingress 仅在显式 reply contract 下、以稳定 MessageId 投影一次 `agent_reply`；失败重试不重跑 Agent |
| `Services/Conversation/` | 对话受理与投影的处理器目录 | `SubmitTurnHandler` / `SystemCommandHandler` / `RequestCompactionHandler` / `CreateSteeringHandler` | `Services/ConversationEventStore.cs` | — |
| `Services/Conversation/CreateSteeringHandler.cs` | Steering 的单一受理边界 | `CreateSteeringHandler` | `Services/Conversation/` | 只接受 canonical Running Turn，校验 Workspace/Agent 后写 Runtime 消费队列 |
| `Controllers/Api/ConversationTurnsController.cs` | canonical Turn 的 HTTP API | `ConversationTurnsController` / `SteeringHttpRequest` / `SubmitTurnHttpRequest` | `Services/Conversation/CreateSteeringHandler.cs` | Steering 受理返回 202，冲突 409（fail closed） |
| `Services/ConversationEventStore.cs` | 对话事件存储 | `ConversationEventStore` / `GetBoundsAsync` | `IConversationEventStore` | `GetBoundsAsync` 用一条语句内 `ORDER BY sequence ASC/DESC LIMIT 1` 取索引两端点（复用 `(conversation_id, sequence)`，不新增索引），空会话返回 `EventBounds(null, null)`；不得改回 `MIN/MAX` |
| `Services/RsiTrajectoryDataAccess.cs` | RSI 轨迹的 EF 数据访问 | `RsiTrajectoryDataAccess` | `Services/ConversationEventStore.cs` | `(turn_id,type)` 索引给不了 `(TurnId,Sequence)` 完全有序，第二排序键走 `USE TEMP B-TREE FOR LAST TERM OF ORDER BY`，代价受返回行数上界约束，limit 放大到千行级必须重评 |
| `Services/ConversationProjectionWorker.cs` | 对话投影 Worker | `ConversationProjectionWorker` | `Services/ConversationEventStore.cs` | 活跃流小积压做短 coalescing，checkpoint/catalog 批量提交，避免每个 raw source event 触发 SQLite/日志紧循环 |
| `Services/Execution/SqliteExecutionJournal.cs` | canonical 执行日志 | `SqliteExecutionJournal` | — | 开事务前处理 SQLite pooled-handle 激活异常；只在尚未写入事件时清池并有限重试，避免瞬时连接故障终止 Agent turn |
| `Services/MessageTopicService.cs` | 消息主题 | `MessageTopicService` | — | — |

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
| `Controllers/Api/AuthApiController.cs` | 登录、JWT/Session 当前用户投影；签发统一委托 `JwtTokenFactory`（密钥/有效期只来自配置，登录响应不返回到期时间）；认证成功/失败按 Information 记录且不记录用户标识、密码长度或账户存在性；`/api/currentUser` 异步读取 `AppUsers.Avatar`（空值回退自有 `/admin/assets/images/me.png`），刷新/重登后头像保持数据库最新值 |
| `Services/JwtTokenFactory.cs` | 登录态 JWT 的唯一配置解析（`PuddingJwtSettings`：`Jwt:Key/Issuer/Audience/ExpiryHours`，缺省 168 小时 = 7 天，密钥缺失/过短即 fail closed，无硬编码兜底）与唯一签发入口（HS256 + `sm2_sig` 载荷签名）；登录与 Bootstrap 首次初始化共用 |
| `Controllers/Api/UserAvatarApiController.cs` | 头像唯一上传契约 `POST /api/users/{userId}/avatar`（multipart 字段 `file`，返回 `{ avatar }`）；上传自己需登录、为他人上传需 Admin（403）；统一 PNG/JPG/WebP、5 MiB 上限；`SaveForUserAsync` 复用落盘/写库/旧文件清理；`GET` 匿名查任意用户头像 |
| `Controllers/Api/AppUserApiController.cs` | 用户管理 CRUD/密码/角色，收紧为 `[Authorize(Roles = "admin")]`；`AppUserDto` 携带 `Avatar` |
| `Services/UserAvatarStorageService.cs` | 头像落盘 `wwwroot/user-avatars/`（userId 前缀防穿越、原子写、TryDelete 限根内）；允许 MIME 仅 PNG/JPEG/WebP（GIF 已移除） |
| `Services/Sm2JwtSigner.cs` | ECDSA-P256 JWT payload 签名；缺少持久密钥时仅记录进程临时密钥告警与公钥 SHA-256 指纹，禁止日志输出私钥/公钥材料 |

## 子代理 & 诊断

| 文件 | 用途 |
|------|------|
| `Middleware/TraceableExceptionMiddleware.cs` | 未处理异常生成可检索 errorId/500；仅当 `RequestAborted` 已取消时把 `OperationCanceledException` 视为客户端断开（499 + Debug），不污染 Error 日志 |
| `Services/SubAgentManager.cs` | 子代理管理；统一预算解析（N00）：`maxRounds = request.MaxRounds ?? options.MaxRounds`、`maxToolCallsTotal` 同理（无 isManagedWorkUnit 特判）；显式超过 options 上限抛 InvalidOperationException；收尾宽限与续跑语义不变；以同一 SubSessionId + 新 runId 透明续跑并重置计数器；终态 usage 只存运行摘要；`SubAgentResultIdentity.Compute(childRunId, 规范化终态, 父会话)` 派生确定性 `resultId` 并复用为结果消息 `MessageId`（无时间戳/随机成分），借 MessageFabric 按 MessageId 去重保证“一个 result 只触发一次父级接续” |
| `Services/SubAgentPool.cs` | Core `ISubAgentPool` 的 Platform 子代理池实现 |
| `Services/SubAgentTransientDirectoryGcService.cs` | 历史临时执行身份空 Skill 脚手架 GC；以精确目录形状 + 子代理池 + durable run 终态多重门禁，先移入 retention-archive 隔离，延迟后再安全删除 |
| `Services/SubAgentDiagnosticsService.cs` | 子代理诊断 |
| `Services/FileSubAgentRunStore.cs` | 子代理运行文件存储；终态 `run.json` 固化轮次、工具、耗时和错误；支持可恢复终态 `budget_exhausted` 与预算通知投影；归档读写同一 per-run gate + sharing violation 退避重试，重试耗尽写 archive-degraded.json 降级（ADR-060 §3.11） |
| `Controllers/Api/SubAgentRunController.cs` | 认证运行检查器 API；详情从归档返回终态统计，events 分页返回可重建历史时间线的完整事件 payload |
| `Services/SessionStateManager.cs` | 会话/子代理持久状态查询；子代理状态 DTO 按可复用 SubSessionId 关联最新 canonical runId，供托盘坞和检查器在漏收事件后恢复运行 |

## 任务系统（Tasks）

| 文件 | 用途 | 关键符号 | 关联 | 约束 |
|------|------|------|------|------|
| `Services/Tasks/SqliteWorkspaceTaskStore.cs` | Task Ledger 的 SQLite 存储 | `SqliteWorkspaceTaskStore` | `Data/Entities/WorkspaceTaskEntity.cs` | CAS 乐观并发；Task 与 Event 原子提交；硬删语义（无软删）；keyset 分页；结构化路由列与 auto-dispatch opt-in 同表持久化 |
| `Services/Tasks/WorkspaceTaskSchemaBootstrapper.cs` | Task 相关表的幂等建表与补列 | `WorkspaceTaskSchemaBootstrapper` | `Data/Entities/TaskEventEntity.cs` | 对既有 SQLite 幂等补齐结构化路由列与 `auto_dispatch_enabled` |
| `Services/Tasks/TaskAgentCommandService.cs` | task_* 工具的命令服务 | `TaskAgentCommandService` | `Services/Tasks/TaskCommandService.cs` | claim/update 原子写回 Task、Attempt、Event、Binding 四表 |
| `Services/Tasks/TaskCommandService.cs` | Task 写命令的原子语义 | `TaskCommandService` | `Services/Tasks/TaskAgentCommandService.cs` | 无 Assignment 的人工完成写 `TaskCompleted/manual_without_execution`；active Assignment 禁止 PATCH 伪完成；`mark_failed` 原子释放 attempt |
| `Services/Tasks/TaskDispatcher.cs` | 任务派发与 outbox 投递 | `TaskDispatcher` / `TaskDispatcherOptions` | `Services/Tasks/TaskDispatchOutboxStore.cs` | 发送前重验 Task/Assignment owner；stale 与确定性终态冲突走 dead-letter，其他失败受 MaxAttempts 限制；ActiveTask 以 `RuntimeDispatchRequest.ActiveTask` 注入派发链 |
| `Services/Tasks/TaskDispatchOutboxStore.cs` | 派发 outbox 持久化 | `TaskDispatchOutboxStore` / `TaskDispatchOutboxStatuses` | `Services/Tasks/TaskDispatcher.cs` | — |
| `Services/Tasks/TaskDispatchSchemaBootstrapper.cs` | 派发 outbox 的幂等建表 | `TaskDispatchSchemaBootstrapper` | `Services/Tasks/TaskDispatchOutboxStore.cs` | — |
| `Services/Tasks/TaskDispatchSerialization.cs` | 派发载荷序列化 | `TaskDispatchSerialization` | `Services/Tasks/TaskDispatchOutboxStore.cs` | — |
| `Services/Tasks/TaskDependencyStore.cs` | finish-to-start 依赖图存储 | `TaskDependencyStore` | `Data/Entities/TaskDependencyEntity.cs` | 同 Workspace 校验、幂等增删、环检测与 Satisfied/Waiting/Broken 评估；`ListAsync` 排序在客户端完成（SQLite/EF 不支持 `DateTimeOffset` 的 ORDER BY 翻译） |
| `Services/Tasks/WorkspaceTaskAdminService.cs` | manage_tasks 的管理者服务面 | `WorkspaceTaskAdminService` | `Services/Tasks/TaskDependencyStore.cs` | 详情含前置链 BFS 展开与后继 `EvaluateAsync`（单一事实源）；写侧依赖复用 `AddAsync`（幂等 + 环检测，fail-closed 转结构化错误码）；`include_children` 复用单次 `ListChildrenAsync` |
| `Services/Files/SqliteProviderFileRefStore.cs` | provider 文件引用的 SQLite 存储 | `SqliteProviderFileRefStore` | `Services/Files/ProviderFileRefSchemaBootstrapper.cs` | `ON CONFLICT DO UPDATE` 幂等 upsert；BEGIN IMMEDIATE + status CAS 防重复分配；近过期（<300s）不分配；`RemoteFileId` 只存不打印 |
| `Services/Files/ProviderFileRefSchemaBootstrapper.cs` | `llm_provider_file_refs` 的幂等建表 | `ProviderFileRefSchemaBootstrapper` | `Services/Files/SqliteProviderFileRefStore.cs` | 唯一主键 + status/expires_at 索引 |
| `Services/Tasks/TaskWireMaps.cs` | 枚举与 wire 的双向映射 | `TaskWireMaps` | `Controllers/Api/TaskDtos.cs` | `ErrorCode` 到 wire/HTTP 状态码的映射也在此处，新增错误码必须同步 |
| `Services/Tasks/ManualAlwaysAllowFence.cs` | 人工命令的 always-allow 围栏 | `ManualAlwaysAllowFence` | `Services/Tasks/TaskCommandService.cs` | — |
| `Controllers/Api/TaskController.cs` | Task Control Plane 的 HTTP 面 | `TaskController` / `GET /tasks/watch`(SSE) | `Services/Tasks/TaskCommandService.cs` | 13 个端点；watch 走快照 + 游标 + Last-Event-ID；`boardColumn` 五列过滤；`DELETE` 智能删除返回 200 deleted/archived |
| `Controllers/Api/TaskSchedulingController.cs` | 认证的调度诊断端点 | `TaskSchedulingController` | `Services/Scheduling/TaskSchedulerControlService.cs` | Agent Availability query/rebuild、Auto evaluate-only、Task 依赖增删与评估 |
| `Controllers/Api/TaskDtos.cs` | Task 的 wire DTO 集合 | `TaskDto` / `CreateTaskDto` / `PatchTaskDto` | `Controllers/Api/TaskController.cs` | — |
| `Data/Entities/WorkspaceTaskEntity.cs` | `workspace_tasks` 实体 | `WorkspaceTaskEntity` | `Services/Tasks/SqliteWorkspaceTaskStore.cs` | 共 28 列，结构化路由列与 `auto_dispatch_enabled` 同表 |
| `Data/Entities/TaskEventEntity.cs` | `task_events` 实体 | `TaskEventEntity` | `Services/Tasks/SqliteWorkspaceTaskStore.cs` | long 自增 Id + 18 业务列 |
| `Data/Entities/TaskAssignmentAttemptEntity.cs` | `task_assignment_attempts` 实体 | `TaskAssignmentAttemptEntity` / `AssignmentAttemptStatus` | `Services/Tasks/TaskCommandService.cs` | partial unique index（`task_id` WHERE `released_at_utc IS NULL`）保证单 Task 单 active attempt |

## Agent Availability 与自动派发（Services/Scheduling/，2026-08-26）

| 文件 | 用途 | 关键符号 | 关联 | 约束 |
|------|------|------|------|------|
| `Services/Scheduling/AgentAvailabilityProjectionStore.cs` | 从持久事实保守重建 Agent 可用性 | `AgentAvailabilityProjectionStore` | `IWorkspaceAgentCatalog` | 仅 canonical active assignment 的非终态 Task 占用 Agent，终态历史脏 attempt 不再造成 false-busy；Unknown/过期不参与自动派发 |
| `Services/Scheduling/AgentExecutionReservationStore.cs` | Agent 自动工作槽与租约存储 | `AgentExecutionReservationStore` | `Data/Entities/AgentExecutionReservationEntity.cs` | 单 Agent/Task 一个 active 工作槽；lease 与 fencing token 单调，支持 renew/release/expiry |
| `Services/Scheduling/ConservativeExecutionWindowResolver.cs` | 保守执行窗口判定 | `ConservativeExecutionWindowResolver` | `Services/Scheduling/ProviderModelExecutionWindowResolver.cs` | `anytime` 直接放行；`inherit`/`off_peak_only` 在路由价格档案缺失时返回 Unknown（fail closed） |
| `Services/Scheduling/ProviderModelExecutionWindowResolver.cs` | 按 provider/model 价格档案解析执行窗口 | `ProviderModelExecutionWindowResolver` | `IWorkspaceAgentCatalog` | 按 Agent 实际 provider/model 与 `llm.providers.json` 版本化价格窗口处理时区/跨午夜/边界；未知即 fail closed |
| `Services/Scheduling/TaskAutoDispatchEvaluator.cs` | 无副作用的自动派发候选评估 | `TaskAutoDispatchEvaluator` / `TaskAutoDispatchOptions` / `TaskTypeRouteOptions` | `IWorkspaceAgentCatalog` | 每轮每 Agent 只重建一次 Availability，全部候选共享同一 version fence；另受依赖、5 分钟 idle grace、窗口与同轮单 Agent 单任务约束 |
| `Services/Scheduling/TaskAgentRouteMatcher.cs` | 不读任务标题的确定性 Agent 路由 | `TaskAgentRouteMatcher` / `TaskAgentRouteMatch` | — | 任务类型规则与任务显式约束取交集，输出 provider/model/capability 解释与 SHA-256 快照；`CreatedAt`/`UpdatedAt` 不进入原子路由指纹 |
| `Services/Scheduling/ModelRoutePolicyContracts.cs` | 阶段感知模型路由的声明式契约 | `RoutePolicy` / `ModelCapabilityProfile` / `RoutePolicyCatalog` | `Services/Scheduling/ModelRoutePolicyEvaluator.cs` | 按 (taskType, phase) 给默认策略，Explore/triage 低成本、Verify 固定 isolated-readonly；模型身份只由结构化字段决定，不由自由文本决定 |
| `Services/Scheduling/ModelRoutePolicyEvaluator.cs` | 确定性的模型路由求值 | `ModelRoutePolicyEvaluator` | `Services/Scheduling/ModelRoutePolicyContracts.cs` | 硬门顺序 capability → context → tool protocol → quality floor → security，失败返回机器可读码；Fingerprint 为 SHA-256 且与候选顺序无关；无候选通过时不静默回退 |
| `Services/Scheduling/TaskExecutionPlanCompiler.cs` | 不读任务正文的 WorkUnit 编译 | `TaskExecutionPlanCompiler` | `Data/Entities/TaskDependencyEntity.cs` | 按 taskType 生成有界 DAG，把依赖/能力/冲突范围/预算冻结为 SHA-256；未知类型 fail closed |
| `Services/Scheduling/TaskBacklogRefinementEvaluator.cs` | Backlog 精化候选的只读评估 | `TaskBacklogRefinementEvaluator` | `IWorkspaceAgentCatalog` | 每五分钟只读检查已 opt-in Backlog 的描述/验收标准/任务类型/兼容 Agent，输出 ReadyCandidate 或 NeedsRefinement，不改任务状态 |
| `Services/Scheduling/TaskBacklogRefinementStore.cs` | Backlog→Ready 的唯一 CAS 写入者 | `TaskBacklogRefinementStore` | `Services/Scheduling/TaskBacklogRefinementEvaluator.cs` | 写入前重验任务、Agent、TaskTypeRoute 与路由 SHA-256；原子写 canonical `TaskReady/backlog_refined` |
| `Services/Scheduling/TaskExecutionTracker.cs` | Task 执行链的只读一致性巡检 | `TaskExecutionTracker` | `Services/Scheduling/TaskExecutionRepairCoordinator.cs` | Blocked Goal 仍持 active binding 判为 `blocked_binding_still_active`（不可直接清 binding）；终态 Delivery 无 execution 属即时 cleanup |
| `Services/Scheduling/TaskExecutionRepairCoordinator.cs` | 终态遗留与超时的确定性修复 | `TaskExecutionRepairCoordinator` | `Services/Scheduling/TaskExecutionTracker.cs` | Serializable 重读 fence 后才清理，范围限终态或 Blocked Goal 的遗留记录与超时未被 claim 的 legacy assignment；禁止猜 Task 成功、续过期 reservation、合成 Turn |
| `Services/Scheduling/TaskAutoDispatchWorker.cs` / `Services/Scheduling/TaskAutoDispatchScanRunner.cs` | 低频恢复扫描 | `TaskAutoDispatchWorker` | `TaskAutoDispatchScanRunner` | 周期与 Admin 立即扫描复用同一 runner，顺序固定 tracking/repair → Availability 重建 → refinement → dispatch；按 workspace gate 串行 |
| `Services/Scheduling/TaskSchedulerControlService.cs` / `Controllers/Api/TaskSchedulingController.cs` | Admin 调度控制面 | `TaskSchedulerControlService` | — | revision CAS 策略热加载、workspace pause/resume、立即 scan/repair 与权威 status；原子写回 `<DataRoot>/config/system.json` 的 `taskAutoDispatch`；控制端点限 admin |
| `Services/Scheduling/TaskBoundGoalOptions.cs` | Task-bound Goal 的安全开关与预算 | `TaskBoundGoalOptions` | — | 默认关闭；开启后才允许 Task 绑定 Goal 并使用 Iteration 预算与 Reservation lease |
| `Services/Scheduling/TaskSchedulingSchemaBootstrapper.cs` | 调度三表与索引的幂等建表 | `TaskSchedulingSchemaBootstrapper` | `Data/Entities/AgentAvailabilityProjectionEntity.cs` | Availability、Reservation、Task dependency 三表与唯一索引幂等建表 |
| `Services/Scheduling/TaskSchedulerIntentStore.cs` | durable intent 队列存储 | `TaskSchedulerIntentStore` / `ITaskSchedulerIntentStore` | `TaskSchedulerIntentSchemaBootstrapper` | `INSERT OR IGNORE` 幂等入队；事务内单 UPDATE 抢占式 Dequeue（回收过期 lease、attempt 自增）；Complete/Fail 超限转 dead；时间列固定宽度 UTC TEXT，保证字典序=时间序 |
| `Services/Scheduling/TaskSchedulerIntentSchemaBootstrapper.cs` | intent 队列的幂等建表 | `TaskSchedulerIntentSchemaBootstrapper` | `Services/Scheduling/TaskSchedulerIntentStore.cs` | 建 `task_scheduler_intents` 与 `UNIQUE(source, source_event_id)` 等 4 个索引 |
| `Services/Scheduling/TaskAutoDispatchStarter.cs` | 事件驱动的派发启动 | `TaskAutoDispatchStarter` / `ITaskAutoDispatchStarter` | `Services/Scheduling/TaskSchedulingCoordinator.cs` | 围栏字段校验 + 二次 window fence + 原子 StartAsync，LostRace 容忍；MaxStarts/MinimumIdle 从动态策略读取 |
| `Services/Scheduling/TaskEventLedgerTailBridge.cs` | 账本尾游标到 intent 的桥 | `TaskEventLedgerTailBridge` | `ITaskSchedulerIntentStore` | 按 `IntentPollInterval` 轮询 `task_events`/`conversation_events` 新行，游标取账本 MAX 懒初始化且不回放历史；按事件清单过滤入队；shadow 只推进游标不入队 |
| `Services/Scheduling/TaskSchedulingCoordinator.cs` | 事件驱动的调度协调 | `TaskSchedulingCoordinator` | `ITaskAutoDispatchStarter` | Dequeue → 按 workspace 合并 → goal 终态先重建 Availability → Evaluate → Starter 派发 → Complete/Fail（超限 dead）；动态 pause 后不消费该 workspace intent |
| `Data/Entities/AgentAvailabilityProjectionEntity.cs` | 持久 Availability 投影实体 | `AgentAvailabilityProjectionEntity` | `Services/Scheduling/AgentAvailabilityProjectionStore.cs` | — |
| `Data/Entities/AgentExecutionReservationEntity.cs` | 自动工作租约与单调 fencing 实体 | `AgentExecutionReservationEntity` | `Services/Scheduling/AgentExecutionReservationStore.cs` | — |
| `Data/Entities/TaskDependencyEntity.cs` | Task finish-to-start 依赖边实体 | `TaskDependencyEntity` | `Services/Scheduling/TaskExecutionPlanCompiler.cs` | — |

## Goal 持久控制面（Services/Goals/ · ADR-074 G1–G3 + Task-bound 原子启动源码链）

| 文件 | 用途 |
|------|------|
| `Services/Goals/GoalSchemaBootstrapper.cs` | goal_runs/goal_iterations/goal_outbox/goal_verifications/task_goal_bindings 五表幂等建表；含"单会话一个非终态 Goal" partial unique 与 outbox 幂等键索引（G1 冻结全部 schema） |
| `Services/Goals/GoalRunStore.cs` | 聚合写入原语：Create/TryMutate（CAS + Func 卫兵）与 goal.* ConversationEvent 同事务直写；提供按 sequence 选择当前非终态 WorkUnit 的只读查询 |
| `Services/Goals/GoalOutboxStore.cs` + `GoalOutboxSignal.cs` | continuation due/claim/lease/fencing/recovery/defer/suppress/dead-letter；signal 只降延迟 |
| `Services/Goals/GoalContinuationWorker.cs` | durable intent → 受信 synthetic Acceptance；用户 Turn 优先；从 Binding 解析当前 WorkUnit，将 plan/node/fingerprint/预算放入受信 Acceptance 与 prompt；payload JSON 保留可读 Unicode，同时继续转义 HTML 敏感字符以保护 envelope 边界 |
| `Services/Goals/GoalSettlementStore.cs` + `GoalSettlementWorker.cs` | canonical Turn 全窗口终态判定 → 最新 128 条有界 Evidence Capsule → version/epoch/Task/Reservation gates → 下一 outbox 或终态；候选携带验收合同 source/version 与 turn.completed 独立键 goal_contract_proposal（片6-3b：TryGetProperty 容错读取，历史行无键不抛异常），Worker 携 proposal 时先走 GoalContractRefinementStore 合同整理通道（validator→幂等探针→CAS→同事务审计），Applied 后完整重载 Criteria/Checks/Source/ContractVersion 四项；主 Turn canonical usage 加当前 Turn 时间窗内递归子会话 TokenUsageEvents，连同 Run/耗时/工具聚合到 Iteration/Goal。**熔断接线（P0-2）**：结算事务为唯一 writer，按指纹/同阻塞码/infra 三轴独立计数（`ApplyProgressAccounting`，**Wait 族整轮排除**），达 `NoProgressBreakerThreshold`（默认 3）后不再返回 repair —— 先一次性 Replan（PlanVersion++ 并退回卡死 WorkUnit），Replan 不可行或已消耗则转 needs_user，并落 `goal.circuit_opened` 与 `goal.progress.recorded` 事件（非静默）。**终态化降级（P0-3）**：不可恢复 verdict 需同一原因连续达到阈值才 Failed/Blocked；`Unsafe` 与取消类（cancelled/iteration_cancelled）豁免、立即终止 |
| `Services/Goals/ConservativeGoalIterationVerifier.cs` | fail-closed 只读 Verifier；自然语言完成无权写终态，Task canonical Completed 才允许 bound Goal 完成 |
| `Services/Goals/TaskGoalDispatchTransactionStore.cs` | Task/ExecutionPlan/WorkUnits/Assignment/Reservation/Binding/Goal/首个 Outbox/事件/Availability 单 Serializable 事务与幂等 replay；事务前重读 Agent/类型规则并重算 route/plan 双 SHA-256，任一漂移 fail closed；派发时原子退役同 Workspace/Agent/会话且 terminal Task binding 的遗留 Blocked Goal（可来自前一 Task），释放 active-Goal 唯一索引，并将 SQLite 约束详情写入诊断日志 |
| `Services/TaskPlanning/TaskPlanningSchemaBootstrapper.cs` + `Data/Entities/TaskPlanRunEntity.cs` / `TaskNodeEntity.cs` / `WorkUnitAwaitHandleEntity.cs` | 复用规划表冻结 WorkspaceTask version 对应的执行快照、WorkUnit budgets/scopes/dependencies/checkpoint 与 durable AwaitHandle；启动初始化器显式幂等升级旧 SQLite |
| `Services/ConversationAcceptanceStore.cs` | Chat/Goal synthetic Turn 原子受理；重验 Goal/outbox/Task/Assignment/Reservation/Plan/当前 WorkUnit 全围栏并原子置 Running；lease 校验/续租统一使用注入 `TimeProvider` |
| `Services/ExecutionCommandReader.cs` | 执行前沿 Command→GoalIteration→Binding→Task/Reservation→Plan/Node 重读 canonical WorkUnit 身份与预算；metadata 只选择、不授权，漂移 fail closed |
| `Services/Goals/GoalCommandService.cs` | /goal 全命令合同：set/edit/replace/pause/resume/cancel/clear/status/**policy**（值取 paused 或 auto_resume_on_restart，仅非终态可设、非法值 fail-closed、同值幂等不写事件、CAS + 同事务写 resume_policy 列与 `goal.policy_changed` 事件，status 输出含 Resume policy 行；属**用户权能**，不暴露为 agent 工具）；conflict、幂等重放（source_command_id 唯一）、expectedVersion、budget_exhausted 不可 resume、feature flag 下保留 status/pause/cancel |
| `Services/Goals/GoalQueryService.cs` | 只读投影（active/latest/iterations/steps/todo；todo 由归属 Agent 服务端解析 + `ITodoStore.ReadAsync`，未写拆解 = found:false） |
| `Services/Goals/GoalRestartReconciler.cs` | 启动按 `goal_runs.resume_policy` 分流：`paused`（默认）active→paused（bootId 锚点 + goal.paused 事件）；`auto_resume_on_restart` 保持 Active 但换发 activation fence（epoch++/bootId）并落 goal.resumed，旧 writer 失效。同 bootId 重放幂等、非 Active 不动、单 boot 恢复配额（`MaxAutoResumesPerBoot`）超出则降级 paused；返回 `GoalRestartReconcileResult(DisarmedCount, AutoResumedCount)` |
| `Controllers/Api/GoalCommandsController.cs` | POST /api/v1/conversations/{id}/goals/commands（结构化命令） |
| `Controllers/Api/GoalQueriesController.cs` | GET /goal、/api/v1/goals/{id}、/goals/{id}/iterations、/goals/{id}/steps、/goals/{id}/todo（只读；未写拆解 200+found:false，仅 goal 缺失 404） |
| `Data/Entities/Goal*Entity.cs` + `TaskGoalBindingEntity.cs` | 五张表实体（枚举 int、snake_case、version CAS） |

关联修改：`SystemCommandHandler`（/goal 分支委托 GoalCommandService，不创建 Turn）；`PlatformDbContext`（5 个 DbSet + partial unique 索引）；`PuddingApplicationInitializer`（GoalSchemaBootstrapper + 启动 reconcile：按 resume_policy 分流 disarm / auto-resume，日志区分 disarmed 与 auto-resumed）。相关配置见 `PuddingCore/Goals/GoalRunOptions.cs`：`NoProgressBreakerThreshold`（默认 3，边界 1..16）、`DefaultResumePolicy`、`MaxAutoResumesPerBoot`（默认 8，边界 0..64）。

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

## 安全审批管理 API 与分类器健康（2026-09-21）

| 文件 | 说明 |
| --- | --- |
| `Controllers/Api/ToolApprovalAdminApiController.cs` | 安全审批白名单/审计管理 API（`[Authorize]`，`/api/tool-approval`）：allowlist 的 list/create/get/update + audit 查询；来源 `built_in` / `audit_agent` / `human` / **`classifier`** 双向映射。**2026-09-21 修两处缺陷**：① `FormatSource` 曾以 `_ => "human"` 兜底 ⇒ **分类器落的规则被当作 human 上报**（审计溯源失真），现改为如实回显枚举名，未来新增来源**宁可暴露未知也不谎报来源**；② `TryParseSource` 补 `classifier` 分支，否则前端编辑/禁用分类器规则会被 **400** 拒绝 |
| `Controllers/Api/ClassifierHealthApiController.cs` | 分类器健康**只读** API（切片 S6b-1，规格 §8.2 / §10 D6）：`GET /api/classifier-health`，`[Authorize]`，**服务端权威**——直接透传 `IClassifierHealthReporter.Snapshot()`，不由前端推断、不缓存改写；健康面未接线 ⇒ **`200` + `configured=false` 的明确未知态**（绝不 500、绝不假装健康）；wire 仅白名单字段（`classifierId` / `health` 小写字符串 / `detail` / `consecutiveFailures` / `lastCheckedAtUtc` / `lastLatencyMs`）；**只依赖 PuddingCore 抽象**（不引用 Runtime 具体类）；per-key 连续 deferred 计数与退避档属 **Agent 侧诊断**（`classifier_status` 工具），**不进本 API**（§8.2 裁定）；离线契约测试 `PuddingPlatformTests/Controllers/ClassifierHealthApiControllerTests.cs`（7 例） |

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
| `Services/VisionArtifactStorageService.cs` + `.Preprocessing.cs` + `ImagePreprocessing.cs` | Workspace原图与派生缓存；64MiB、实际格式/尺寸/EXIF；模型边界低分辨率副本与超尺寸概览；本地裁剪/旋转/灰度/降噪/缩放/编码。原图不覆盖，变换按源与版本化参数复用，解码串行限并发 |
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

余额链路测试：`PuddingPlatformTests/Services/DeepSeekLlmBalanceProviderTests.cs`（8 用例：解析//v1 剥离/Bearer/非 2xx/网络错误/CanHandle 矩阵）+ `LlmProviderBalanceDispatchTests.cs`（4 用例：暂不支持/委托与密钥/404/400）；扩展步骤见 `Docs/12_features/服务商余额查询与多服务商计费适配器设计方案.md`。

## Token 计量

| 文件 | 用途 |
|------|------|
| `Services/TokenUsageRecorder.cs` | Token 用量记录；持久化 RuntimeExecutionIdentity 提供的 parent/sub-agent、零基 round、本轮 canonical 工具及 context layer Token/UTF-8/GZIP/hash/cache 诊断；prefix-v2 在 system/tool 不变而 PrefixHash 变化时归因为 `history_anchor_changed`，版本切换归因为 `serialization_version_changed`；不复制 prompt 正文 |
| `Services/CacheDiagnosticsService.cs` | 会话级 Cache Miss Inspector 后端；汇总 token-weighted hit/miss、prefix churn、首次变化原因与逐轮事实 |
| `Services/GoodputAttributionService.cs` | Goodput 归因聚合（只读）：SourceId 中段(TraceId)→goal_iterations/execution_runs 归因，汇总每迭代 prompt/completion/cache-hit/miss/成本；并判定「零成本却非零 token 不得当节省」（SavingsClaimable）。含 `UsageAttribution.Parse` 与 `PricingClassifier`（priced/free/unpriced/unknown_provider） |
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
