# PuddingCore CodeMAP

> 核心抽象与契约 | 接口 · 模型 · 配置 · 序列化 · Agent 定义

## Goal 域（Goals/ · ADR-074 G0 合同层）

| 文件 | 用途 |
|------|------|
| `Goals/GoalContracts.cs` | GoalPhase/GoalCommandKind 枚举、GoalSnapshot、GoalCommand、GoalCommandRequest/Result、GoalLimits（256 硬上限、objective 1-4000）与 GoalErrorCodes |
| `Goals/GoalStateMachine.cs` | 纯状态机：转换矩阵、终态判定、resume/edit 卫兵、计数不变量、CanAcceptNewIteration 预算裁决 |
| `Goals/GoalEventTypes.cs` | goal.* canonical 事件目录 + ProducerComponent 常量（G1 冻结全部命名） |
| `Goals/GoalCommandTextParser.cs` | /goal 严格 grammar（中文/多行 objective、--rounds 1..256、子命令消歧） |
| `Goals/IGoalCommandService.cs` + `IGoalQueryService.cs` | 命令/查询应用服务契约（slash 与结构化 API 共用） |
| `Goals/GoalRunOptions.cs` | GoalRuns 配置节（Enabled 默认 false；局部配置不得扩大硬边界） |

## 抽象层

| 文件/目录 | 用途 |
|------|------|
| `Abstractions/` | 核心接口定义 |
| `Core/` | 核心实现 |
| `Platform/` | 平台抽象 |
| `Abstractions/ILlmGatewayUsageRecorder.cs` | 一次 Provider usage 对应一条本地计费事实的必达写入契约 |
| `Abstractions/ISubAgentPool.cs` | Runtime 调用子代理池所需的最小契约、池状态与池快照模型；实现留在 Platform |
| `Platform/IPlatformRepositories.cs` | Platform 持久化仓储契约；包含 Agent Token/熵诊断的 Core DTO 查询边界 |
| `Platform/AgentProjectionDtos.cs` | Agent 会话读模型；`ProcessSummaryItem.Sequence` 为 canonical 必填，active/detail 输出携带 `TurnEventWindow`（through/min/max/hasMoreBefore）供前端识别截断 |

## 模型（Models/）

| 文件 | 用途 |
|------|------|
| `ChatMessage.cs` + `LlmContentPart.cs` + `ChatMessageMultimodalNormalizer.cs` | 消息模型；ADR-077 有序 typed 内容部件（text/image→Artifact 引用+detail）与 v1 信封 `ContentPartsEnvelope`，Gateway 渲染统一经 normalizer（旧 VisualArtifactIds 派生 original） |
| `LlmResponse.cs` | LLM 响应 |
| `LlmContinuationState.cs` | Provider opaque output items 跨工具轮次回放契约 |
| `LlmOptions.cs` | LLM 选项、最近请求的 ContextUsageSnapshot、Tokenizer 校准和工具 schema 的 UTF-8/GZIP 归因（不保存正文） |
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
| `ResponsesLlmGateway.cs` | OpenAI/DeepSeek Responses API 网关；flat tools、明文 reasoning SSE、completed/failed/incomplete 终态、截断工具调用隔离与 output items 回放；ADR-077：user `input_image`（original→high）与 `function_call_output.output` [input_text, input_image] 数组，图片经 LlmVisualInputPlanner fail-closed |
| `AnthropicMessagesLlmGateway.cs` | Anthropic Messages API 网关；`x-api-key`、顶层 system、content blocks、工具回放和 SSE state |

## 推理紧凑编解码（Services/）

| 文件 | 用途 |
|------|------|
| `Services/ReasoningCompactCodec.cs` | 🆕 P1-3 推理紧凑编解码：v2 紧凑 JSON（UTF-8 字节偏移 + delta 时间戳 + SHA-256 hash），旧格式兼容、hash fail-open、UTF-8 多字节严格边界 |

## Agent 定义

| 目录/文件 | 用途 |
|------|------|
| `Agents/` | Agent 抽象定义 |
| `SubAgents/` | 子代理抽象 |
| `Runtime/SubAgentInvocationContracts.cs` | 子代理调用与系统执行预算契约；大型任务基线 600 轮/2400 工具调用/24h + 20 轮/30 分钟收尾宽限，并定义临时执行身份目录保留/隔离配置；父 Agent 只可指定 `resume_sub_agent_id`，不可传数值预算 |
| `Runtime/ContextAssemblyContracts.cs` | 上下文装配契约；同时携带执行 AgentInstanceId 与稳定 ConfigurationAgentInstanceId，避免把 SubSessionId 当持久配置目录 |
| `Runtime/ContextSegmentContracts.cs` | ContextSegmentLedger 数据契约（§6.1）+ ContextSegmentTier（T0–T4 分级枚举）|
| `Runtime/ContextTierPlannerContracts.cs` | T0–T4 分级规划器契约：段输入/分配结果/阈值选项 + IContextTierPlanner |
| `Runtime/ContextTierPlanner.cs` | 纯函数式分级规划器：轮次距离基础分级 → 原子组校正 → query 有界晋升 |
| `Runtime/CompositionContracts.cs` | 🆕 P0-5 SessionCompositionRecord（SessionId/CompositionVersion/SystemPromptHash/ToolSpecHash/PrefixHash/SkillManifestHash/SerializationVersion/ToolIds/ChangeReason/PermissionEpoch/CanonicalSystemPrefixHash）+ `ICompositionStore`（GetLatest/Append/Load，append-only）|

## 子代理编排（Orchestration/）

