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

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `Services/ChatHistoryService.cs` | 聊天历史读写 | `ChatHistoryService` | `Services/ChatMessageRepository.cs` | — |
| `Controllers/Api/MessageSearchController.cs` | 消息弹窗检索（未走全文/向量索引） | `MessageSearchController`、`RawSessionLogService` | `Services/RawSessionLogService.cs`、`Services/ChatMessageRepository.cs` | POST /api/messages/search；GrepMessagesAsync 从事件表发现会话、取最多 10,000 条后内存筛日期/子串 |
| `Services/RawSessionLogService.Fts.cs` | Agent Markdown 日志的 Lucene 搜索 | `RawSessionLogService` | `Services/RawSessionLogService.cs` | 要求 agent 与有界日期范围；查询中按日刷新文件索引；区别于消息弹窗检索 |
| `Services/ChatMessageRepository.cs` | 消息仓储与 after-Id 增量扫描 | `ChatMessageRepository` | `Services/ChatMessageSchemaBootstrapper.cs` | ChatMessageRow 透传 WorkspaceId/MessageId/TurnId 与 ContentPartsJson canonical 信封；不因空正文越过纯 typed-parts 消息 |
| `Services/ChatMessageSchemaBootstrapper.cs` | 存量 SQLite 幂等补列 | `ChatMessageSchemaBootstrapper` | `Services/ChatMessageRepository.cs` | 补 ChatMessages.content_parts_json 列 |
| `Services/AgentChat/ExecutionRunCoordinator.cs` | 执行前预算与身份冻结 | `ExecutionRunCoordinator`、`CallerLlmSnapshot`、`snapshot.VisionPolicy` | `Services/Snapshot/AgentExecutionSnapshotFactory.cs` | 应用 canonical WorkUnit context；取最严 rounds/tools/duration 冻结 deadline；按实际 provider/model 冻结价格与预算 |
| `Services/Snapshot/AgentExecutionSnapshotFactory.cs` | 执行快照组装与视觉策略解析 | `AgentExecutionSnapshotFactory` | `Services/AgentChat/ExecutionRunCoordinator.cs` | 视觉策略按配置合同∩模型类别解析（不再硬编码 VisionRequestPolicy.Default）；无 vision 标签一律 null 不误标；日志键 visionPolicy=／visionPolicySource= |
| `Services/AgentChat/TurnOutputChunker.cs` | Delta 聚合分块与轮次边界 | `TurnOutputChunker` | `PuddingPlatformTests/Services/TurnOutputChunkerPayloadOwnershipTests.cs` | 非 delta 事件先 flush 已缓冲正文/思考再透传；轮次边界进入 canonical sequence |
| `Services/AgentChat/AgentConversationProjectionService.cs` | Chat 首屏/活动 run/消息明细投影 | `AgentConversationProjectionService` | `Services/AgentChat/TurnOutputChunker.cs` | 活动根 run 以最新根 turn.started 锚定，避免子代理 runId 抢占；身份/每消息/每 Turn/游标查询按收敛索引点查，不再会话级 IN 扫描；TurnEventWindow 标记 64 条窗口边界 |
| `Services/ChatTranscriptWriter.cs` | 转录写入（含投影共享事务重载） | `ChatTranscriptWriter` | `Services/ConversationProjector.cs` | `PersistMessageInTransactionAsync` 只 Add 不 SaveChanges，提交后 `ScheduleIndexing`；原独立 scope 路径保留给其他写入者 |
| `Services/ChatTelemetryRecorder.cs` | 遥测记录 | `ChatTelemetryRecorder` | `Services/ChatHistoryService.cs` | — |

## 会话首屏读模型（Plan《会话首屏与渐进加载架构优化方案》，阶段 C）

