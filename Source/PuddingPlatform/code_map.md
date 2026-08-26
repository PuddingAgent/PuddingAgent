# PuddingPlatform CodeMAP

> 平台层 | Session 管理 · API · EF Core 持久化 · 消息网关

## 会话管理

| 文件 | 用途 |
|------|------|
| `Services/SessionStateManager.cs` | 🔑 会话状态管理（88KB，核心） |
| `Services/SessionEventStreamService.cs` | 会话事件流 |
| `Services/SessionStateStore.cs` | 会话状态持久化 |
| `Services/SessionSteeringService.cs` | 会话路由 |
| `Services/SessionCompactionEventEmitter.cs` | 压缩事件发射 |
| `Services/SessionTitleService.cs` | 会话标题 |

## 对话 & 聊天

| 文件 | 用途 |
|------|------|
| `Services/ChatHistoryService.cs` | 聊天历史 |
| `Services/ChatMessageRepository.cs` | 消息仓储；ChatMessageRow 透传 `ContentPartsJson` canonical 信封 |
| `Services/ChatMessageSchemaBootstrapper.cs` | 存量 SQLite 幂等补 `ChatMessages.content_parts_json` 列 |
| `Services/AgentChat/ExecutionRunCoordinator.cs` | ADR-077：canonical parts + 冻结 Snapshot 判定 vision/文本占位；已删除自动预观察旁路，消息正文不再含本地绝对路径 |
| `Services/AgentChat/TurnOutputChunker.cs` | Delta 聚合分块器；非 delta 事件（工具/step）先 flush 已缓冲正文/思考再透传——「文本 → 工具 → 文本」轮次边界进入 canonical sequence（chat 交错时间线依赖，2026-08-24）；测试 `PuddingPlatformTests/Services/TurnOutputChunkerPayloadOwnershipTests.cs` |
| `Services/AgentChat/AgentConversationProjectionService.cs` | Chat 首屏/活动 run/消息明细投影；活动根 run 以最新根 `turn.started` 锚定，避免子代理 runId 抢占；active/full detail 都把 `message.content.appended` 与思考/工具/委派按真实 sequence 返回，并用 `TurnEventWindow` 显式标记 64 条活动窗口边界 |
| `Services/ChatTranscriptWriter.cs` | 转录写入 |
| `Services/ChatTelemetryRecorder.cs` | 遥测记录 |

## 消息网关

| 文件 | 用途 |
|------|------|
| `Services/MessageGateway/` | 🔑 消息网关投影（FeishuImageArtifactProjection 等） |
| `Services/Conversation/` | 对话接受/投影/事件存储 |
| `Services/ConversationEventStore.cs` | 对话事件存储（18KB） |
| `Services/ConversationProjectionWorker.cs` | 对话投影 Worker |
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
| `Controllers/Api/AuthApiController.cs` | 登录、JWT/Session 当前用户投影；`/api/currentUser` 异步读取 `AppUsers.Avatar`（空值回退自有 `/admin/assets/images/me.png`），刷新/重登后头像保持数据库最新值 |
| `Controllers/Api/UserAvatarApiController.cs` | 头像唯一上传契约 `POST /api/users/{userId}/avatar`（multipart 字段 `file`，返回 `{ avatar }`）；上传自己需登录、为他人上传需 Admin（403）；统一 PNG/JPG/WebP、5 MiB 上限；`SaveForUserAsync` 复用落盘/写库/旧文件清理；`GET` 匿名查任意用户头像 |
| `Controllers/Api/AppUserApiController.cs` | 用户管理 CRUD/密码/角色，收紧为 `[Authorize(Roles = "admin")]`；`AppUserDto` 携带 `Avatar` |
| `Services/UserAvatarStorageService.cs` | 头像落盘 `wwwroot/user-avatars/`（userId 前缀防穿越、原子写、TryDelete 限根内）；允许 MIME 仅 PNG/JPEG/WebP（GIF 已移除） |

## 子代理 & 诊断

