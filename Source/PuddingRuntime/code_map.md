# PuddingRuntime CodeMAP

> 运行时核心 | Agent Loop · LLM 调用 · 工具系统 · 上下文管线 · Git 20 工具

## 入口 & 配置

| 文件 | 用途 |
|------|------|
| `DependencyInjection.cs` | Runtime 服务注册入口 |
| `Services/PuddingConfigLoader.cs` | JSON 配置加载 |
| `Services/PuddingJsonConfig.cs` | 配置模型定义 |
| `Services/RuntimeExecutionConfigService.cs` | 执行配置：系统统一分配 600 轮、2400 工具调用、24h，以及 20 轮/30 分钟收尾宽限；规范化临时子代理目录保留/隔离参数；父 Agent 不可覆盖 |

## Agent Loop

| 文件 | 用途 |
|------|------|
| `Services/AgentExecutionService.cs` | 🔑 执行编排入口，session 单写者，liveness/progress 报告；把日期、召回和 inbound context 与当前消息组成 volatile User tail |
| `Services/AgentExecution/AgentExecutionService.Buffered.cs` | 非流式主循环（partial）；子代理 LLM 完成事件携带已脱敏的消息预览与不脱敏但有界的实际推理预览；重复消息只返回结构化 stop reason |
| `Services/AgentExecution/AgentExecutionService.Streaming.cs` | SSE 流式主循环（partial）；重复 message_id 的 done 帧标记 `DuplicateMessage` 且不产生可展示 reply |
| `Services/AgentExecution/ToolResultContextPolicy.cs` | 工具结果进入模型历史前的统一 8 KiB 边界；完整原文作为 workspace-scoped artifact 保存，sidecar manifest 固化 SHA-256、UTF-8 字节、行数和 session/tool/call 身份；模型输入不做脱敏并提供渐进读取路径，存储失败时 fail-open |
| `Services/Messaging/MessageDeliveryDispatcher.cs` | durable Message Fabric 投递；入站转录沿用稳定 messageId，重复命中不得持久化或回发占位回复 |
| `Services/AgentExecution/AgentToolArguments.cs` | tool-call JSON → 参数转换 |
| `Services/AgentLoop/CanonicalWorkReport.cs` | 子代理五段报告解析/校验 |
| `Services/GoalMode/` | 🆕 Goal 模式 v2 执行器 |
| `Services/TurnExecutorAdapter.cs` | Turn 执行适配器 |

## 上下文管线

| 文件 | 用途 |
|------|------|
| `Services/ContextPipeline.cs` | 🔑 上下文组装管线；区分执行 `AgentInstanceId` 与持久 `ConfigurationAgentInstanceId`，私有 Skill/人格/记忆/日志只读稳定身份；稳定 system prefix 与本轮 User tail 分离；Tool 层强制 Direct/Delegated 判定与前三次调用委派合同；L1 TOOLS 层索引文本从 session 已加载工具集合（append-only）生成（Core ∪ Loaded 不收缩），消除每轮全量重建导致的 prefix 漂移；已拆为 `ContextPipelineLayers.cs`（层装配）与 `ContextPipelineOrchestrator.cs`（编排执行）两个 partial |
| `Services/Skills/AgentSkillFileService.cs` | Agent 私有 Skill 文件服务；缺失索引的 Get/List 为无副作用空读取，只有显式初始化或写操作创建目录 |
| `Services/ContextWindowManager.cs` | Token 窗口管理；DB/JSONL 回填与内存裁剪已从扁平截断改为 `ContextTierPlanner` 分级填充（T0 全保 → T4 先弃，保新弃旧）；JSONL 冷启动路径经 `CompactionCoverageFilter` 过滤已压缩消息，防止复活 |
| `Services/CompactionCoverageFilter.cs` | 压缩覆盖过滤器；加载 session 最新 `CompactionCoverageManifest`（SourceMessageIds/SourceHashes）为覆盖集合，供 JSONL 冷启动路径去重；null factory / 无 manifest / 非法 JSON 均 no-op |
| `Services/ContextAssemblyService.cs` | 上下文装配 |
| `Services/ContextBudgetAllocator.cs` | 预算分配 |
| `Services/ContextCompactionService.cs` | 压缩服务；active 消息按页全量读取，80 条仅作为 Map-Reduce 块大小；所有待压缩消息进入 map 输入并通过覆盖校验后才写 `CompactedBy`；同一事务写 `CompactionCoverageManifests` 覆盖清单（OmittedCount==0 门禁）与 session 递增 `CompactionGeneration`（Source/TargetGeneration） |
| `Services/ContextHealthEvaluator.cs` | 健康评估 |
| `Services/SystemPromptBuilder.cs` | 系统提示构建（24KB） |

## LLM 调用

| 文件 | 用途 |
|------|------|
| `Services/DirectLlmClient.cs` | 🔑 直接 LLM 客户端；只按选中模型 protocol 路由；Provider 成功后以共享 ActivityId 必达写入逐请求 usage 账本 |
| `Services/CompositionSnapshot.cs` | 前缀缓存归因：逐请求计算 systemPromptHash/toolSpecHash/prefixHash（SHA-256 小写 hex）与 compositionVersion（进程内按 session 递增） |
| `Services/SqliteCompositionStore.cs` | 🆕 P0-5 `ICompositionStore` SQLite 实现：落 `CompositionSnapshots` 表（MemoryDbContext 同库），append-only（版本严格递增，重写/乱序抛 InvalidOperationException）、写穿、GetLatest 取最大版本 |
| `Services/LlmInvocationService.cs` | LLM 调用编排；把模型配置解析出的 protocol 传给 Direct/Controller 路径 |
| `Services/LlmProfileResolver.cs` | Profile 解析 |
| `Services/LlmRequestBudgetGuard.cs` | 预算守卫 |
| `Services/ProviderRateLimiter.cs` | 速率限制 |