| 文件 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `Data/Entities/ConversationTurnSummaryEntity.cs` | `conversation_turn_summaries`：Turn 终态、计数与有界预览，主键 `(conversation_id, turn_id)` | `ConversationTurnSummaryEntity` | `Services/ConversationReadModelSchemaBootstrapper.cs` | 只保存首屏摘要，不保存事件明细/完整正文；`read_model_version` 用于缓存与 ETag 失效 |
| `Data/Entities/ConversationReadStateEntity.cs` | `conversation_read_state`：当前根 Turn、压缩状态、有界子代理摘要 JSON、初始化状态与 revision | `ConversationReadStateEntity` | `Services/ConversationReadModelSchemaBootstrapper.cs` | 事件游标唯一权威仍是 `conversation_projection_checkpoints`；本行与 checkpoint 同事务提交 |
| `Data/ConversationReadModelSchema.cs` | 读模型表名/索引名的单一定义点 | `ConversationReadModelSchema` | `PlatformDbContext.cs`、`Services/ConversationReadModelSchemaBootstrapper.cs` | 新库 EF 模型与存量库 bootstrapper 引用同一常量，禁止两套名字；`conversation_events` 两条查询索引只在显式维护窗口创建 |
| `Services/ConversationReadModelSchemaBootstrapper.cs` | 存量库就地补齐两张小表 + 索引；显式维护创建 `conversation_events` 消息/Turn 收敛索引 | `ConversationReadModelSchemaBootstrapper`、`ConversationEventIndexMaintenanceResult` | `Data/ConversationReadModelSchema.cs` | 启动期只建小表（有界成本）；300 万行事件索引必须显式维护（有耗时/磁盘成本），不在每次启动执行 |
| `Services/AgentChat/ConversationReadModelEventMapper.cs` | canonical 事件 → 纯组件输入映射 | `ConversationReadModelEventMapper` | `PuddingConversationReadModel/Contracts/ConversationEventContracts.cs` | 只读生产 payload 字段（delta/tool/终态/子代理 producer），缺失不伪造；子代理产物正文标 `ParentRunId`，delta 入组件前 64 KiB 上限 |
| `Services/AgentChat/ConversationReadModelProjectionStore.cs` | 读模型加载/写入/陈旧自愈（三表同事务） | `ConversationReadModelProjectionStore` | `Data/Entities/ConversationTurnSummaryEntity.cs` | 不执行 SaveChanges/Commit；legacy partial；checkpoint 领先时用摘要最大 last_sequence 补放尾部；损坏 JSON fail closed |
| `Source/PuddingPlatformTests/Services/ConversationReadModelEventMapperTests.cs` | C2 事件映射定向测试（类/字段/delta 上限/子代理归属） | `ConversationReadModelEventMapperTests` | `Services/AgentChat/ConversationReadModelEventMapper.cs` | 覆盖 20 类 kind、UTF-8 边界、未知类型；纯内存 |
| `Source/PuddingPlatformTests/Services/ConversationReadModelProjectionStoreTests.cs` | C2 读模型持久化定向测试 | `ConversationReadModelProjectionStoreTests` | `Services/AgentChat/ConversationReadModelProjectionStore.cs` | 真实 SQLite：往返/upsert/回滚/legacy partial/omitted/损坏 JSON |
| `Source/PuddingPlatformTests/Services/ConversationProjectorReadModelTests.cs` | C2b 投影事务/陈旧读模型自愈端到端测试 | `ConversationProjectorReadModelTests` | `Services/ConversationProjector.cs` | 真实 SQLite+fake event store：原子提交/回滚/重放幂等/子 run 隔离/legacy partial/陈旧 checkpoint 补放尾部 |
| `Source/PuddingPlatformTests/Services/ConversationReadModelSchemaTests.cs` | 读模型持久化基线：EF 模型、存量库补齐、幂等、两条事件索引与 EXPLAIN 收敛断言 | `ConversationReadModelSchemaTests` | `Services/ConversationReadModelSchemaBootstrapper.cs` | 真实 SQLite 临时库；断言查询计划按 message/turn 等值收敛，禁止全会话扫描 |
| `Services/AgentChat/ConversationOpenQueryService.cs` | 统一打开/历史/过程分页有界只读查询；C/H 一致读事务 + ETag；SQL 先解包信封正文/身份再截断；图片引用有界投影；正文 UTF-8 字节分段读取 | `ConversationOpenQueryService` | `Services/AgentChat/ConversationOpenContracts.cs` | GET 不重放/不补建；预览与展开使用同一正文表达式、显示角色与模型角色分离；过程候选 scan≤400、空展示页前进、分段不切码点 |
| `Services/AgentChat/ConversationOpenContracts.cs` | 打开/分页 DTO、正文分段页、失败码 | `ConversationOpenSnapshot`、`ConversationMessageContentPage` | `Services/AgentChat/ConversationOpenQueryService.cs` | 与旧 AgentConversationView 并存至 D；失败用稳定 code，不伪造空会话 |
| `Source/PuddingPlatformTests/Services/ConversationOpenQueryServiceTests.cs` | 统一打开/历史/过程/正文分段定向测试 | `ConversationOpenQueryServiceTests` | `Services/AgentChat/ConversationOpenQueryService.cs` | 真实 SQLite：C/H 一致、有界预览、信封身份/图片引用、普通 JSON 保真、键集分页、空展示页前进、游标拒绝、UTF-8 正文分段、结构化过程字段 |
| `Services/AgentChat/AgentMainSessionResolver.cs` | Agent 主会话有界身份解析（redirect → Manifest MainSessionId → canonical FindMain） | `AgentMainSessionResolver` | `Services/SessionRedirectStore.cs`、`ISessionRepository` | 只点查不拉工作区列表；workspace/owner 不匹配 fail closed；解析失败返回 null |
| `Controllers/Api/ConversationOpenApiController.cs` | 统一打开/历史/过程 HTTP 面 | `ConversationOpenApiController` | `Services/AgentChat/ConversationOpenQueryService.cs` | ETag/If-None-Match 304；查询失败映射 400 稳定 code；owner 优先 NameIdentifier（显示名非会话 owner）；旧 /conversation 保留至 D |
| `Source/PuddingPlatformTests/Services/AgentMainSessionResolverTests.cs` | C3b 身份解析测试（5 例） | `AgentMainSessionResolverTests` | `Services/AgentChat/AgentMainSessionResolver.cs` | redirect/Manifest/FindMain 优先级、workspace/owner 拒绝、admin 归一 |
| `Source/PuddingPlatformTests/Services/ConversationOpenApiControllerTests.cs` | C3b/D 控制器合同测试（6 例） | `ConversationOpenApiControllerTests` | `Controllers/Api/ConversationOpenApiController.cs` | 200+ETag、条件请求 304、未知 Agent 404、非法游标 400 稳定码、NameIdentifier/显示名 owner 口径 |
| `Source/PuddingPlatformTests/Services/ConversationOpenQueryPerformanceGateTests.cs` | C3b 百万事件查询门禁（显式 env） | `ConversationOpenQueryPerformanceGateTests` | `Services/AgentChat/ConversationOpenQueryService.cs` | `PUDDING_QUERY_GATE=1`；1.82M 事件 p95/EXPLAIN 无会话扫描；报告写 temp/test-out |

关联修改：`PlatformDbContext`（读模型 2 表 + 2 条事件索引）；Platform/slnx 接入 `PuddingConversationReadModel`；`ConversationProjector` 单事务投影；C3b 新增 `ConversationOpenQueryService`/`AgentMainSessionResolver`/`ConversationOpenApiController`（`ISessionRepository` 映射在 Host）；门禁见阶段 C 报告。

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
| `Services/ConversationProjectionWorker.cs` | 对话投影 Worker：head>checkpoint + 陈旧读模型双来源发现 | `ConversationProjectionWorker` | `Services/ConversationEventStore.cs` | 活跃流小积压做短 coalescing；额外发现 checkpoint 已到 head 但 read_state 明显落后的会话，交给 Projector 补放尾部 |
| `Services/Execution/SqliteExecutionJournal.cs` | canonical 执行日志 | `SqliteExecutionJournal` | — | 开事务前处理 SQLite pooled-handle 激活异常；只在尚未写入事件时清池并有限重试，避免瞬时连接故障终止 Agent turn |
| `Services/MessageTopicService.cs` | 消息主题 | `MessageTopicService` | — | — |