| 文件 | 用途 |
|------|------|
| `Services/SubAgentManager.cs` | 子代理管理；固化系统预算/收尾宽限；以同一 SubSessionId + 新 runId 透明续跑并重置计数器；终态 usage 只存运行摘要，不重复写逐轮 TokenUsageEvents |
| `Services/SubAgentPool.cs` | Core `ISubAgentPool` 的 Platform 子代理池实现 |
| `Services/SubAgentTransientDirectoryGcService.cs` | 历史临时执行身份空 Skill 脚手架 GC；以精确目录形状 + 子代理池 + durable run 终态多重门禁，先移入 retention-archive 隔离，延迟后再安全删除 |
| `Services/SubAgentDiagnosticsService.cs` | 子代理诊断 |
| `Services/FileSubAgentRunStore.cs` | 子代理运行文件存储；终态 `run.json` 固化轮次、工具、耗时和错误；支持可恢复终态 `budget_exhausted` 与预算通知投影；归档读写同一 per-run gate + sharing violation 退避重试，重试耗尽写 archive-degraded.json 降级（ADR-060 §3.11） |
| `Controllers/Api/SubAgentRunController.cs` | 认证运行检查器 API；详情从归档返回终态统计，events 分页返回可重建历史时间线的完整事件 payload |
| `Services/SessionStateManager.cs` | 会话/子代理持久状态查询；子代理状态 DTO 按可复用 SubSessionId 关联最新 canonical runId，供托盘坞和检查器在漏收事件后恢复运行 |

## 任务系统（Tasks）

| 文件 | 用途 |
|------|------|
| `Services/Tasks/SqliteWorkspaceTaskStore.cs` | SQLite Task Ledger：`workspace_tasks` + `task_events` 两表、snake_case 列、CAS 乐观并发、Task+Event 原子提交、硬删语义、keyset 分页 |
| `Services/Tasks/TaskAgentCommandService.cs` | task_* 工具命令服务：claim/update 原子写回 Task+Attempt+Event+Binding 四表 |
| `Services/Tasks/TaskCommandService.cs` | PATCH/ApplyCommand 原子语义（含 status 显式迁移走 `CanTransition` 校验）；`DeleteTaskAsync` 智能删除（无历史 Backlog 硬删，其余任意状态归档软删）|
| `Services/Tasks/TaskDispatcher.cs` | 任务派发（RuntimeDispatchRequest.ActiveTask 注入到派发链）|
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
| `Services/Scheduling/AgentAvailabilityProjectionStore.cs` | 从配置、Task/Goal、Chat command、Message delivery、SubAgent 与 Reservation 的持久事实保守重建 Agent 状态；Unknown/过期不接 Auto |
| `Services/Scheduling/AgentExecutionReservationStore.cs` | 单 Agent/Task active 自动工作槽、lease、fencing token、renew/release/expiry |
| `Services/Scheduling/ConservativeExecutionWindowResolver.cs` | `anytime` allow；`inherit/off_peak_only` 在路由价格档案缺失时 Unknown/fail-closed |
| `Services/Scheduling/TaskAutoDispatchEvaluator.cs` | 无副作用确定性候选评估；偏好 Agent、依赖、30 分钟 idle grace、窗口、同轮单任务 |
| `Services/Scheduling/TaskAutoDispatchWorker.cs` | 默认关闭的有界恢复扫描；shadow 只评估，authoritative 对 eligible 候选重算窗口后调用唯一 Task→Goal 原子事务；同 Agent 每轮至多一个 Task |
| `Services/Scheduling/TaskBoundGoalOptions.cs` | Task-bound Goal 独立安全开关、Iteration 预算与 Reservation lease（默认关闭） |
| `Services/Scheduling/TaskSchedulingSchemaBootstrapper.cs` | Availability、Reservation、Task dependency 三表与唯一索引幂等建表 |
| `Data/Entities/AgentAvailabilityProjectionEntity.cs` | 持久 Availability 投影实体 |
| `Data/Entities/AgentExecutionReservationEntity.cs` | 自动工作租约与单调 fencing 实体 |
| `Data/Entities/TaskDependencyEntity.cs` | Task finish-to-start 依赖边实体 |

