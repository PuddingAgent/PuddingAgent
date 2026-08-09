# PuddingRuntime CodeMAP

> 运行时核心 | Agent Loop · LLM 调用 · 工具系统 · 上下文管线 · Git 20 工具

## 入口 & 配置

| 文件 | 用途 |
|------|------|
| `DependencyInjection.cs` | Runtime 服务注册入口 |
| `Services/PuddingConfigLoader.cs` | JSON 配置加载 |
| `Services/PuddingJsonConfig.cs` | 配置模型定义 |
| `Services/RuntimeExecutionConfigService.cs` | 执行配置：24h 硬上限、子代理并发与 timeout |

## Agent Loop

| 文件 | 用途 |
|------|------|
| `Services/AgentExecutionService.cs` | 🔑 执行编排入口，session 单写者，liveness/progress 报告 |
| `Services/AgentExecution/AgentExecutionService.Buffered.cs` | 非流式主循环（partial） |
| `Services/AgentExecution/AgentExecutionService.Streaming.cs` | SSE 流式主循环（partial） |
| `Services/AgentExecution/AgentToolArguments.cs` | tool-call JSON → 参数转换 |
| `Services/AgentLoop/CanonicalWorkReport.cs` | 子代理五段报告解析/校验 |
| `Services/GoalMode/` | 🆕 Goal 模式 v2 执行器 |
| `Services/TurnExecutorAdapter.cs` | Turn 执行适配器 |

## 上下文管线

| 文件 | 用途 |
|------|------|
| `Services/ContextPipeline.cs` | 🔑 上下文组装管线（85KB，核心） |
| `Services/ContextWindowManager.cs` | Token 窗口管理（48KB） |
| `Services/ContextAssemblyService.cs` | 上下文装配 |
| `Services/ContextBudgetAllocator.cs` | 预算分配 |
| `Services/ContextCompactionService.cs` | 压缩服务（71KB） |
| `Services/ContextHealthEvaluator.cs` | 健康评估 |
| `Services/SystemPromptBuilder.cs` | 系统提示构建（24KB） |

## LLM 调用

| 文件 | 用途 |
|------|------|
| `Services/DirectLlmClient.cs` | 🔑 直接 LLM 客户端；只按选中模型的 protocol 路由 Chat Completions/Responses/Anthropic Messages |
| `Services/LlmInvocationService.cs` | LLM 调用编排；把模型配置解析出的 protocol 传给 Direct/Controller 路径 |
| `Services/LlmProfileResolver.cs` | Profile 解析 |
| `Services/LlmRequestBudgetGuard.cs` | 预算守卫 |
| `Services/ProviderRateLimiter.cs` | 速率限制 |

## 工具系统

| 文件 | 用途 |
|------|------|
| `Tools/BuiltIns/` | 内置工具（Git 20 工具在此） |
| `Tools/Platform/` | 平台工具实现 |
| `Services/Tools/` | 工具运行时服务 |
| `Services/TerminalProcessManager.cs` | 终端进程管理 |
| `Services/TerminalSecurity.cs` | 终端安全 |

## 子代理 & 计划

| 文件 | 用途 |
|------|------|
| `Services/SubAgentInvocationService.cs` | 子代理调用 |
| `Services/SubconsciousRecallPipeline.cs` | 潜意识召回管道（25KB） |
| `Services/SubconsciousPlanGenerationService.cs` | 计划生成 |
| `Services/TaskPlanning/` | 任务规划 |

## 记忆 & 知识

| 文件 | 用途 |
|------|------|
| `Services/MemoryWriteCoordinator.cs` | 记忆写入协调 |
| `Services/KnowledgeAccessRuntime.cs` | 知识访问 |
| `Services/AgentLogRecallService.cs` | 日志召回 |

## 多媒体

| 文件 | 用途 |
|------|------|
| `Services/DashScopeAsrProvider.cs` | 语音识别 |
| `Services/DashScopeTtsProvider.cs` | 语音合成 |
| `Services/VolcengineArkImageGenerationProvider.cs` | 图片生成 |
| `Services/ManagedOggOpusTranscoder.cs` | 音频转码 |

## 会话 & 事件

| 文件 | 用途 |
|------|------|
| `Services/AgentSessionManager.cs` | 会话管理 |
| `Services/SessionExecutionGate.cs` | 执行门控 |
| `Services/SessionArchiver.cs` | 会话归档 |
| `Services/HeartbeatService.cs` | 心跳服务 |
| `Services/AgentWakeQueue.cs` | 唤醒队列 |
| `Services/StreamWatchdog.cs` | 流看门狗 |

## 测试

对应测试项目：`../PuddingRuntimeTests/` — Agent Loop、上下文管线、语音/图片 Provider