## Agent 管理

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `Services/WorkspaceAgentFileService.cs` | Agent 文件服务（65KB） | `WorkspaceAgentFileService` | `Services/AgentTemplateFileService.cs` | 单文件承载 Agent 增删改查与校验 |
| `Services/AgentTemplateFileService.cs` | 模板文件服务（27KB） | `AgentTemplateFileService` | `Services/AgentTemplateProvider.cs` | 单文件承载模板读写 |
| `Services/AgentTemplateProvider.cs` | 模板提供 | `AgentTemplateProvider` | `Services/AgentTemplateFileService.cs` | — |
| `Services/AgentLLMConfigResolver.cs` | LLM 配置解析 | `AgentLLMConfigResolver` | `Services/AgentRuntimeProfileResolver.cs` | 把选中模型的协议写入 LlmConfig |
| `Services/AgentRuntimeProfileResolver.cs` | Runtime Profile 解析（16KB） | `AgentRuntimeProfileResolver` | `Services/AgentLLMConfigResolver.cs` | 单文件承载 Profile 解析 |
| `Services/AgentConversationLogService.cs` | 对话日志 | `AgentConversationLogService` | `Services/WorkspaceAgentFileService.cs` | — |
## 认证与当前用户

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `Controllers/Api/AuthApiController.cs` | 登录与当前用户投影 | `AuthApiController` | `Services/JwtTokenFactory.cs` | 签发统一委托 JwtTokenFactory（密钥与有效期只来自配置，登录响应不返回到期时间）；认证成功/失败按 Information 记录且不记用户标识、密码长度或账户存在性；/api/currentUser 异步读 AppUsers.Avatar（空值回退 /admin/assets/images/me.png） |
| `Services/JwtTokenFactory.cs` | 登录态 JWT 的唯一解析与签发 | `JwtTokenFactory` | `Controllers/Api/AuthApiController.cs` | 配置键 PuddingJwtSettings：Jwt:Key/Issuer/Audience/ExpiryHours，缺省 168 小时；密钥缺失或过短即 fail closed，无硬编码兜底；HS256 + sm2_sig 载荷签名；登录与 Bootstrap 首次初始化共用 |
| `Controllers/Api/UserAvatarApiController.cs` | 头像上传唯一契约 POST /api/users/{userId}/avatar | `UserAvatarApiController` | `Services/UserAvatarStorageService.cs` | multipart 字段 file，返回 { avatar }；上传自己需登录、为他人需 Admin(403)；统一 PNG/JPG/WebP 与 5 MiB 上限；SaveForUserAsync 复用落盘/写库/旧文件清理；GET 匿名查任意用户头像 |
| `Controllers/Api/AppUserApiController.cs` | 用户管理 CRUD/密码/角色 | `AppUserApiController` | `Controllers/Api/UserAvatarApiController.cs` | 收紧为 [Authorize(Roles = "admin")]；AppUserDto 携带 Avatar |
| `Services/UserAvatarStorageService.cs` | 头像落盘与路径防护 | `UserAvatarStorageService` | `Controllers/Api/UserAvatarApiController.cs` | 落盘 wwwroot/user-avatars/（userId 前缀防穿越、原子写、TryDelete 限根内）；允许 MIME 仅 PNG/JPEG/WebP（GIF 已移除） |
| `Services/Sm2JwtSigner.cs` | JWT payload 的 ECDSA-P256 签名 | `Sm2JwtSigner` | `Services/JwtTokenFactory.cs` | 缺持久密钥时仅记进程临时密钥告警与公钥 SHA-256 指纹；禁止日志输出私钥/公钥材料 |

