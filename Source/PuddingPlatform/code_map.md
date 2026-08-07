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
| `Services/AgentLLMConfigResolver.cs` | LLM 配置解析 |
| `Services/AgentRuntimeProfileResolver.cs` | Runtime Profile 解析（16KB） |
| `Services/AgentConversationLogService.cs` | 对话日志 |

## 子代理 & 诊断

| 文件 | 用途 |
|------|------|
| `Services/SubAgentManager.cs` | 子代理管理（55KB） |
| `Services/SubAgentPool.cs` | 子代理池 |
| `Services/SubAgentDiagnosticsService.cs` | 子代理诊断 |
| `Services/FileSubAgentRunStore.cs` | 子代理运行文件存储（40KB） |

## 持久化

| 文件 | 用途 |
|------|------|
| `Data/` | EF Core DbContext、实体、迁移 |
| `Migrations/` | EF Core 迁移 |
| `DesignTimeDbContextFactory.cs` | 设计时工厂 |

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
| `Services/LlmProviderFileService.cs` | LLM 提供商配置（27KB） |
| `Services/ChannelConfigurationFileService.cs` | 渠道配置（21KB） |
| `Services/VoiceProviderFileService.cs` | 语音提供商（18KB） |

## Token 计量

| 文件 | 用途 |
|------|------|
| `Services/TokenUsageRecorder.cs` | Token 用量记录（26KB） |
| `Services/TokenUsageRebuildService.cs` | 重建服务 |
| `Services/TokenCostService.cs` | 成本计算 |

## 测试

`../PuddingPlatformTests/` — 渠道配置、Artifact 存储、视觉观察、图片生成/投递（11/11 ✅）
