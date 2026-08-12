# PuddingRuntime CodeMAP

> 运行时核心 | Agent Loop · LLM 调用 · 工具系统 · 上下文管线 · Git 20 工具

## 入口 & 配置

| 文件 | 用途 |
|------|------|
| `DependencyInjection.cs` | Runtime 服务注册入口 |
| `Services/PuddingConfigLoader.cs` | JSON 配置加载 |
| `Services/PuddingJsonConfig.cs` | 配置模型定义 |
| `Services/RuntimeExecutionConfigService.cs` | 执行配置：系统统一分配 600 轮、2400 工具调用、24h，以及 20 轮/30 分钟收尾宽限；父 Agent 不可覆盖 |

## Agent Loop

| 文件 | 用途 |
|------|------|
| `Services/AgentExecutionService.cs` | 🔑 执行编排入口，session 单写者，liveness/progress 报告 |
| `Services/AgentExecution/AgentExecutionService.Buffered.cs` | 非流式主循环（partial）；子代理 LLM 完成事件携带已脱敏的消息预览与不脱敏但有界的实际推理预览；重复消息只返回结构化 stop reason |
| `Services/AgentExecution/AgentExecutionService.Streaming.cs` | SSE 流式主循环（partial）；重复 message_id 的 done 帧标记 `DuplicateMessage` 且不产生可展示 reply |
| `Services/Messaging/MessageDeliveryDispatcher.cs` | durable Message Fabric 投递；入站转录沿用稳定 messageId，重复命中不得持久化或回发占位回复 |
| `Services/AgentExecution/AgentToolArguments.cs` | tool-call JSON → 参数转换 |
| `Services/AgentLoop/CanonicalWorkReport.cs` | 子代理五段报告解析/校验 |
| `Services/GoalMode/` | 🆕 Goal 模式 v2 执行器 |
| `Services/TurnExecutorAdapter.cs` | Turn 执行适配器 |

## 上下文管线

| 文件 | 用途 |
|------|------|
| `Services/ContextPipeline.cs` | 🔑 上下文组装管线；Tool 层强制 Direct/Delegated 判定与前三次调用委派合同；已拆为 `ContextPipelineLayers.cs`（层装配）与 `ContextPipelineOrchestrator.cs`（编排执行）两个 partial |
| `Services/ContextWindowManager.cs` | Token 窗口管理（48KB） |
| `Services/ContextAssemblyService.cs` | 上下文装配 |
| `Services/ContextBudgetAllocator.cs` | 预算分配 |
| `Services/ContextCompactionService.cs` | 压缩服务（71KB） |
| `Services/ContextHealthEvaluator.cs` | 健康评估 |
| `Services/SystemPromptBuilder.cs` | 系统提示构建（24KB） |

## LLM 调用

| 文件 | 用途 |
|------|------|
| `Services/DirectLlmClient.cs` | 🔑 直接 LLM 客户端；只按选中模型 protocol 路由；Provider 成功后以共享 ActivityId 必达写入逐请求 usage 账本 |
| `Services/LlmInvocationService.cs` | LLM 调用编排；把模型配置解析出的 protocol 传给 Direct/Controller 路径 |
| `Services/LlmProfileResolver.cs` | Profile 解析 |
| `Services/LlmRequestBudgetGuard.cs` | 预算守卫 |
| `Services/ProviderRateLimiter.cs` | 速率限制 |

## 工具系统