## 工具系统

| 文件 | 用途 |
|------|------|
| `Tools/BuiltIns/` | 内置工具（Git 20 工具在此） |
| `Tools/BuiltIns/Search/SearchGrepTool.cs` | 代码文本搜索；排除目录在枚举前裁剪（修复 false-negative）；默认额外排除 `.pudding`，结果默认 20 条/16 KiB，支持显式扩大与继续检索 |
| `Tools/BuiltIns/Git/GitCommitTool.cs` | git_commit 提交工具；files 数组反序列化兼容 `string` 与 `string[]`（`StringOrStringArrayConverter`） |
| `Tools/BuiltIns/Files/FileChunkService.cs` | Runtime 文件工具的大文件分块/流式读取服务；不再反向依赖 Platform |
| `Tools/BuiltIns/Diagnostics/AgentDiagnosticsTool.cs` | Agent 上下文/Token 诊断；通过 Core 仓储契约读取持久化诊断 |
| `Tools/BuiltIns/SmartWorkflow/` | 七个角色化 Smart 入口；统一 `task` schema、历史参数归一化、子代理报告校验；`SmartWorkflowToolBase.cs` 校验失败时 partial-salvage：附验证说明后原样返回子代理实际产出，父 Agent 仍可用 |
| `Tools/Platform/` | 平台工具实现 |
| `Tools/Platform/ToolInvocationService.cs` | 统一调用入口；模型 callId、执行身份、CapabilityPolicy、deadline 与委派上下文的传递边界；目标协议要求 callId 进入 Registry 后保持不变 |
| `Tools/Platform/PuddingToolRegistry.cs` | Tool Registry、LLM schema 投影、AgentFirewall 门控与统一执行服务；canonical output/结构化错误/分阶段执行管线的主要改造入口 |
| `Services/Tools/` | 工具运行时服务 |
| `Services/Tools/ToolExposurePlanner.cs` | Provider 无关的工具暴露规划；名称稳定排序，超过阈值时保留核心工具并通过 `search_tools` 下一轮加载能力；已发现工具由 AgentSessionManager 在 live session 内保持加载 |
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

## 任务工具（TaskTools）

| 文件 | 用途 |
|------|------|
| `Services/TaskTools/TaskListTool.cs` | `task_list` 工具（按 workspace/过滤列查询）|
| `Services/TaskTools/TaskGetTool.cs` | `task_get` 工具（单任务查询）|
| `Services/TaskTools/TaskClaimTool.cs` | `task_claim` 工具（领取任务，写入 ActiveTask）|
| `Services/TaskTools/TaskUpdateTool.cs` | `task_update` 工具（状态迁移/disposition，走 `CanTransition`）|
| `Services/TaskTools/TaskToolModels.cs` | 工具参数/结果模型 + `TaskToolErrors` |

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
| `Services/CompositionRecoveryService.cs` | P0-5 步骤 5：跨 1h 超时/Core 重启从持久化 Composition 水合工具集合（append-only） |
| `Services/SessionExecutionGate.cs` | 执行门控 |
| `Services/SessionArchiver.cs` | 会话归档 |
| `Services/HeartbeatService.cs` | 会话超时资源清理（不是 Agent 自主心跳编排） |
| `Services/AgentWakeQueue.cs` | 唤醒队列 |
| `Services/StreamWatchdog.cs` | 流看门狗 |
| `Services/Events/InternalEventBus.cs` | 当前进程内 fire-and-forget pub/sub；目标只保留 non-critical live notification 或作为 durable publisher adapter |
| `Services/Events/EventDispatcher.cs` | 当前 SQLite 队列 dispatcher；目标按 consumer group 独立 checkpoint/retry/dead-letter |
| `Services/Hooks/HookPublisher.cs` | 当前生命周期事件 publisher adapter；目标正名为 LifecycleEventPublisher，真正同步干预由 Typed Hook Dispatcher 承担 |

## 插件 & 后台学习

| 文件/目录 | 用途 |
|------|------|
| `Services/Plugins/PluginManifestCatalog.cs` | 当前 `pudding-plugin/v1` manifest-only Tool catalog；目标 v2 多 contribution + dependency/scope/activation |
| `Services/Plugins/PluginPackageInstaller.cs` | 插件 ZIP 安全安装；目标增加签名/grant/staging activation/rollback |
| `Services/Background/SubconsciousWorkerService.cs` | 持久潜意识 Job 消费 + 当前周期入队循环；目标按 learning stage plugin 拆分，Timer 只产生幂等 Command |
| `Services/Background/SubconsciousJobScheduler.cs` | 空闲、并发和预算约束下的 Job lease 决策 |
| `Services/Hooks/SessionCompressedMemoryMaintenanceHook.cs` | 当前 `session.compressed` 事件到持久 Job 桥；目标作为 durable event consumer 重命名，不再称 Hook |

## 测试

对应测试项目：`../PuddingRuntimeTests/` — Agent Loop、上下文管线、语音/图片 Provider；SubAgent/输入 resolver/图片生成/图片展示编排定向测试 4/4 ✅