## 子代理 & 诊断

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `Middleware/TraceableExceptionMiddleware.cs` | 未处理异常的可检索 500 | `TraceableExceptionMiddleware` | — | 仅当 RequestAborted 已取消时把 OperationCanceledException 视为客户端断开（499 + Debug），不污染 Error 日志 |
| `Services/SubAgentManager.cs` | 子代理预算与结果身份 | `SubAgentManager`、`SubAgentResultIdentity.Compute(childRunId, 规范化终态, 父会话)` | — | 预算解析（N00）：`maxRounds = request.MaxRounds ?? options.MaxRounds`、maxToolCallsTotal 同理；同 SubSessionId + 新 runId 续跑重置计数器；resultId 复用为 MessageId 去重 |
| `Services/SubAgentPool.cs` | Core ISubAgentPool 的 Platform 实现 | `SubAgentPool` | `Services/SubAgentManager.cs` | — |
| `Services/SubAgentTransientDirectoryGcService.cs` | 历史空 Skill 脚手架目录 GC | `SubAgentTransientDirectoryGcService` | `Services/SubAgentPool.cs` | 以精确目录形状 + 子代理池 + durable run 终态多重门禁；先移入 retention-archive 隔离，延迟后再安全删除 |
| `Services/SubAgentDiagnosticsService.cs` | 子代理诊断 | `SubAgentDiagnosticsService` | `Services/SubAgentManager.cs` | — |
| `Services/FileSubAgentRunStore.cs` | 子代理运行文件存储 | `FileSubAgentRunStore` | `Controllers/Api/SubAgentRunController.cs` | 终态 run.json 固化轮次/工具/耗时/错误；支持可恢复终态 budget_exhausted；per-run gate + sharing violation 退避重试，耗尽写 archive-degraded.json 降级 |
| `Controllers/Api/SubAgentRunController.cs` | 认证运行检查器 API | `SubAgentRunController` | `Services/FileSubAgentRunStore.cs` | 详情从归档返回终态统计；events 分页返回可重建历史时间线的完整事件 payload |
| `Services/SessionStateManager.cs` | 会话与子代理持久状态查询 | `SessionStateManager` | `Services/SubAgentManager.cs` | 状态 DTO 按可复用 SubSessionId 关联最新 canonical runId，供托盘坞与检查器在漏收事件后恢复运行 |

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

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `Services/Goals/GoalSchemaBootstrapper.cs` | goal 五表幂等建表与索引冻结 | `GoalSchemaBootstrapper` | `Data/Entities/Goal*Entity.cs` | 单会话仅一个非终态 Goal 的 partial unique；outbox 幂等键索引 |
| `Services/Goals/GoalRunStore.cs` | 聚合写入原语与同事务事件直写 | `GoalRunStore` | `Services/Goals/GoalOutboxStore.cs` | Create/TryMutate 用 CAS + Func 卫兵；goal.* 事件与写入同事务；按 sequence 取当前非终态 WorkUnit |
| `Services/Goals/GoalOutboxStore.cs` + `GoalOutboxSignal.cs` | outbox 生命周期与降延迟信号 | `GoalOutboxStore`、`GoalOutboxSignal` | `Services/Goals/GoalContinuationWorker.cs` | due/claim/lease/fencing/recovery/defer/suppress/dead-letter；signal 只降延迟 |
| `Services/Goals/GoalContinuationWorker.cs` | durable intent 转受信 synthetic Acceptance | `GoalContinuationWorker` | `Services/Goals/GoalOutboxStore.cs` | 用户 Turn 优先；plan/node/fingerprint/预算入受信 Acceptance 与 prompt；payload 保 Unicode、转义 HTML 敏感字符 |
| `Services/Goals/GoalSettlementStore.cs` | Turn 全窗口终态判定与结算 | `GoalSettlementStore`、`GoalSettlementWorker.cs` | `GoalContractRefinementStore` | 唯一 writer；ApplyProgressAccounting 计数达 NoProgressBreakerThreshold 转 needs_user；落 goal.circuit_opened／goal.progress.recorded；Unsafe 与取消类豁免 |
| `Services/Goals/ConservativeGoalIterationVerifier.cs` | fail-closed 只读完成校验 | `ConservativeGoalIterationVerifier` | `Services/Goals/GoalSettlementStore.cs` | 自然语言完成无权写终态；仅 Task canonical Completed 允许 bound Goal 完成 |
| `Services/Goals/TaskGoalDispatchTransactionStore.cs` | 启动/派发单事务与幂等 replay | `TaskGoalDispatchTransactionStore` | `Services/Scheduling/TaskExecutionPlanCompiler.cs` | 单 Serializable 事务；事务前重算 route/plan 双 SHA-256，漂移 fail closed；原子退役遗留 Blocked Goal |
| `Services/TaskPlanning/TaskPlanningSchemaBootstrapper.cs` | 规划表冻结与启动幂等升级 | `TaskPlanningSchemaBootstrapper` | `Data/Entities/TaskPlanRunEntity.cs`、`TaskNodeEntity.cs`、`WorkUnitAwaitHandleEntity.cs` | 冻结 WorkspaceTask version 对应的执行快照（budgets/scopes/dependencies/checkpoint 与 durable AwaitHandle） |
| `Services/ConversationAcceptanceStore.cs` | synthetic Turn 原子受理并置 Running | `ConversationAcceptanceStore` | `Services/Goals/GoalRunStore.cs` | 重验 Goal/outbox/Task/Assignment/Reservation/Plan/WorkUnit 全围栏；lease 校验与续租统一用注入 TimeProvider |
| `Services/ExecutionCommandReader.cs` | 执行前沿 canonical 身份与预算重读 | `ExecutionCommandReader` | `Services/Goals/TaskGoalDispatchTransactionStore.cs` | metadata 只选择、不授权；漂移 fail closed |
| `Services/Goals/GoalCommandService.cs` | /goal 命令合同（含 policy 与幂等） | `GoalCommandService` | `SystemCommandHandler` | expectedVersion CAS；budget_exhausted 不可 resume；policy 属用户权能，不暴露为 agent 工具；同值幂等不写 goal.policy_changed |
| `Services/Goals/GoalQueryService.cs` | 只读投影（active/iterations/steps/todo） | `GoalQueryService` | `Controllers/Api/GoalQueriesController.cs` | todo 由归属 Agent 服务端解析（ITodoStore.ReadAsync）；未写拆解 = found:false |
| `Services/Goals/GoalRestartReconciler.cs` | 重启分流与 fence 换发 | `goal.paused`、`goal.resumed` | `GoalRunOptions.cs`、`MaxAutoResumesPerBoot` | 按 goal_runs.resume_policy 分流（auto_resume_on_restart 换 fence）超出即降级 paused；返回 GoalRestartReconcileResult(DisarmedCount, AutoResumedCount) |
| `Controllers/Api/GoalCommandsController.cs` | 结构化命令端点 | `GoalCommandsController` | `Services/Goals/GoalCommandService.cs` | POST /api/v1/conversations/{id}/goals/commands |
| `Controllers/Api/GoalQueriesController.cs` | 只读查询端点 | `GoalQueriesController` | `Services/Goals/GoalQueryService.cs` | 未写拆解 200+found:false；仅 goal 缺失 404 |
| `Data/Entities/Goal*Entity.cs` + `TaskGoalBindingEntity.cs` | Goal 五表与 Task binding 实体 | `GoalRunEntity` | `PlatformDbContext` | 枚举存 int、snake_case、version CAS；5 DbSet + partial unique 索引 |

关联修改：`SystemCommandHandler`（/goal 分支委托 GoalCommandService，不创建 Turn）；`PlatformDbContext`（5 个 DbSet + partial unique 索引）；`PuddingApplicationInitializer`（GoalSchemaBootstrapper + 启动 reconcile：按 resume_policy 分流 disarm / auto-resume，日志区分 disarmed 与 auto-resumed）。
相关配置见 `PuddingCore/Goals/GoalRunOptions.cs`：`NoProgressBreakerThreshold`（默认 3，边界 1..16）、`DefaultResumePolicy`、`MaxAutoResumesPerBoot`（默认 8，边界 0..64）。

