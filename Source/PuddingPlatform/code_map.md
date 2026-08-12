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
| `Services/ChatMessageRepository.cs` | 消息仓储 |
| `Services/ChatTranscriptWriter.cs` | 转录写入 |
| `Services/ChatTelemetryRecorder.cs` | 遥测记录 |
| `Services/ChatOmniRealtimeSessionRunner.cs` | 全双工实时会话 |

## 消息网关

| 文件 | 用途 |
|------|------|
| `Services/MessageGateway/` | 🔑 消息网关投影（FeishuImageArtifactProjection 等） |
| `Services/Conversation/` | 对话接受/投影/事件存储 |
| `Services/ConversationEventStore.cs` | 对话事件存储（18KB） |
| `Services/ConversationProjectionWorker.cs` | 对话投影 Worker |
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
| `Controllers/Api/AuthApiController.cs` | 登录、JWT/Session 当前用户投影；默认头像使用 Web 自有 `/admin/assets/images/me.png`，不得回退到框架示例或第三方远程资源 |

## 子代理 & 诊断

| 文件 | 用途 |
|------|------|
| `Services/SubAgentManager.cs` | 子代理管理；固化系统预算/收尾宽限；以同一 SubSessionId + 新 runId 透明续跑并重置计数器 |
| `Services/SubAgentPool.cs` | Core `ISubAgentPool` 的 Platform 子代理池实现 |
| `Services/SubAgentDiagnosticsService.cs` | 子代理诊断 |
| `Services/FileSubAgentRunStore.cs` | 子代理运行文件存储；支持可恢复终态 `budget_exhausted` 与预算通知投影 |

## 持久化

| 文件 | 用途 |
|------|------|
| `Data/` | EF Core DbContext、实体、迁移 |
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
| `Services/Diagnostics/DiagnosticRetentionService.cs` | 后台诊断保留期裁剪；仅遥测、上下文指标与运行活动，权威 session/conversation 事实源不在白名单 |
| `Services/RetentionPruningService.cs` | 🆕 platform.db 数据保留期裁剪 BackgroundService；补齐 session_event_log/telemetry_metric_events/runtime_activity/conversation_events 四张表保留期清理，表名/列名白名单防注入、分批删除+批间限速+VACUUM；ChatMessages 永不裁剪 |

## 多媒体

| 文件 | 用途 |
|------|------|
| `Services/ImageGenerationService.cs` | 图片生成 |
| `Services/VisionArtifactStorageService.cs` | 视觉存储 |
| `Services/VisualArtifactObservationService.cs` | 视觉观察 |
| `Services/AudioArtifactStorageService.cs` | 音频存储 |
| `Services/AudioTranscriptionService.cs` | 音频转录 |
| `Services/VoiceSynthesisService.cs` | 语音合成 |

## 提供商配置

| 文件 | 用途 |
|------|------|
| `Services/LlmProviderFileService.cs` | LLM Provider/模型文件配置；协议只存在于模型 DTO 与模型写入请求 |
| `Services/ChannelConfigurationFileService.cs` | 渠道配置（21KB） |
| `Services/VoiceProviderFileService.cs` | 语音提供商（18KB） |

## Token 计量

| 文件 | 用途 |
|------|------|
| `Services/TokenUsageRecorder.cs` | Token 用量记录（26KB） |
| `Services/TokenUsageEventRepository.cs` | Token 事件持久化与最近层级/熵诊断查询；向 Runtime 返回 Core 诊断 DTO |
| `Services/LlmGatewayUsageRecorder.cs` | Provider 成功边界逐请求计费账本；与会话归因投影解耦 |
| `Data/Entities/LlmGatewayUsageEventEntity.cs` | `llm_gateway_usage_events` 本地计费事实；sourceId 唯一 |
| `Services/TokenUsageSchemaBootstrapper.cs` | 旧 SQLite 的 Token 字段/索引与网关账本幂等建表 |
| `Services/TokenUsageRebuildService.cs` | 从成功网关活动 + session usage 帧重建计费事实，并保留无法覆盖的实时行 |
| `Controllers/Api/StatsApiController.cs` | 月度/趋势优先网关计费账本，无网关历史月份回退会话投影 |
| `Services/TokenCostService.cs` | 成本计算 |

## 测试

`../PuddingPlatformTests/` — 渠道配置、Artifact、消息与通用编排；2026-08-11 Orchestration 定向测试 62/62 ✅，覆盖 Graph/Run 发现、Revision/Layout CAS、Draft validate、Graph 生命周期、冻结 Run Inputs、手动运行、后继 Ready/失败 Skipped 与 Run 原子终态、两节点图片模板、HTTP Hook 映射/幂等冲突