| 文件 | 用途 |
|------|------|
| `AgentOrchestrationModels.cs` | `pudding.agent-orchestration/v2` 通用图、修订、Graph Input/Trigger、typed node/control-data edge、受治理 predicate、权限策略与 append-only run event 契约 |
| `AgentOrchestrationComponentContracts.cs` | 组件/触发器注册表、contract hash、类型/MIME/基数/delivery 端口、多模态 Artifact 值与独立 GraphLayout 契约；内置 `pudding.agent.subagent` 的 request/context/result 端口及图片生成/展示组件，形成 typed 文本与 Artifact 组合链 |
| `AgentOrchestrationGraphCompiler.cs` | 纯图编译器；规范化、组件冻结、Graph Input/端口四维兼容、data binding/安全 sourcePath、受治理 control-edge predicate、引用/精确路由/写权限校验和确定性 DAG 拓扑排序，不执行节点 |
| `AgentOrchestrationPersistenceContracts.cs` | 通用图/run 发现摘要、revision/run/node 快照、冻结 Run Inputs 与按端口 node Outputs、真实 child Run/SubSession 身份、独立 GraphLayout CAS、无 Run Graph 的 Head-CAS 删除收据、只读 QueryStore、可写 Store、claim 请求与 committed-event signal 契约 |
| `DesignCouncilOrchestrationGraphAdapter.cs` | 将 MOA stage/work item 和上下文可见性映射为通用 gate/subAgent 节点及 control/data edge |
| `SubAgentOrchestrationModels.cs` | MOA 设计请求、专家成员、阶段门禁、只读 work item 与 Draft 计划契约 |
| `DesignCouncilPlanCompiler.cs` | 纯计划编译器；校验精确模型路由/多样性/独立终审并生成六阶段 DAG，不启动子代理 |
| `SubAgentOrchestrationRuntimeModels.cs` | MOA run/stage/work item 快照、claim、完成结果和用户上下文补充契约 |
| `SubAgentOrchestrationRuntimeContracts.cs` | MOA 乐观并发 run store、运行时命令/派发结果与 `IDesignCouncilRuntimeService` 契约 |
| `DesignCouncilRunStateMachine.cs` | 纯状态机；显式激活、并发领取、claim 校验、暂停恢复、法定人数、取消和终态 |

## 工具契约

| 文件 | 用途 |
|------|------|
| `Tools/` | 工具接口与基类 |
| `Tools/PuddingToolContracts.cs` | 原生 Tool 描述、反序列化与执行基类；当前结果仍为 Output/Error string，deepseek-harness 对齐方案将从这里演进 input/output schema、canonical value、结构化错误与不可变调用身份 |

## 存储管理契约（Storage/）

| 文件 | 用途 |
|------|------|
| `StorageMaintenanceContracts.cs` | Core 数据库/索引明细、语义清理目标、十分钟预览令牌、执行结果与 `IStorageMaintenanceService` 契约；Desktop 只通过 HTTP 使用 |

## 任务系统契约（Tasks/）

| 文件 | 用途 |
|------|------|
| `Tasks/WorkspaceTaskModels.cs` | WorkspaceTask 核心模型（28 字段 + 五列投影 Backlog/Ready/InProgress/Review/Done）|
| `Tasks/TaskStateMachine.cs` | 纯状态机：12 态 + 10 命令 + `CanTransition` 终态出边约束 |
| `Tasks/TaskPersistenceContracts.cs` | `ITaskStore` 持久化契约（keyset 分页 + CAS 乐观并发 + 硬删语义）|
| `Tasks/TaskStoreException.cs` | Store 契约异常（ErrorCode/TaskId/ExpectedVersion/ActualVersion）|
| `Tasks/TaskDispatchModels.cs` | 任务派发模型（RuntimeDispatchRequest.ActiveTask 注入）|
| `Tasks/TaskAgentCommandContracts.cs` | task_* 工具命令契约（List/Get/Claim/Update + `ITaskAgentCommandService`）|
| `Tasks/ActiveTaskRuntimeContext.cs` | ActiveTask 运行时上下文（派发链注入 ToolExecutionContext，含 ExpectedVersion）|

## 配置 & 序列化

| 目录/文件 | 用途 |
|------|------|
| `Configuration/` | 配置抽象；`PuddingDataPaths` 提供临时子代理目录隔离根；`llm.providers.json` 的协议归属模型支持同一 Provider 混合 `openai` / `responses` / `anthropic` |
| `Serialization/` | 序列化契约 |
| `Skills/` | 技能系统抽象 |

## 事件 & 观测

| 目录/文件 | 用途 |
|------|------|
| `Events/` | 事件定义 |
| `Models/InternalEvent.cs` | 当前跨 Runtime/Platform 的事件信封与队列 DTO；目标补齐 aggregate/version/partition、producer、actor、classification 及 run/turn/call 身份 |
| `Abstractions/IInternalEventBus.cs` | 当前进程内 pub/sub；不作为长期 durable replay 或同步 Hook 合同 |
| `Observability/` | 观测抽象 |
| `Diagnostics/` | 诊断抽象 |

目标新增 `Plugins/`、`Hooks/`、`Lifecycle/` 与 `Events/DomainEventContracts.cs`，分别承载 PluginActivation/Scope、Guard/Transform/Around、各 aggregate 状态机，以及 durable event envelope；详见 `Docs/deepseek-harness-pi-plugin-hook-event-architecture-2026-08-14.md`。

## 运行时抽象

| 目录/文件 | 用途 |
|------|------|
| `Runtime/` | 运行时抽象 |
| `Services/` | 服务接口 |
| `Swarm/` | Swarm 协作抽象 |

## 测试

`../PuddingCoreTests/` — 工具契约、LLM 网关、消息围栏、MessageFabric；2026-08-11 Orchestration 定向测试 81/81 ✅