## 外部访问令牌与 Agent 消息 API（ADR-075 / ADR-082）

| 文件 | 用途 | 关键符号 | 关联 | 约束 |
|------|------|------|------|------|
| `Services/Security/ExternalAccessTokenStore.cs` | 外部访问令牌的持久化 | `ExternalAccessTokenStore` / `ExternalAccessTokenMutationResult` | `Data/Entities/ExternalAccessTokenEntity.cs` | `external_access_tokens` 与 scopes/workspaces/audit 四表；CAS rename/revoke；按 keyId 索引查询；last-used 合并写落库 |
| `Services/Security/ExternalAccessTokenService.cs` | 外部令牌的签发与认证 | `ExternalAccessTokenService` / `ExternalAccessTokenAuthFailureReason` | `Services/Security/ExternalAccessTokenStore.cs` | RNG 生成 `pdt_v1_<keyId>.<secret>`；SHA-256 摘要固定时间比较；默认 90d、上限 365d；认证 fail-closed 六类原因；auth-fail 节流审计 |
| `Services/Security/ExternalAccessTokenHandler.cs` | 外部令牌的 ASP.NET 认证 scheme | `ExternalAccessTokenHandler` / `ExternalAccessTokenOptions` | `Services/Security/ExternalAccessTokenService.cs` | Header 解析 → 验证 → `ClaimsPrincipal`（不带 admin role）；成功时投递 last-used 合并器 |
| `Services/Security/ExternalAccessTokenAuthorization.cs` | 外部令牌的 requirement 与 Policy | `ExternalAccessTokenPolicyNames` / `ExternalWorkspaceRequirement` | `Services/Security/ExternalAccessTokenHandler.cs` | handler 校验 scheme 身份与 scope/workspace claim（ordinal 比较） |
| `Services/Security/ExternalAccessTokenUsageCoalescer.cs` | last-used 的有界合并写 | `ExternalAccessTokenUsageCoalescer` | `Services/Security/ExternalAccessTokenStore.cs` | 首次立即写，之后每 5 分钟至多一次；停机时 force flush |
| `Services/Security/ExternalAccessTokenSchemaBootstrapper.cs` | 四张 Token 表的幂等建表 | `ExternalAccessTokenSchemaBootstrapper` | `Data/Entities/ExternalAccessTokenEntity.cs` | 列名与 EF 实体一致 |
| `Services/Security/ExternalTaskApiOptionsProvider.cs` | externalTaskApi 配置读取 | `ExternalTaskApiOptionsProvider` | `<DataRoot>/config/system.json` | 读 `externalTaskApi` 节（30s 缓存），并在启动期做越界校验 |
| `Controllers/Api/AdminAccessTokenController.cs` | JWT-admin-only 的令牌管理 API | `AdminAccessTokenController` | `Services/Security/ExternalAccessTokenService.cs` | status/list/create（明文仅 201 返回一次）/detail/rename(CAS)/revoke(CAS)；不提供 reveal、unrevoke、删除与扩权 |
| `Controllers/External/V1/ExternalTokenInfoController.cs` | whoami 自检端点 | `ExternalTokenInfoController` / `GET /api/external/v1/token` | `Controllers/External/V1/ExternalApiGateFilter.cs` | — |
| `Controllers/External/V1/ExternalTaskController.cs` | External Task API v1 | `ExternalTaskController` | `Controllers/External/V1/ExternalTaskDtos.cs` | 写入要求 `Idempotency-Key`；patch 走 `If-Match` → CAS（428/412 带 currentTask 快照）；无 delete；Actor=`access-token:{tokenId}`、Origin=`external.api` |
| `Controllers/External/V1/ExternalWorkspaceAgentController.cs` | External Workspace 与 Agent API v1 | `ExternalWorkspaceAgentController` | `IWorkspaceAgentCatalog` | 消息以 connector/access-token actor 进入 Message Fabric 并强制 `Idempotency-Key`；`202 + Location` 与 Token-owned receipt 分离受理与 terminal reply |
| `Controllers/External/V1/ExternalApiGateFilter.cs` | External API 门控过滤器 | `ExternalApiGateFilter` | `Controllers/External/V1/ExternalTaskController.cs` | `Enabled=false` 一律 404；非 Loopback 的明文 HTTP 一律 400 |
| `Controllers/External/V1/ExternalTaskDtos.cs` / `Controllers/External/V1/ExternalWorkspaceAgentDtos.cs` | External V1 的稳定 wire DTO | `ExternalTaskDto` / `ExternalCreateTaskRequest` | `ExternalTaskController` | 与 Internal DTO / EF Entity 分 namespace；投影排除成员、Profile、Prompt、MainSessionId 与 Secret |
| `Services/ExternalApi/TaskEvaluationStore.cs` | 追加式任务评价存储 | `TaskEvaluationStore` | `Data/Entities/TaskEvaluationEntity.cs` | `task_evaluations` 与 `task.evaluated` 事件同事务；校验 score/verdict/taskVersionObserved/supersedes；不改 Task 状态与 version |
| `Services/ExternalApi/ExternalApiIdempotencyStore.cs` | External API 的幂等存储 | `ExternalApiIdempotencyStore` | `Data/Entities/ExternalApiIdempotencyEntity.cs` | key = SHA-256(token + method + route + key)；claim-then-execute；replay 与 409；失败释放；保留期顺带清理 |
| `Services/ExternalApi/ExternalTaskApiSchemaBootstrapper.cs` | 评价与幂等表的幂等建表 | `ExternalTaskApiSchemaBootstrapper` | `Data/Entities/ExternalApiIdempotencyEntity.cs` | 建 `task_evaluations` 与 `external_api_idempotency` |
| `Data/Entities/ExternalAccessTokenEntity.cs` / `Data/Entities/ExternalAccessTokenScopeEntity.cs` | 令牌主表与 scope 联结实体 | `ExternalAccessTokenEntity` / `ExternalAccessTokenScopeEntity` | `Services/Security/ExternalAccessTokenStore.cs` | 复合主键联结（token 与 scope 多对多） |
| `Data/Entities/ExternalAccessTokenWorkspaceEntity.cs` / `Data/Entities/ExternalAccessTokenAuditEventEntity.cs` | 令牌 workspace 联结与审计实体 | `ExternalAccessTokenWorkspaceEntity` / `ExternalAccessTokenAuditEventEntity` | `Services/Security/ExternalAccessTokenStore.cs` | audit 表 append-only（不更新、不删除） |
| `Data/Entities/TaskEvaluationEntity.cs` / `Data/Entities/ExternalApiIdempotencyEntity.cs` | 评价与幂等实体 | `TaskEvaluationEntity` / `ExternalApiIdempotencyEntity` | `Services/ExternalApi/TaskEvaluationStore.cs` | — |
| `Source/PuddingPlatformTests/Security/` / `Source/PuddingPlatformTests/Controllers/` | 外部令牌与 External API 的回归测试入口 | `ExternalAccessTokenServiceTests` / `ExternalTaskApiV1Tests` | `ExternalAccessTokenService` | 测试位于独立工程 `Source/PuddingPlatformTests/`，本表只登记入口，不复制用例清单 |