## Goal 持久控制面（Services/Goals/ · ADR-074 G1–G3 + Task-bound 原子启动源码链）

| 文件 | 用途 |
|------|------|
| `Services/Goals/GoalSchemaBootstrapper.cs` | goal_runs/goal_iterations/goal_outbox/goal_verifications/task_goal_bindings 五表幂等建表；含"单会话一个非终态 Goal" partial unique 与 outbox 幂等键索引（G1 冻结全部 schema） |
| `Services/Goals/GoalRunStore.cs` | 聚合写入原语：Create/TryMutate（CAS + Func 卫兵）与 goal.* ConversationEvent 同事务直写（照 AcceptanceStore 序列分配模式，不走自开事务的 EventStore） |
| `Services/Goals/GoalOutboxStore.cs` + `GoalOutboxSignal.cs` | continuation due/claim/lease/fencing/recovery/defer/suppress/dead-letter；signal 只降延迟 |
| `Services/Goals/GoalContinuationWorker.cs` | durable intent → 受信 synthetic Acceptance；用户 Turn 优先，Task-bound metadata 注入 ActiveTask |
| `Services/Goals/GoalSettlementStore.cs` + `GoalSettlementWorker.cs` | canonical Turn 终态 → Evidence Capsule → version/epoch/Task/Reservation gates → 下一 outbox 或终态 |
| `Services/Goals/ConservativeGoalIterationVerifier.cs` | fail-closed 只读 Verifier；自然语言完成无权写终态，Task canonical Completed 才允许 bound Goal 完成 |
| `Services/Goals/TaskGoalDispatchTransactionStore.cs` | Task/Assignment/Reservation/Binding/Goal/首个 Outbox/事件/Availability 单 Serializable 事务与幂等 replay |
| `Services/Goals/GoalCommandService.cs` | /goal 全命令合同：set/edit/replace/pause/resume/cancel/clear/status；conflict、幂等重放（source_command_id 唯一）、expectedVersion、budget_exhausted 不可 resume、feature flag 下保留 status/pause/cancel |
| `Services/Goals/GoalQueryService.cs` | 只读投影（active/latest/iterations） |
| `Services/Goals/GoalRestartReconciler.cs` | 启动 disarm：active→paused（bootId 锚点 + goal.paused 事件），幂等 |
| `Controllers/Api/GoalCommandsController.cs` | POST /api/v1/conversations/{id}/goals/commands（结构化命令） |
| `Controllers/Api/GoalQueriesController.cs` | GET /goal、/api/v1/goals/{id}、/goals/{id}/iterations |
| `Data/Entities/Goal*Entity.cs` + `TaskGoalBindingEntity.cs` | 五张表实体（枚举 int、snake_case、version CAS） |

关联修改：`SystemCommandHandler`（/goal 分支委托 GoalCommandService，不创建 Turn）；`PlatformDbContext`（5 个 DbSet + partial unique 索引）；`PuddingApplicationInitializer`（GoalSchemaBootstrapper + 启动 disarm）。

## 外部访问令牌（ADR-075 External Access Token，P1+P3 已实现）