| 文件 | 用途 |
|------|------|
| `Tools/BuiltIns/` | 内置工具（Git 20 工具在此） |
| `Tools/BuiltIns/Search/SearchGrepTool.cs` | 代码文本搜索；排除目录在枚举前裁剪（修复 false-negative）；默认排除 `$outputWwwroot;dist;node_modules;bin;obj;.git;TestResults;artifacts;publish;.venv;.tmp`，`max_line_bytes` 单行截断 |
| `Tools/BuiltIns/Git/GitCommitTool.cs` | git_commit 提交工具；files 数组反序列化兼容 `string` 与 `string[]`（`StringOrStringArrayConverter`） |
| `Tools/BuiltIns/Files/FileChunkService.cs` | Runtime 文件工具的大文件分块/流式读取服务；不再反向依赖 Platform |
| `Tools/BuiltIns/Diagnostics/AgentDiagnosticsTool.cs` | Agent 上下文/Token 诊断；通过 Core 仓储契约读取持久化诊断 |
| `Tools/BuiltIns/SmartWorkflow/` | 七个角色化 Smart 入口；统一 `task` schema、历史参数归一化、子代理报告校验；`SmartWorkflowToolBase.cs` 校验失败时 partial-salvage：附验证说明后原样返回子代理实际产出，父 Agent 仍可用 |
| `Tools/Platform/` | 平台工具实现 |
| `Services/Tools/` | 工具运行时服务 |
| `Services/TerminalProcessManager.cs` | 终端进程管理 |
| `Services/TerminalSecurity.cs` | 终端安全 |

## 子代理 & 计划

| 文件 | 用途 |
|------|------|
| `Services/SubAgentInvocationService.cs` | 子代理调用；固化统一系统预算，并把公开 `resume_sub_agent_id` 映射为稳定 SubSessionId 续跑 |
| `Services/AgentLoop/SubAgentBudgetLifecycle.cs` | 子代理预算状态机：启动/80%/50% 通知、10-50 轮收尾宽限、可恢复终止判定 |
| `Services/DesignCouncilRuntimeService.cs` | MOA 运行时适配器；精确 provider/model 路由、可见性裁剪、只读派发、结果回填与暂停输入 |
| `Services/InMemorySubAgentOrchestrationRunStore.cs` | 进程内 MOA run 快照 store；Version CAS 防止重复 claim，不支持跨重启恢复 |
| `Services/Orchestration/AgentOrchestrationWorkerService.cs` | 通用编排 Runtime worker；领取已注册 executor 的 Ready 节点，90 秒续租 5 分钟 claim，以 fence 提交按端口输出/真实 child 身份并原子推进后继与 Run 终态 |
| `Services/Orchestration/AgentOrchestrationNodeInputResolver.cs` | 从冻结 Graph Inputs 与上游 `outputs[sourcePortId]` 解析节点输入；当前显式支持 `$`、Replace/Append、inline text 与 Artifact 列表，拒绝未实现 sourcePath/targetKey |
| `Services/Orchestration/SubAgentOrchestrationNodeExecutor.cs` | 只读 `pudding.agent.subagent` executor；冻结 role/template/provider/model，复用 `ISubAgentInvocationService` 与系统预算，提交 `result` 文本和 child Run/SubSession；用 workspace/graph 派生安全 archive owner，不把审计主体当目录 |
| `Services/Orchestration/ImageGenerateOrchestrationNodeExecutor.cs` | `pudding.media.image-generate` executor；经共享 resolver 读取 prompt/参考图，复用 `IImageGenerationService`，使用稳定 paid-call idempotency key，并把 Artifact 列表写入 `outputs.images` |
| `Services/Orchestration/ImagePreviewOrchestrationNodeExecutor.cs` | `pudding.media.image-preview` executor；经共享 resolver 读取上游 `images` Artifact 列表，并以同一引用写入自己的 `outputs.images`，不复制或内联图片 bytes |
| `Services/SubconsciousRecallPipeline.cs` | 潜意识召回管道（25KB） |
| `Services/SubconsciousPlanGenerationService.cs` | 计划生成 |
| `Services/TaskPlanning/` | 任务规划 |

## 记忆 & 知识

| 文件 | 用途 |
|------|------|
| `Services/MemoryWriteCoordinator.cs` | 记忆写入协调 |
| `Services/UserPreferenceService.cs` | 用户偏好管理：Prefetch 会话启动注入 System Prompt + save_preference 工具 Sync 存储 |
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

对应测试项目：`../PuddingRuntimeTests/` — Agent Loop、上下文管线、语音/图片 Provider；SubAgent/输入 resolver/图片生成/图片展示编排定向测试 4/4 ✅