## 安全审批管理 API 与分类器健康（2026-09-21）

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `Controllers/Api/ToolApprovalAdminApiController.cs` | 审批白名单与审计 API | `ToolApprovalAdminApiController`、built_in、audit_agent、human、classifier | — | [Authorize] /api/tool-approval：allowlist 增删查改 + audit；FormatSource 曾以 _ => "human" 兜底致分类器规则误报，现如实回显枚举名；TryParseSource 补 classifier 否则 400 |
| `Controllers/Api/ClassifierHealthApiController.cs` | 分类器健康只读 API | `ClassifierHealthApiController` | `PuddingPlatformTests/Controllers/ClassifierHealthApiControllerTests.cs` | GET /api/classifier-health，[Authorize]；直接透传 IClassifierHealthReporter.Snapshot()；未接线 ⇒ 200 + configured=false 的明确未知态 |

契约细节：wire 仅白名单字段 `classifierId` / `health`（小写字符串） / `detail` / `consecutiveFailures` / `lastCheckedAtUtc` / `lastLatencyMs`；只依赖 PuddingCore 抽象（不引用 Runtime 具体类）。
per-key 连续 deferred 计数与退避档属 Agent 侧诊断（`classifier_status` 工具），不进本 API（规格 §8.2 裁定）；离线契约测试 `PuddingPlatformTests/Controllers/ClassifierHealthApiControllerTests.cs`（7 例）。

## 持久化

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `Data/` | EF Core DbContext、实体与迁移 | `PlatformDbContext` | `Data/PlatformSqliteConnectionInterceptor.cs` | 实体枚举存 int、snake_case、version CAS |
| `Data/PlatformSqliteConnectionInterceptor.cs` | SQLite 连接初始化与 PRAGMA 顺序 | `PlatformSqliteConnectionInterceptor` | `Data/` | 首条装 30 秒 busy_timeout，再执行其余 PRAGMA，避免连接设置本身在 writer 竞争时过早失败 |
| `Migrations/` | EF Core 迁移脚本 | `Migrations` | `Data/` | 迁移随模型演进，只增不改 |
| `DesignTimeDbContextFactory.cs` | 设计时工厂 | `DesignTimeDbContextFactory` | `Data/` | 仅设计时使用，不参与运行时装配 |
| `Services/Orchestration/AgentOrchestrationSchemaBootstrapper.cs` | 编排表与索引幂等初始化 | `AgentOrchestrationSchemaBootstrapper` | `Services/Orchestration/SqliteAgentOrchestrationStore.cs` | graph/revision/layout/run/run-input/node-run/event 幂等建表；补齐 node-run outputs_json 按端口输出列 |
| `Services/Orchestration/SqliteAgentOrchestrationStore.cs` | Graph/Run 存储：CAS、claim/fence、事件 | `SqliteAgentOrchestrationStore` | `Services/Orchestration/AgentOrchestrationEventFollower.cs` | 修订与独立布局 CAS；无 Run Graph 的 Head-CAS 删除；terminal commit 按无边推进后继并同事务提交 |
| `Services/Orchestration/AgentOrchestrationAuthoringService.cs` | Admin Revision 写入 | `AgentOrchestrationAuthoringService` | `Controllers/Api/AgentOrchestrationRevisionApiController.cs` | 校验 graphId/base/head；Core compiler 规范化；Head CAS 存不可变 Revision；审计字段服务端生成 |
| `Services/Orchestration/AgentOrchestrationManualRunService.cs` | Admin 手动运行命令 | `AgentOrchestrationManualRunService` | `Controllers/Api/AgentOrchestrationRunCommandApiController.cs` | 要求显式不可变 revisionId；冻结类型化输入后幂等 Create/Activate；不解析 Graph Head |
| `Services/Orchestration/AgentOrchestrationHttpHookService.cs` | 调试型 HTTP Hook | `AgentOrchestrationHttpHookService` | `Controllers/Api/AgentOrchestrationHttpHookApiController.cs` | 固定不可变 Revision；受限 JSON path 映射为 Inputs；sourceEventId 确定性 Run；不解析 Head、不冒充 Deployment |
| `Services/Orchestration/AgentOrchestrationCommittedEventSignal.cs` | 提交后进程内唤醒 | `AgentOrchestrationCommittedEventSignal` | `Services/Orchestration/AgentOrchestrationEventFollower.cs` | 只唤醒不带载荷；业务数据仍从 SQLite 读取 |
| `Services/Orchestration/AgentOrchestrationEventFollower.cs` | 高水位 replay 到 live 的连续事件读取 | `AgentOrchestrationEventFollower` | `Services/Orchestration/AgentOrchestrationCommittedEventSignal.cs` | 检测 sequence gap |
| `Controllers/Api/AgentOrchestrationApiController.cs` | 登录态只读发现与 SSE Watch | `AgentOrchestrationApiController` | `Services/Orchestration/AgentOrchestrationEventFollower.cs` | graph/run/catalog/revision/event 只读；Watch 支持 Last-Event-ID 续读 |
| `Controllers/Api/AgentOrchestrationLayoutApiController.cs` | 布局读取与 Admin CAS 写入 | `AgentOrchestrationLayoutApiController` | `Services/Orchestration/SqliteAgentOrchestrationStore.cs` | 不持有运行写命令端点 |
| `Controllers/Api/AgentOrchestrationManagementApiController.cs` | Admin Graph 新建/删除 | `AgentOrchestrationManagementApiController` | `Services/Orchestration/AgentOrchestrationAuthoringService.cs` | Admin-only；blank 占位图与「生成图片 → 展示图片」image-generation 模板；拒删有任何 Run 历史的 Graph |
| `Controllers/Api/AgentOrchestrationRevisionApiController.cs` | Draft validate 与 Revision PUT CAS | `AgentOrchestrationRevisionApiController` | `Services/Orchestration/AgentOrchestrationAuthoringService.cs` | 编排专用 Web/string-enum JSON 契约；冲突返回当前 Revision 事实；诊断含 elementType/elementId/portId |
| `Controllers/Api/AgentOrchestrationRunCommandApiController.cs` | 运行命令端点 | `AgentOrchestrationRunCommandApiController` | `Services/Orchestration/AgentOrchestrationManualRunService.cs` | POST /api/orchestrations/runs；Admin-only、1 MiB 上限、201/200 幂等回执与稳定 400/404/409 |
| `Controllers/Api/AgentOrchestrationHttpHookApiController.cs` | HTTP Hook 端点 | `AgentOrchestrationHttpHookApiController` | `Services/Orchestration/AgentOrchestrationHttpHookService.cs` | POST /api/orchestrations/hooks/{graphId}/{triggerId}?revisionId=...；Admin-only、1 MiB、201/200 幂等 |