| 文件 | 用途 |
|------|------|
| `Services/Security/ExternalAccessTokenStore.cs` | Token 持久化：`external_access_tokens` + scopes/workspaces/audit 四表、CAS rename/revoke、按 keyId 索引查询、last-used 合并写落库 |
| `Services/Security/ExternalAccessTokenService.cs` | 领域服务：RNG 生成 `pdt_v1_<keyId>.<secret>`、SHA-256 摘要固定时间比较、生命周期规则（默认 90d/上限 365d/每人 Active 上限）、认证 fail-closed（malformed/unknown/bad-secret/revoked/expired/owner-disabled）、auth-fail 节流审计 |
| `Services/Security/ExternalAccessTokenHandler.cs` | `PuddingExternalAccessToken` ASP.NET Core 认证 scheme（AuthenticationHandler）：Header 解析 → 验证 → ClaimsPrincipal（无 admin role）；成功投递 last-used 合并器 |
| `Services/Security/ExternalAccessTokenAuthorization.cs` | ExternalScopeRequirement/ExternalWorkspaceRequirement + Policy 名称；handler 校验 scheme 身份 + scope/workspace claim（ordinal）|
| `Services/Security/ExternalAccessTokenUsageCoalescer.cs` | last-used 有界合并写（首次立即、之后每 5 分钟至多一次；停机 force flush）|
| `Services/Security/ExternalAccessTokenSchemaBootstrapper.cs` | 四张 Token 表幂等建表（与 EF 实体列名一致）|
| `Services/Security/ExternalTaskApiOptionsProvider.cs` | `config/system.json` externalTaskApi 节读取（30s 缓存）+ 启动期越界校验 |
| `Controllers/Api/AdminAccessTokenController.cs` | JWT-admin-only 管理 API：status/list/create（明文仅 201 一次）/detail/rename(CAS)/revoke(CAS)；不提供 reveal/unrevoke/删除/扩权 |
| `Controllers/External/V1/ExternalTokenInfoController.cs` | `GET /api/external/v1/token` whoami 自检（ExternalApiGateFilter 门控）|
| `Controllers/External/V1/ExternalTaskController.cs` | External Task API v1（ADR-075 P2 基本功能）：list/get/create/patch(If-Match→CAS，428/412+currentTask 快照)/comments/evaluations/commands(白名单)；Actor=access-token:{tokenId}、Origin=external.api 注入；mutation 要求 Idempotency-Key；无 delete；RateLimiter/SSE Watch/OpenAPI 未实现 |
| `Controllers/External/V1/ExternalApiGateFilter.cs` | External API 门控：Enabled=false → 404；非 Loopback 明文 HTTP → 400 |
| `Controllers/External/V1/ExternalTaskDtos.cs` | V1 稳定 wire DTO（与 Internal TaskDtos 分 namespace） |
| `Services/ExternalApi/TaskEvaluationStore.cs` | 追加式评价：task_evaluations + task.evaluated 事件同事务；score/verdict/taskVersionObserved/supersedes 校验；不改 Task 状态/version |
| `Services/ExternalApi/ExternalApiIdempotencyStore.cs` | 简化幂等：key=SHA-256(token+method+route+key)、claim-then-execute、replay/409/失败释放、保留期顺带清理 |
| `Services/ExternalApi/ExternalTaskApiSchemaBootstrapper.cs` | task_evaluations + external_api_idempotency 幂等建表 |
| `Data/Entities/ExternalAccessToken*.cs` | 主表/scope/workspace/audit 四实体（复合主键联结 + append-only 审计）|
| `Data/Entities/TaskEvaluationEntity.cs` / `Data/Entities/ExternalApiIdempotencyEntity.cs` | 评价 + 幂等实体 |
| 测试 | `PuddingPlatformTests/Security/ExternalAccessToken*Tests.cs`（42 项）+ `Controllers/ExternalTaskApiV1Tests.cs` + `Services/TaskEvaluationStoreTests.cs` + `Services/ExternalApiIdempotencyStoreTests.cs`（P2 共 23 项）|

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
| `Services/TokenUsageRecorder.cs` | Token 用量记录；持久化 RuntimeExecutionIdentity 提供的 parent/sub-agent、零基 round、本轮 canonical 工具及 context layer Token/UTF-8/GZIP/hash/cache 诊断，不复制 prompt 正文 |
| `Services/ConversationProjector.cs` | Conversation Event 增量投影；usage 仅在 direct `session:trace:round` 行缺失时补记，父子身份来自持久关系、零基 round 来自 invocation index，未知工具数保持 NULL |
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
