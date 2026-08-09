# PuddingCore CodeMAP

> 核心抽象与契约 | 接口 · 模型 · 配置 · 序列化 · Agent 定义

## 抽象层

| 文件/目录 | 用途 |
|------|------|
| `Abstractions/` | 核心接口定义 |
| `Core/` | 核心实现 |
| `Platform/` | 平台抽象 |

## 模型（Models/）

| 文件 | 用途 |
|------|------|
| `ChatMessage.cs` | 聊天消息模型 |
| `LlmResponse.cs` | LLM 响应 |
| `LlmContinuationState.cs` | Provider opaque output items 跨工具轮次回放契约 |
| `LlmOptions.cs` | LLM 选项 |
| `StreamDelta.cs` | 流式增量 |
| `ToolCall.cs` | 工具调用 |
| `ToolParameterSchema.cs` | 工具参数 Schema（JSON Schema） |
| `ToolPermissionLevel.cs` | 工具权限级别 |
| `AgentContextEnvelope.cs` | Agent 上下文信封 |
| `AgentReplyImageDirective.cs` | 图片指令解析 |
| `AgentReplyImageGenerationDirective.cs` | ImageGeneration fence 解析（含 StripBlocks） |
| `AgentReplyVoiceDirective.cs` | 语音指令解析 |
| `MessageFabricModels.cs` | 消息结构模型 |
| `MemoryMaintenancePlanModels.cs` | 记忆维护计划（12KB） |
| `MemoryWriteCommandModels.cs` | 记忆写入命令（10KB） |
| `TaskPlanningModels.cs` | 任务规划模型（12KB） |
| `OmniRealtimeModels.cs` | 全双工实时模型 |
| `VisualReasoningModels.cs` | 视觉推理模型 |
| `VoiceRecognitionModels.cs` | 语音识别模型 |
| `VoiceSynthesisModels.cs` | 语音合成模型 |
| `AudioTranscodingModels.cs` | 音频转码模型 |
| `LlmMessageSequenceNormalizer.cs` | 消息序列规范化 |

## LLM 网关（Core/）

| 文件 | 用途 |
|------|------|
| `OpenAiLlmGateway.cs` | OpenAI-compatible Chat Completions 网关 |
| `ResponsesLlmGateway.cs` | OpenAI Responses API 网关；flat tools、SSE tool state、终态错误、output items 回放 |
| `AnthropicMessagesLlmGateway.cs` | Anthropic Messages API 网关；`x-api-key`、顶层 system、content blocks、工具回放和 SSE state |

## Agent 定义

| 目录/文件 | 用途 |
|------|------|
| `Agents/` | Agent 抽象定义 |
| `SubAgents/` | 子代理抽象 |

## 子代理编排（Orchestration/）

| 文件 | 用途 |
|------|------|
| `SubAgentOrchestrationModels.cs` | MOA 设计请求、专家成员、阶段门禁、只读 work item 与 Draft 计划契约 |
| `DesignCouncilPlanCompiler.cs` | 纯计划编译器；校验精确模型路由/多样性/独立终审并生成六阶段 DAG，不启动子代理 |

## 工具契约

| 文件 | 用途 |
|------|------|
| `Tools/` | 工具接口与基类 |

## 配置 & 序列化

| 目录/文件 | 用途 |
|------|------|
| `Configuration/` | 配置抽象；`llm.providers.json` 的协议归属模型，支持同一 Provider 混合 `openai` / `responses` / `anthropic` |
| `Serialization/` | 序列化契约 |
| `Skills/` | 技能系统抽象 |

## 事件 & 观测

| 目录/文件 | 用途 |
|------|------|
| `Events/` | 事件定义 |
| `Observability/` | 观测抽象 |
| `Diagnostics/` | 诊断抽象 |

## 运行时抽象

| 目录/文件 | 用途 |
|------|------|
| `Runtime/` | 运行时抽象 |
| `Services/` | 服务接口 |
| `Swarm/` | Swarm 协作抽象 |

## 测试

`../PuddingCoreTests/` — 工具契约、LLM 网关、消息围栏、MessageFabric