## 多媒体

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `Services/ImageGenerationService.cs` | 图片生成 | `ImageGenerationService` | `Services/VisionArtifactStorageService.cs` | — |
| `Services/VisionArtifactStorageService.cs` | Workspace 原图与派生缓存 | `VisionArtifactStorageService`、`.Preprocessing.cs`、`ImagePreprocessing.cs` | `Services/VisualArtifactObservationService.cs` | 64MiB 与实际格式/尺寸/EXIF；模型边界低分辨率副本与超尺寸概览；原图不覆盖，变换按源与版本化参数复用；解码串行限并发 |
| `Services/VisualArtifactObservationService.cs` | 视觉观察 | `VisualArtifactObservationService` | `Services/VisionArtifactStorageService.cs` | — |
| `Services/AudioArtifactStorageService.cs` | 音频存储 | `AudioArtifactStorageService` | `Services/AudioTranscriptionService.cs` | — |
| `Services/AudioTranscriptionService.cs` | 音频转录 | `AudioTranscriptionService` | `Services/AudioArtifactStorageService.cs` | — |
| `Services/VoiceSynthesisService.cs` | 语音合成 | `VoiceSynthesisService` | `Services/AudioArtifactStorageService.cs` | — |

## 提供商配置

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `Services/LlmProviderFileService.cs` | LLM Provider/模型文件配置与余额查询 | `LlmProviderFileService` | `Services/ILlmBalanceProvider.cs` | 协议只在模型 DTO 与写入请求；GetBalanceAsync 解析 apiKey（ApiKey/${ENV}/{{vault:NAME}}/ApiKeyRef→KeyVault）后按注册表 CanHandle 分发；未注册返回「暂不支持」DTO；apiKey 不进日志 |
| `Services/ILlmBalanceProvider.cs` | 余额查询适配器契约 | `ILlmBalanceProvider` | `Services/DeepSeekLlmBalanceProvider.cs` | CanHandle(provider) + QueryAsync(provider, apiKey, ct)；网络错误抛 HttpRequestException（控制器映射 502）；上游业务错误返回 IsAvailable=false DTO |
| `Services/DeepSeekLlmBalanceProvider.cs` | DeepSeek 余额适配器 | `DeepSeekLlmBalanceProvider`、`LlmBalanceQuery` | `Services/ILlmBalanceProvider.cs` | GET {baseUrl 剥尾 /v1}/user/balance + Bearer；解析 is_available/balance_infos 与 error.message；CanHandle：providerId 含 deepseek 或 baseUrl 指向 deepseek.com |
| `Controllers/Api/LlmProviderApiController.cs` | Provider CRUD/配额/余额出口 | `LlmProviderApiController` | `Services/LlmProviderFileService.cs` | GET api/llm/providers/{providerId}/balance；KeyNotFound→404 / InvalidOperation→400 / HttpRequestException→502 |
| `Services/ChannelConfigurationFileService.cs` | 渠道配置文件服务 | `ChannelConfigurationFileService` | — | 单文件配置（21KB） |
| `Services/VoiceProviderFileService.cs` | 语音提供商配置文件服务 | `VoiceProviderFileService` | — | 单文件配置（18KB） |

余额链路测试：`PuddingPlatformTests/Services/DeepSeekLlmBalanceProviderTests.cs`（8 用例：解析//v1 剥离/Bearer/非 2xx/网络错误/CanHandle 矩阵）+ `LlmProviderBalanceDispatchTests.cs`（4 用例：暂不支持/委托与密钥/404/400）；扩展步骤见 `Docs/12_features/服务商余额查询与多服务商计费适配器设计方案.md`。

## Token 计量

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `Services/TokenUsageRecorder.cs` | Token 用量记录与层级诊断 | `TokenUsageRecorder` | `Data/Entities/LlmGatewayUsageEventEntity.cs` | 持久化 parent/sub-agent 身份与零基 round；prefix-v2 归因 history_anchor_changed 与 serialization_version_changed；不复制 prompt 正文 |
| `Services/CacheDiagnosticsService.cs` | 会话级 Cache Miss Inspector 后端 | `CacheDiagnosticsService` | `Services/TokenUsageEventRepository.cs` | 汇总 token-weighted hit/miss、prefix churn、首次变化原因与逐轮事实 |
| `Services/GoodputAttributionService.cs` | Goodput 归因聚合（只读） | `GoodputAttributionService`、`UsageAttribution.Parse`、`PricingClassifier` | `Controllers/Api/StatsApiController.cs` | TraceId 中段→goal_iterations/execution_runs 归因；零成本却非零 token 不得当节省 |
| `Services/ConversationProjector.cs` | Conversation Event 增量投影（消息+读模型+checkpoint 同事务；陈旧 checkpoint 恢复） | `ConversationProjector` | `Services/AgentChat/ConversationReadModelProjectionStore.cs` | 单事务提交消息/摘要/read_state/checkpoint；读模型落后时补放尾部；usage/catalog/索引在 commit 后；指纹补记保留 |
| `Services/TokenUsageEventRepository.cs` | Token 事件持久化与诊断查询 | `TokenUsageEventRepository` | `Services/ConversationProjector.cs` | 向 Runtime 返回 Core 诊断 DTO |
| `Services/LlmGatewayUsageRecorder.cs` | Provider 成功边界逐请求计费账本 | `LlmGatewayUsageRecorder` | `Data/Entities/LlmGatewayUsageEventEntity.cs` | 与会话归因投影解耦 |
| `Data/Entities/LlmGatewayUsageEventEntity.cs` | 本地计费事实实体 | `LlmGatewayUsageEventEntity` | `Services/LlmGatewayUsageRecorder.cs` | 表 llm_gateway_usage_events；sourceId 唯一 |
| `Services/TokenUsageSchemaBootstrapper.cs` | 旧 SQLite Token 与指标列幂等升级 | `TokenUsageSchemaBootstrapper` | `Services/TokenUsageRebuildService.cs` | Token 字段与索引、context-layer UTF-8/GZIP 指标列、网关账本均幂等 |
| `Services/AppUserSchemaBootstrapper.cs` | AppUsers.Avatar 幂等补列 | `AppUserSchemaBootstrapper` | `Services/TokenUsageSchemaBootstrapper.cs` | 避免头像实体升级后 schema 漂移令登录查询 500 |
| `Services/TokenUsageRebuildService.cs` | 从网关活动重建计费事实 | `TokenUsageRebuildService` | `Services/TokenUsageDailyAggregateService.cs` | 保留无法覆盖的实时行；提交后按月失效按日聚合缓存 |
| `Controllers/Api/StatsApiController.cs` | 统计 HTTP 出口（月/趋势/层级） | `StatsApiController` | `Services/TokenUsageDailyAggregateService.cs` | 优先网关账本、无网关历史月份回退会话投影；monthly/series/context-layers 走闭日缓存 + 当天实时渐进加载 |
| `Services/TokenUsageDailyAggregateService.cs` | 按日 Token 聚合缓存 | `TokenUsageDailyAggregateService` | `Data/Entities/LlmUsageDailyAggregateEntity.cs` | 闭日聚合一次落 llm_usage_daily_aggregates（day × source × provider × model）；当天实时；Rebuild 后按月失效 |
| `Services/ContextLayerDailyRollupService.cs` | context-layer 按日 rollup 缓存 | `ContextLayerDailyRollupService` | `Data/Entities/ContextLayerDailyRollupEntity.cs` | 存 JSON 分布；跨日精确合并 median/P95/distinctHashes；非对齐边界日直查明细 |
| `Services/DailyCacheUtility.cs` | 按日缓存共用工具 | `DailyCacheUtility` | `Services/TokenUsageDailyAggregateService.cs` | cache_key、UTC 日枚举、闭日标记；SQLite DateTimeOffset 文本范围格式（EF 无法翻译该比较） |
| `Data/Entities/LlmUsageDailyAggregateEntity.cs` | 闭日 Token 聚合行 | `LlmUsageDailyAggregateEntity` | `Services/TokenUsageDailyAggregateService.cs` | 表 llm_usage_daily_aggregates |
| `Data/Entities/ContextLayerDailyRollupEntity.cs` | 闭日层级分析 rollup | `ContextLayerDailyRollupEntity` | `Services/ContextLayerDailyRollupService.cs` | 表 context_layer_daily_rollups |
| `Data/Entities/StatsDailyCacheDayEntity.cs` | 闭日完成标记（含零数据日） | `StatsDailyCacheDayEntity` | `Services/TokenUsageDailyAggregateService.cs` | 表 stats_daily_cache_days；cache_key × day |
| `Services/TokenCostService.cs` | 成本计算 | `TokenCostService` | `Services/GoodputAttributionService.cs` | 按 provider/model 定价折算；未知 provider 归为 unpriced |

## 测试

`../PuddingPlatformTests/` — 渠道配置、Artifact、消息与通用编排；2026-08-11 Orchestration 定向测试 62/62 ✅，覆盖 Graph/Run 发现、Revision/Layout CAS、Draft validate、Graph 生命周期、冻结 Run Inputs、手动运行、后继 Ready/失败 Skipped 与 Run 原子终态、两节点图片模板、HTTP Hook 映射/幂等冲突；
2026-08-22 新增 StatsApiController/TokenUsageDailyAggregate/ContextLayerDailyRollup/TokenUsageRebuild 定向 18/18 ✅（闭日缓存命中、空日完成标记、当天实时不落缓存、按月失效、rollup 跨日精确合并、非对齐边界直查）
