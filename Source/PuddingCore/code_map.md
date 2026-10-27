# PuddingCore CodeMAP
> 源指纹: Source/PuddingCore/**=421-96f5558b0e54 · 条目数: 145 · 最近整理: 2026-10-22

> 核心抽象与契约 | 接口 · 模型 · 配置 · 序列化 · Agent 定义

## Goal 域（Goals/ · ADR-074 G0 合同层）

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `Goals/GoalContracts.cs` | Goal 合同类型总集 | `GoalContracts` | `Goals/GoalStateMachine.cs` | GoalPhase/GoalCommandKind（含 Policy）、GoalSnapshot/GoalCommand（含 ResumePolicy）、GoalLimits（256 硬上限、objective 1-4000）、GoalErrorCodes（含 InvalidResumePolicy） |
| `Goals/GoalStateMachine.cs` | Goal 纯状态机 | `GoalStateMachine` | `Goals/GoalContracts.cs` | 转换矩阵、终态判定、resume/edit 卫兵、计数不变量、CanAcceptNewIteration 预算裁决 |
| `Goals/GoalEventTypes.cs` | goal.* canonical 事件目录 | `GoalEventTypes` | `Goals/GoalContracts.cs` | G1 冻结命名 + ProducerComponent 常量；冻结后新增 goal.policy_changed；消费方按 goal. 前缀或精确类型读取，无类型穷举 switch |
| `Goals/GoalCommandTextParser.cs` | /goal 严格 grammar 解析 | `GoalCommandTextParser` | `Goals/IGoalCommandService.cs` | 中文/多行 objective、--rounds 1..256、子命令消歧；保留子命令 policy 缺值或未知值 fail-closed 并列出合法取值，取值归一化为规范常量、大小写不敏感 |
| `Goals/IGoalCommandService.cs` + `IGoalQueryService.cs` | 命令/查询应用服务契约 | `IGoalCommandService`、`IGoalQueryService` | `Goals/GoalCommandTextParser.cs` | slash 与结构化 API 共用；查询含 steps 与 GetTodoAsync 拆解投影，复用 TodoItemView/TodoSummary |
| `Goals/GoalRunOptions.cs` | GoalRuns 配置节 | `GoalRunOptions` | `Goals/GoalContracts.cs` | Enabled 默认 false，局部配置不得扩大硬边界；NoProgressBreakerThreshold（默认 3，边界 1..16）、DefaultResumePolicy、MaxAutoResumesPerBoot（默认 8，边界 0..64）、GoalResumePolicies |
| `Goals/GoalContinuationContracts.cs` | continuation outbox wire 契约 | `GoalContinuationContracts` | `Goals/GoalRunOptions.cs` | durable outbox wire 值、受信 Acceptance fence、Task plan/node/fingerprint metadata 与稳定失败码 |
| `Goals/GoalVerificationContracts.cs` | 有界证据与 verifier 契约 | `GoalVerificationContracts` | `Goals/GoalStateMachine.cs` | Evidence Capsule、verdict/decision 与只读接口；含 WaitBlockerCodes/InfraFailureBlockerCodes/UnrecoverableBlockerCodes 白名单与 IsWaitBlockerCode/IsInfraFailureBlockerCode |
| `Goals/TaskBoundGoalContracts.cs` | Task-bound 原子启动契约 | `StartGoalFromTaskCommand` | `Goals/GoalContracts.cs` | 含 Agent 路由 SHA-256；原子启动结果/稳定码与跨域事务 Store 契约 |
| `Scheduling/TaskAutoDispatchContracts.cs` | evaluate-only 候选结果 | `TaskAutoDispatchContracts` | `Scheduling/TaskExecutionPlanContracts.cs` | 携带 taskType、Agent 选择原因与路由指纹，不代表已派发 |
| `Scheduling/TaskBacklogRefinementContracts.cs` | opt-in Backlog 只读合同 | `TaskBacklogRefinementContracts` | `Scheduling/TaskAutoDispatchContracts.cs` | ReadyCandidate/NeedsRefinement；不代表状态已迁移 |
| `Scheduling/TaskExecutionTrackingContracts.cs` | 自动 Task 只读五态跟踪 | `TaskExecutionTrackingContracts` | `Scheduling/TaskAutoDispatchContracts.cs` | 跨层事实摘要；不授权 repair/requeue/release |
| `Tasks/WorkspaceTaskModels.cs` + `Tasks/TaskPersistenceContracts.cs` | Task 状态与持久合同 | `WorkspaceTaskModels`、`TaskPersistenceContracts` | `Tasks/WorkspaceTaskModels.cs` | 含结构化 taskType、能力、provider/model 要求、显式 Agent fallback 与默认关闭的 auto-dispatch opt-in |

## 抽象层

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `Abstractions/` | 核心接口定义目录 | — | — | — |
| `Abstractions/IBrowserDelegationLifecycle.cs` | 委派生命周期端口（R4·§6.2） | `IBrowserDelegationLifecycle` | `Contracts` | Runtime/Platform 与 Host 之间唯一接缝，只用标量（PuddingCore 不引用 Contracts）；拒绝必须可判且带理由（调用方据此 fail-closed） |
| `Core/` | 核心实现目录 | — | — | — |
| `Observability/` | 出站请求诊断目录 | — | — | — |
| `Observability/LlmRequestBodyFingerprint.cs` | 出站报文指纹：UTF-8 字节数 + 小写 SHA-256 | `LlmRequestBodyFingerprint.Compute` | 无 | 纯函数；**不做换行归一化**（归一化会让不同报文同指纹） |
| `Observability/LlmRequestCapture.cs` | 发送前捕获实际报文指纹，并按开关记录 invocationId/modelId/本地估算值/净化后 bodyTokens/内联图片张数与 base64 字节/**报文里的 reasoning.effort** | `LlmRequestCapture`、`LlmRequestCapture.Enabled` | `Core/` 三个网关、`Runtime/Services/` | `PUDDING_CAPTURE_REQUEST` 未设时只有一次 bool 判断；只读字节重发，不改报文/头部/顺序；无 reasoning 段时记哨字符 `-` |
| `Observability/LlmBodyReasoningProbe.cs` | 从出站报文读 `reasoning.effort`，使「思考强度真的发出去了」可直接从日志验证 | `LlmBodyReasoningProbe.TryReadReasoningEffort` | `Observability/LlmRequestCapture.cs` | 纯函数；只在捕获开关打开时调用；解析失败一律返 null，不得抛异常 |
| `Observability/LlmBodyTextSanitizer.cs` | 把 `data:image/...;base64,...` 载荷替换为占位符并如实计数，使 bodyTokens 只覆盖 provider 按文本计费的部分 | `LlmBodyTextSanitizer.RedactInlineImages` | `Observability/LlmRequestCapture.cs` | 纯函数；只认 `data:image` 前缀，audio 等其它载荷故意不动 |
| `Platform/` | 平台抽象目录 | — | — | — |
| `Platform/ReasoningEffortPolicy.cs` | 思考强度唯一决策点：手动档位（**界面只暴露 深入=high / 极致=max**）> Agent/模型配置 > 模型默认 > **产品默认 high**；合法集合 `none/low/high/max` | `ReasoningEffortPolicy.Resolve`、`IsValid`、`Label`、`TryNormalizeSelection`、`IsUserSelectable`、`Default`、`AutoSelection` | `Platform/ExecutionRunContracts.cs` | 纯函数；产品默认仅在模型声明 `reasoning` 能力时生效（`useProductDefault`），否则返 null——openai 网关拿到非 null 就写 reasoning_effort，会给不支持该参数的模型造成 400。**2026-10-25**：界面删掉 low/medium（medium 服务商根本没有，旧界面选它会静默回落 high） |
| `Platform/IAgentReasoningEffortStore.cs` | Agent 级思考强度读写端口：`/effort` 命令与聊天面板下拉共写同一份存储 | `IAgentReasoningEffortStore` | `WorkspaceAgentReasoningEffortStore`（Platform）、`SystemCommandHandler` | 只有读一个值/写一个值两个方法：注入整个 Agent 文件服务会把创建/删除/冻结等写能力带进纯文本命令入口，也无法在无数据目录时断言 |
| `Abstractions/ILlmGatewayUsageRecorder.cs` | 本地计费事实的必达写入契约 | `ILlmGatewayUsageRecorder` | `Services/` | 一次 Provider usage 对应一条本地计费事实 |
| `Abstractions/ISubAgentPool.cs` | 子代理池最小契约 | `ISubAgentPool` | `Platform/` | 含池状态与池快照模型；实现留在 Platform |
| `Abstractions/ITokenUsageRecorder.cs` | Token 归因写入边界 | `ITokenUsageRecorder`、`TokenUsageAttribution` | `Services/` | 承载 canonical parent/sub-agent、零基 round 与本轮工具事实；默认接口实现保持旧 recorder 源码兼容 |
| `Platform/IPlatformRepositories.cs` | Platform 持久化仓储契约 | `IPlatformRepositories`、`ChatMessageRow` | `Abstractions/` | ChatMessageRow 含 WorkspaceId、稳定 platform/business MessageId、TurnId 与消息内容；供 Runtime 压缩/冷水合前增量镜像 canonical 转录并排除当前 Turn；含 Agent Token/熵诊断 Core DTO 查询边界 |
| `Platform/IExecutionCommandReader.cs` | ExecutionCommand 只读边界 | `IExecutionCommandReader`、`ExecutionWorkUnitContext` | `Platform/IPlatformRepositories.cs` | 返回由 canonical Goal/Task/Plan 解析的上下文及 rounds/tools/duration/token/cost 冻结预算 |
| `Platform/AgentProjectionDtos.cs` | Agent 会话读模型 | `AgentProjectionDtos`、`ProcessSummaryItem.Sequence`、`TurnEventWindow` | `Platform/IPlatformRepositories.cs` | Sequence 为 canonical 必填；active/detail 输出携带 TurnEventWindow（through/min/max/hasMoreBefore）供前端识别截断 |
| `Platform/ExecutionRunContracts.cs` | ExecutionRun 冻结快照合同 | `LlmRouteSnapshot`、`CallerLlmSnapshot` | `Platform/AgentProjectionDtos.cs` | V5：LlmRouteSnapshot 与 CallerLlmSnapshot 新增可选 VisionRequestPolicy? VisionPolicy；CallerLlmSnapshot.SupportsVision 为唯一 vision 能力投影（OrdinalIgnoreCase）。**2026-10-25**：新增可选 `string? ReasoningEffort`——本轮手动思考强度的唯一载体，放在快照而非 agent 请求，避免两套来源 |
| `Platform/ApprovalCode.cs` | 待审单确认码生成与校验 | `ApprovalCode` | `Controllers/` | 细节见后注 |
| `Platform/IRsiTrajectoryDataAccess.cs` | RSI-S3 轨迹数据访问接缝 | `IRsiTrajectoryDataAccess` | `Data/` | 细节见后注 |
| `Platform/RsiEventTimestamp.cs` | RSI-S3 时间戳契约 | `RsiEventTimestamp` | `Platform/IRsiTrajectoryDataAccess.cs` | 细节见后注 |

**`Platform/ApprovalCode.cs`**：待审单确认码的生成与校验——PuddingController 审批流程的**唯一凭据**（`GET /api/approval/*` 只下发脱敏投影）。`Generate()` 用 CSPRNG 取 8 位十六进制（**32 bit**，对照 `ApprovalId` 的 128 bit）；`Matches()` 用 `CryptographicOperations.FixedTimeEquals` 恒时比较。
2026-09-20 从 `InMemoryApprovalService` 抽出（原为 `Guid…[..8]` + 逐字节短路比较），使其可独立单测。`MaxFailedAttempts`=10：确认码失败累计达上限即把审批单置 `Expired` 作废（迭代 #16 实现——因审批服务已在迭代 #15 改为进程内实现，才首次可端到端测试）
**`Platform/IRsiTrajectoryDataAccess.cs`**：RSI-S3 轨迹数据访问接缝；`RsiEventRowWithTurn`（TurnId/Type/Payload/Sequence/OccurredAtRaw）——**不复用** `ConversationEventRow`（后者无 TurnId / 无 OccurredAt，**类型层面**装不下 B1 的 `RsiToolStep`）。查询**只走已索引键 `(TurnId, Type)`**；
⛔ 禁用 `CommandId` 形状：`conversation_events` 上 `command_id` **无任何索引**（4 条索引为 ConversationId+Sequence / EventId / TurnId+Type / CommittedAt），按它过滤即全表扫描且随事件总量线性增长
**`Platform/RsiEventTimestamp.cs`**：RSI-S3 时间戳契约：`DateTimeOffset.TryParse(raw, **InvariantCulture**, **AssumeUniversal**, out v) → v.ToUniversalTime()`。⛔ 禁用 `DateTimeStyles.None`（无偏移串会按本机时区解释 —— 本机 UTC+8 恰好错 8 小时，且**不会有断言失败**）；
⛔ 禁用裸 `DateTimeOffset.Parse`（走 CurrentCulture，非公历文化下解析成不同年份；平台内另有 7+ 处该写法，属既有技术债）；⛔ 不得省 `ToUniversalTime()`（字段名 `OccurredAtUtc` / `RsiEventTimestamp`，**类型名不得撒谎**）。不可解析 ⇒ **fail-closed 抛出**（不返回 null / MinValue、**不丢行**）—— 纯函数层（Rsi 装配器）绝不抛异常，边界层（IO）必须抛

- `Models/TokenUsageDto.cs`：provider usage；`ReasoningTokens` 保留 Responses `output_tokens_details.reasoning_tokens`，属于完成量而非额外计费，缺失时为 null。

## 外部 API 安全合同（ADR-075 / ADR-082）

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `Security/ExternalAccessTokenContracts.cs` | opaque Token 合同与认证 scheme 常量 | `ExternalAccessTokenContracts` | `Security/ExternalTaskApiScopes.cs` | 创建/查询合同与 claims；External principal 不获得 Admin role |
| `Security/ExternalTaskApiScopes.cs` | External API v1 scope 唯一白名单 | `ExternalTaskApiScopes` | `Security/ExternalAccessTokenContracts.cs` | 除 tasks.* 外含 workspaces.read、agents.read、messages.send；无 * 超级权限 |

## 模型（Models/）

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `ChatMessage.cs` + `LlmContentPart.cs` + `ChatMessageMultimodalNormalizer.cs` | 消息模型与多模态归一 | `ChatMessage`、`LlmContentPart` | `AgentReplyImageDirective.cs` | ADR-077 typed 内容部件与 v1 ContentPartsEnvelope；渲染经 normalizer；旧 VisualArtifactIds 派生 original；细节见「关键语义」 |
| `LlmResponse.cs` | LLM 响应与终止原因 | `LlmResponse` | `LlmOptions.cs` | `FinishReason` 透传截断状态，部分摘要不得覆盖持久历史 |
| `LlmContinuationState.cs` | Provider opaque output items 回放契约 | `LlmContinuationState` | `LlmResponse.cs` | 跨工具轮次回放 |
| `LlmOptions.cs` | LLM 选项与请求归因（F1） | `LlmOptions`、`MeasureLlmRequest`、`CaptureLlmRequest` | `LlmResponse.cs` | 含最近请求的 ContextUsageSnapshot、Tokenizer 校准与工具 schema 的 UTF-8/GZIP 归因（不保存正文）；请求归因分离见本表后注 |
| `StreamDelta.cs` | 流式增量 | `StreamDelta` | `LlmResponse.cs` | — |
| `ToolCall.cs` | 工具调用 | `ToolCall` | `ToolParameterSchema.cs` | — |
| `ToolParameterSchema.cs` | 工具参数 Schema（JSON Schema） | `ToolParameterSchema` | `ToolCall.cs` | — |
| `ToolPermissionLevel.cs` | 工具权限级别 | `ToolPermissionLevel` | `ToolCall.cs` | — |
| `AgentContextEnvelope.cs` | Agent 上下文信封 | `AgentContextEnvelope` | `ChatMessage.cs` | — |
| `AgentReplyImageDirective.cs` | 图片指令解析 | `AgentReplyImageDirective` | `AgentReplyImageGenerationDirective.cs` | — |
| `AgentReplyImageGenerationDirective.cs` | ImageGeneration fence 解析 | `AgentReplyImageGenerationDirective` | `AgentReplyImageDirective.cs` | 含 StripBlocks |
| `AgentReplyVoiceDirective.cs` | 语音指令解析 | `AgentReplyVoiceDirective` | `AgentReplyImageDirective.cs` | — |
| `MessageFabricModels.cs` | 消息结构模型 | `MessageFabricModels` | `ChatMessage.cs` | — |
| `MemoryMaintenancePlanModels.cs` | 记忆维护计划模型（12KB） | `MemoryMaintenancePlanModels` | `MemoryWriteCommandModels.cs` | — |
| `MemoryWriteCommandModels.cs` | 记忆写入命令模型（10KB） | `MemoryWriteCommandModels` | `MemoryMaintenancePlanModels.cs` | — |
| `TaskPlanningModels.cs` | 任务规划模型（12KB） | `TaskPlanningModels` | `ToolCall.cs` | — |
| `OmniRealtimeModels.cs` | 全双工实时模型 | `OmniRealtimeModels` | `StreamDelta.cs` | — |
| `VisualReasoningModels.cs` | 视觉推理模型 | `VisualReasoningModels` | `LlmResponse.cs` | — |
| `VoiceRecognitionModels.cs` | 语音识别模型 | `VoiceRecognitionModels` | `ChatMessage.cs` | — |
| `VoiceSynthesisModels.cs` | 语音合成模型 | `VoiceSynthesisModels` | `VoiceRecognitionModels.cs` | — |
| `AudioTranscodingModels.cs` | 音频转码模型 | `AudioTranscodingModels` | `VoiceSynthesisModels.cs` | — |
| `LlmMessageSequenceNormalizer.cs` | 消息序列规范化 | `LlmMessageSequenceNormalizer` | `ChatMessage.cs` | `NormalizeForProvider` 将显式 `IsContextSummary` 背景投影为 user；运行时保留 Assistant 身份，原生 reasoning/tool items 原样回传，不从正文猜测摘要 |

LlmOptions 请求归因分离（F1）：`MeasureLlmRequest` 只计量不发布（候选测量不得改写准备快照）；`CaptureLlmRequest` = 计量 + 发布 `currentPreparedRequest`；
`RecordProviderUsage` 带 `LlmCallPurposes`，只有主请求用途才允许改写准备快照/参与安全校准/写 `MeasuredRequestUsage`（`lastMeasuredRequest`），辅助与摘要调用单独落 `TryGetLastAuxiliaryMeasurement`；`Total` 与 `Prompt` 分开保存。

精确词表计量回归：`../PuddingCoreTests/Platform/ExactTokenizerRequestMeasurementTests.cs` 用独立 Unicode 向量验证生产计数入口与长消息预压缩测量。

## LLM 网关（Core/）

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `OpenAiLlmGateway.cs` | OpenAI 兼容 Chat Completions 网关 | `OpenAiLlmGateway` | `ResponsesLlmGateway.cs` | — |
| `ResponsesLlmGateway.cs` | Responses API 网关 | `ResponsesLlmGateway` | `OpenAiLlmGateway.cs` | UTF-8 紧凑请求 / DeepSeek 无状态协议 / 明文 reasoning / 终态即结束，提前 EOF 报错；图片与 Files 细节见后注 |
| `AnthropicMessagesLlmGateway.cs` | Anthropic Messages API 网关 | `AnthropicMessagesLlmGateway` | `ResponsesLlmGateway.cs` | x-api-key、顶层 system、content blocks、工具回放与 SSE state |
| `LlmVisualInputPlanner.cs` | 图片请求预算与规划（ADR-077） | `LlmVisualInputPlanner`、`PlanAsync` | `VisualInputRequestBudget.cs` | inline 小图 data URL / 大图 fail-closed 或 Files API 上传互斥规划；越界整请求 fail closed；细节见后注 |
| `VisionCapabilityContract.cs` | 模型级视觉能力合同（V5-T2） | `VisionCapabilityContract` | `LlmVisualInputPlanner.cs` | 唯一来源 = llm.providers.json 模型 vision 节；上限只可收紧不可放宽；放宽型配置加载期 fail-fast 拒绝；快照按「合同 ∩ 模型类别」投影；细节见后注 |
| `VisualInputRequestBudget.cs` | 请求级图片预算账本（V5 切片二） | `VisualInputRequestBudget` | `LlmVisualInputPlanner.cs` | 一次 payload 一实例，以显式参数贯穿全部 PlanAsync；累计只增不减；越界异常含维度/累计/增量/上限/来源与批次序号；无 AsyncLocal/静态状态 |
| `DeepSeekFilesApiClient.cs` | DeepSeek Files API 上传客户端 | `DeepSeekFilesApiClient` | `ProviderFileReference.cs` | multipart purpose=user_data；ApiKey 脱敏，异常只含 HTTP status 与错误摘要 |
| `ProviderFileReference.cs` | Files API 值类型 | `ProviderFileUploadResult`、`ProviderFileReference` | `DeepSeekFilesApiClient.cs` | FileId/ExpiresAt 计算；轻量引用 |
| `ProviderFileRefRecord.cs` | llm_provider_file_refs 行值类型 | `ProviderFileRefRecord`、`ProviderFileRefStatus` | `IFileRefStore.cs` | wire 映射 uploading/ready/delete_pending/expired/failed；ToReference() 映射轻量引用 |
| `IFileRefStore.cs` | Files 引用存储接口 | `IFileRefStore` | `ProviderFileRefRecord.cs` | TryGetReadyRefAsync / SaveAsync（幂等 upsert）/ UpdateExpiryAsync / MarkExpiredAsync / MarkDeletePendingAsync / ListExpiredAsync；FileRefNearExpirySkewSeconds=300 |

ResponsesLlmGateway：ADR-077 user `input_image`（original→high）与 `function_call_output.output` [input_text, input_image] 数组，图片经 `LlmVisualInputPlanner` fail-closed；V3-S2a 大图（>2MB）经 `DeepSeekFilesApiClient` 上传后以 `file_id` 引用（不输出 image_url/detail）。
ResponsesLlmGateway 使用 flat tools；completed / failed / incomplete 终态、截断工具调用隔离与 output items 回放保持完整。DeepSeek 不请求 encrypted_content，其他 Responses 服务商保留 store=false 与加密推理回放。
LlmVisualInputPlanner：inline 小图（≤2MB）data URL；大图 fail-closed 或（V3-S2a，有 uploader 时）Files API 上传 `file_id` 互斥规划；`VisionRequestPolicy` 含 Files 常量（64 MiB 单文件 / 200 MiB 总量 / lifetime 1h–30d）。
同上（V5 切片二）：`PlanAsync` 可选接收 `VisualInputRequestBudget`（budget=null 时行为不变），按实际序列化份数把每张图 charge 进请求级账本（份数/解码字节/wire 字节/token/file 上传字节），越界抛 `RequestLimitExceeded` 整请求 fail closed。
VisionCapabilityContract 细节一：唯一来源 = llm.providers.json 模型 `vision` 节 → `LlmModelInfo.VisionContract`；`ToPolicy()` 投影 `VisionRequestPolicy`（上限类 7 维按设计 §4.1 交集语义取 `Math.Min(配置值, 产品默认)`，只能收紧、不得放宽；合同版本进 `ImageTokenEstimatorVersion`）。
VisionCapabilityContract 细节二：V5-T4 放宽型配置由 `PuddingFileConfigLoader.ValidateVisionContract` / `RejectVisionLimitAboveCeiling` 在加载期 fail-fast 显式拒绝（不静默钳制后放行）；快照工厂据「合同 ∩ 模型类别」生成 `AgentExecutionSnapshot.VisionPolicy`，embedding/图像生成/无 vision 标签一律不投影。
## 推理紧凑编解码（Services/）

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `Services/ReasoningCompactCodec.cs` | 推理紧凑编解码（P1-3） | `ReasoningCompactCodec` | `Services/` | v2 紧凑 JSON（UTF-8 字节偏移 + delta 时间戳 + SHA-256 hash）；旧格式兼容；hash fail-open；UTF-8 多字节严格边界 |

## Agent 定义

| 目录/文件 | 用途 |
|------|------|
| `Agents/` | Agent 抽象定义 |
| `SubAgents/` | 子代理抽象 |
| `Platform/BuiltInAgentTemplates.cs` | 内置 Agent 模板的唯一权威源；V2 Default/Grant 直接决定 Low/High 子代理工具投影，Host 不得复制同全名类 |
| `Runtime/SubAgentInvocationContracts.cs` | 子代理调用与系统执行预算契约；工具曝光预算；`ContextMaintenanceOptions` 的注册窗口比例与摘要无进展期限 |
| `Runtime/ContextAssemblyContracts.cs` | 上下文装配契约 |
| `Runtime/ContextSegmentContracts.cs` | ContextSegmentLedger 数据契约（§6.1）+ ContextSegmentTier（T0–T4 分级枚举）|
| `Runtime/ContextTierPlannerContracts.cs` | T0–T4 分级规划器契约：段输入/分配结果/阈值选项 + IContextTierPlanner |
| `Runtime/ContextTierPlanner.cs` | 纯函数式分级规划器：轮次距离基础分级 → 原子组校正 → query 有界晋升 |
| `Runtime/CompositionContracts.cs` | SessionCompositionRecord 与 `ICompositionStore`（append-only） |
| `Runtime/PrefixCacheContracts.cs` | `prefix-v2` 请求前缀快照 |
| `Runtime/LlmInvocationContracts.cs` | 统一 LLM invocation 合同；`Purpose` 以非模型可见方式区分 agent/approval/compaction 计费与诊断 |
| `Runtime/ContextSummaryMarkers.cs` | 压缩摘要的请求侧标记唯一合同 |
| `Runtime/ContextCompactionContracts.cs` | 压缩终态、`ContextHealthSnapshot` 与容量预测契约；WarmPrefixSourceMessage 携带完整运行期原文与精确持久 ID |
| `Runtime/ContextUsageDisplay.cs` | 上下文用量的**唯一**显示口径投影（用户裁定 2026-10-24）：`ContextUsageDisplayProjection.Project` 纯函数给出 percent / remainingPercent / inputUnavailablePercent / segments；`ContextHealthSnapshot.Display` 由快照自身派生，`ExceedsAutoCompactRatio` 同分母 |
| `Runtime/SubAgentInvocationContracts.cs` | `ContextCompaction` 独立压缩策略；`ToolExposure` 工具 schema 投递预算（默认 48 个/40 KiB） |

**关键语义**

**`ChatMessage.cs` / `LlmContentPart.cs` / `ChatMessageMultimodalNormalizer.cs`**：ADR-077 是有序 typed 内容部件（`text`/`image` → Artifact 引用 + detail），与 v1 信封 `ContentPartsEnvelope` 并存；Gateway 渲染统一经 normalizer，旧 `VisualArtifactIds` 派生 `original` 部件。`SourceMessageId` 是**入站来源身份**，不发送给 provider。

**`Runtime/SubAgentInvocationContracts.cs`**：子代理调用与系统执行预算契约；单一预算域（N00，commit f096bc5）：未显式请求时统一使用系统 profile 默认 600 轮/2400 工具调用/24h，请求可显式携带 `int? MaxRounds/MaxToolCallsTotal/TimeoutSeconds`，无 managed WorkUnit 独立预算域，不存在 32/40/120 隐式截断；显式超过配置护栏（options 上限）显式拒绝；
收尾宽限 20 轮/30 分钟为加法（不预扣）；父 Agent 可指定 `resume_sub_agent_id` 与可选预算请求字段
**`Runtime/ContextAssemblyContracts.cs`**：上下文装配契约；同时携带执行 AgentInstanceId 与稳定 ConfigurationAgentInstanceId，避免把 SubSessionId 当持久配置目录
**`Runtime/CompositionContracts.cs`**：🆕 P0-5 SessionCompositionRecord（SessionId/CompositionVersion/SystemPromptHash/ToolSpecHash/PrefixHash/SkillManifestHash/SerializationVersion/ToolIds/ChangeReason/PermissionEpoch/CanonicalSystemPrefixHash）+ 
`ICompositionStore`（GetLatest/Append/Load，append-only）
**`Runtime/PrefixCacheContracts.cs`**：`prefix-v2` 请求前缀快照；稳定 system/tool/envelope version 加首条非 system `HistoryAnchorHash`，区分正常尾部 append 与 rehydrate/checkpoint 造成的历史 epoch 变化
**`Runtime/ContextSummaryMarkers.cs`**：压缩摘要的请求侧标记唯一合同（`<compact_summary>` 持久摘要 + `<compacted-summary>` warm-prefix checkpoint）；`ContainsMarker` 供计量分层与 UI 摘要桶同源识别。请求消息无 `ContentType`，只认其中一个标记会把另一条路径的摘要误计入对话桶（诊断 2026-10-07 §4.3）
**`Runtime/ContextCompactionContracts.cs`**：压缩终态、`ContextHealthSnapshot` 与容量预测契约；快照除用量分层外还输出容量**分量与来源**（`ProviderInputLimitTokens`/`RequestedOutputBudgetTokens`/`SafetyBufferTokens`/`EffectiveWindowSource` + `ContextEffectiveWindowSources` 常量），
使 UI 能解释「有效输入上限为什么比模型窗口小」而不必用窗口差值反推「预留输出」

## 子代理编排（Orchestration/）

| 文件 | 用途 |
|------|------|
| `AgentOrchestrationModels.cs` | `pudding.agent-orchestration/v2` 通用图与 run event 契约 |
| `AgentOrchestrationComponentContracts.cs` | 组件/触发器注册表与 contract hash 契约 |
| `AgentOrchestrationGraphCompiler.cs` | 纯图编译器（校验 + 确定性 DAG 拓扑排序） |
| `AgentOrchestrationPersistenceContracts.cs` | 通用图/run 发现与持久 CAS 契约 |
| `DesignCouncilOrchestrationGraphAdapter.cs` | 将 MOA stage/work item 和上下文可见性映射为通用 gate/subAgent 节点及 control/data edge |
| `SubAgentOrchestrationModels.cs` | MOA 设计请求、专家成员、阶段门禁、只读 work item 与 Draft 计划契约 |
| `DesignCouncilPlanCompiler.cs` | 纯计划编译器；校验精确模型路由/多样性/独立终审并生成六阶段 DAG，不启动子代理 |
| `SubAgentOrchestrationRuntimeModels.cs` | MOA run/stage/work item 快照、claim、完成结果和用户上下文补充契约 |
| `SubAgentOrchestrationRuntimeContracts.cs` | MOA 乐观并发 run store、运行时命令/派发结果与 `IDesignCouncilRuntimeService` 契约 |
| `DesignCouncilRunStateMachine.cs` | 纯状态机；显式激活、并发领取、claim 校验、暂停恢复、法定人数、取消和终态 |

**关键语义**

**`AgentOrchestrationModels.cs`**：`pudding.agent-orchestration/v2` 通用图、修订、Graph Input/Trigger、typed node/control-data edge、受治理 predicate、权限策略与 append-only run event 契约
**`AgentOrchestrationComponentContracts.cs`**：组件/触发器注册表、contract hash、类型/MIME/基数/delivery 端口、多模态 Artifact 值与独立 GraphLayout 契约；内置 `pudding.agent.subagent` 的 request/context/result 端口及图片生成/展示组件，形成 typed 文本与 Artifact 组合链
**`AgentOrchestrationGraphCompiler.cs`**：纯图编译器；规范化、组件冻结、Graph Input/端口四维兼容、data binding/安全 sourcePath、受治理 control-edge predicate、引用/精确路由/写权限校验和确定性 DAG 拓扑排序，不执行节点
**`AgentOrchestrationPersistenceContracts.cs`**：通用图/run 发现摘要、revision/run/node 快照、冻结 Run Inputs 与按端口 node Outputs、真实 child Run/SubSession 身份、独立 GraphLayout CAS、无 Run Graph 的 Head-CAS 删除收据、只读 QueryStore、可写 Store、claim 请求与 committed-event signal 契约

## 工具契约

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `Tools/` | 工具接口与基类目录 | — | — | — |
| `Tools/PuddingToolContracts.cs` | 原生 Tool 描述、反序列化与执行基类 | `PuddingToolContracts` | `Tools/` | 当前结果仍为 Output/Error string；deepseek-harness 对齐将演进 input/output schema、canonical value、结构化错误与不可变调用身份 |

## 交互面板域（Panels/）

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `Panels/PanelCanvasStore.cs` | 画布内容真相与持久边界 | `IPanelCanvasStore`、`PanelCanvasSnapshot`、`PanelCanvasLimits`、`PanelCanvasGuard` | `Panels/FilePanelCanvasStore.cs` | 内容真相只在 Core，Desktop 只是视图；HTML 上限 `MaxContentChars`；panelId 规整只有 `PanelCanvasGuard` 一处 |
| `Panels/FilePanelCanvasStore.cs` | 落盘实现，画布活过进程重启 | `FilePanelCanvasStore` | `Panels/PanelCanvasStore.cs` | 目录 `{DataRoot}/panels`、文件 `panel-{id}.json`；先写盘后改内存；坏文件跳过并计入 `SkippedFileCount` |
| `Panels/PanelAnnotationFile.cs` | 用户标注的只读回传快照 | `PanelAnnotationFile`、`IPanelAnnotationFileReader`、`PanelAnnotationFileName` | `../PuddingBrowser.AgentTools/PanelInspectTools.cs` | Desktop 写 / Core 只读；文件名公式单点定义；panelId 大小写不敏感；损坏即 null 不抛 |

**Panels 域约束细节**

- `PanelCanvasStore.cs`：`InMemoryPanelCanvasStore` 只服务测试与无盘场景；panelId 规整的唯一实现是 `PanelCanvasGuard`，调用方不得自行清洗后再传入。
- `FilePanelCanvasStore.cs`：**先写盘、后改内存**——反过来会出现「用户已经看到新内容、磁盘还是旧的」，重启后画布莫名回退；删除同理，文件删不掉就不改内存。单个坏文件只跳过并计入 `SkippedFileCount`，文件留在盘上便于事后排查。
- `PanelAnnotationFile.cs`：Desktop 写 `{DataRoot}/panels/panel-{id}.annotations.json`（`PanelAnnotationFileName.For` 单点定义，`SchemaVersion=1`，Core 与 Desktop 两侧各有字面量锁测试），Core 只读；**Core 的 HTTP 面不接收标注写入**，避免给 0.0.0.0 匿名接口再开写口。解析 fail-closed：JSON 坏、schema 不认、panelId 与文件名不符一律 null，读不到标注不让 `read_panel` 失败。
## 检索合同（Tools/Retrieval/，ADR-089 U0）

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `Tools/Retrieval/RetrievalContracts.cs` | 统一检索合同 | `RetrievalContracts`、`RetrievalCoverage`、`RetrievalMatchOutcome` | `Tools/Retrieval/RetrievalMatcher.cs` | IsComplete 仅由 Status==Complete 推导，Partial 必给原因；Complete/Partial/Truncated/Timeout/ContractError；MatchOutcome 为 NoMatch/Match/Timeout，超时不降级 |
| `Tools/Retrieval/RetrievalMatcher.cs` | 唯一匹配语义实现 | `RetrievalMatcher`、`TryMatch(line, ct)` | `Tools/Retrieval/RetrievalContracts.cs` | 字面量 Ordinal / 正则 CultureInvariant 归一；TryMatch 三态且 RegexMatchTimeoutException→Timeout（不吞不抛）、取消传播 OperationCanceledException；IsMatch 为兼容层（超时 false） |
| `Tools/Retrieval/RetrievalGlobMatcher.cs` | 共享 glob 匹配合同（ADR-089 U0-G1） | `RetrievalGlobMatcher` | `Tools/Retrieval/RetrievalMatcher.cs` | 契约与逐条边界见本表后注；判定优先级以「漏检优先」为准 |

RetrievalGlobMatcher 合同：`public static`，API 为 `Matches/MatchesFileName/MatchesRelativePath/HasWildcards/Normalize`；`null`/空/全空白匹配一切；`**/`、`**\`、前导 `**` 单次剥离。
通配符仅 `*`（任意长度可 0）与 `?`（恰好一个），`[ ] { }` 按字面；不含分隔符→比对文件名，含分隔符→比对相对路径（两侧分隔符归一 `/`）；无通配符→精确比较（禁子串包含）；`*`/`?` 不跨 `/`。
整串 `*.*` ≡ `*`（规范 3a，父级裁决：漏检优先 + SearchGrepTool 默认 `filePattern`）；大小写由 `ignoreCase` 控制（默认 true，恒用 Ordinal/CultureInvariant）；正则编译 + 有界缓存 512。

## 存储管理契约（Storage/）

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `StorageMaintenanceContracts.cs` | Core 数据库/索引明细与清理契约 | `StorageMaintenanceContracts`、`IStorageMaintenanceService` | `Storage/` | 含语义清理目标、十分钟预览令牌与执行结果；Desktop 只通过 HTTP 使用 |

## 任务系统契约（Tasks/）

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `Tasks/WorkspaceTaskModels.cs` | WorkspaceTask 核心模型 | `WorkspaceTaskModels` | `Tasks/TaskStateMachine.cs` | 28 字段 + 五列投影 Backlog/Ready/InProgress/Review/Done |
| `Tasks/TaskStateMachine.cs` | 纯状态机 | `TaskStateMachine` | `Tasks/WorkspaceTaskModels.cs` | 12 态 + 10 命令；CanTransition 含终态出边约束 |
| `Tasks/TaskPersistenceContracts.cs` | 任务持久化契约 | `ITaskStore` | `Tasks/TaskStoreException.cs` | keyset 分页 + CAS 乐观并发 + 硬删语义 |
| `Tasks/TaskStoreException.cs` | Store 契约异常 | `TaskStoreException` | `Tasks/TaskPersistenceContracts.cs` | 携带 ErrorCode/TaskId/ExpectedVersion/ActualVersion |
| `Tasks/TaskDispatchModels.cs` | 任务派发模型 | `TaskDispatchModels` | `Tasks/ActiveTaskRuntimeContext.cs` | RuntimeDispatchRequest.ActiveTask 注入 |
| `Tasks/TaskAgentCommandContracts.cs` | task_* 工具命令契约 | `ITaskAgentCommandService`、`TaskToolErrors`、`TaskContextRebuildDiagnostics` | `Tasks/TaskAdminContracts.cs` | 统一错误体 code/message/task_id/current_version/current_status/context_rebuild；重建失败给非泄露诊断 attempted/stage/outcome（卡 3133b149） |
| `Tasks/TaskAdminContracts.cs` | 看板契约（无依赖示 (no dependencies)、遇环示 (cycle detected)） | `IWorkspaceTaskAdminService`、`TaskAdminGetResult` | `Tasks/TaskDependencyContracts.cs` | 结果携 dependencies/dependency_tree/children；depends_on_task_ids 追加且幂等，自引用/成环/缺前置 fail-closed |
| `Tasks/TaskDependencyContracts.cs` | finish-to-start 依赖契约 | `ITaskDependencyStore` | `Tasks/TaskAdminContracts.cs` | Add/Remove/List/Evaluate + Satisfied/Waiting/Broken 评估 |
| `Tasks/ActiveTaskRuntimeContext.cs` | ActiveTask 运行时上下文 | `ActiveTaskRuntimeContext` | `Tasks/TaskDispatchModels.cs` | 由派发链注入 ToolExecutionContext，含 ExpectedVersion |

## 自动调度契约（Scheduling/）

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `Scheduling/AgentAvailabilityModels.cs` | 保守持久 Availability 状态模型 | `AgentAvailabilityModels` | `Scheduling/AgentExecutionReservationContracts.cs` | busy reason、版本/TTL 快照与 Store 边界 |
| `Scheduling/AgentExecutionReservationContracts.cs` | 自动工作 Reservation 与 lease 契约 | `AgentExecutionReservationContracts` | `Scheduling/AgentAvailabilityModels.cs` | 单 Agent 粒度；lease 与 fencing token |
| `Scheduling/ExecutionWindowModels.cs` | 执行窗口裁决模型 | `ExecutionWindowModels` | `Scheduling/AgentAvailabilityModels.cs` | Allow/Defer/Unknown 裁决及 route/profile/TTL 快照 |
| `Scheduling/TaskAutoDispatchContracts.cs` | evaluate-only 候选 verdict | `TaskAutoDispatchContracts` | `Scheduling/ExecutionWindowModels.cs` | 携带 Task/Availability/Conversation/Window、Agent route SHA 与 execution-plan SHA/version 事实 |
| `Scheduling/TaskExecutionPlanContracts.cs` | 版本化执行计划合同 | `TaskExecutionPlanSnapshot` | `Scheduling/TaskExecutionTrackingContracts.cs` | Explore/Plan/Change/Test/Review WorkUnit；依赖/冲突范围与轮次/工具/时长/token/cost 预算 |
| `Scheduling/TaskExecutionTrackingContracts.cs` | 五态健康投影与有界 repair 合同 | `TaskExecutionTrackingContracts` | `Scheduling/TaskExecutionPlanContracts.cs` | Task→Plan/WorkUnit→Goal/Execution 五态健康投影、outbox identity；写入权限仅授予独立白名单 repair 实现 |

## 配置 & 序列化

| 目录/文件 | 用途 |
|------|------|
| `Configuration/` | 配置抽象与 `llm.providers.json` 解析 |
| `Configuration/PuddingConfigModels.cs` | 系统配置记录；`desktop.guardian` 定义独立主管端口、构建源路径及健康策略，与 `desktop.bootstrap` 内部控制端口分离 |
| `Configuration/AtomicFileWriter.cs` | 原子配置写入：严格无 BOM UTF-8，磁盘 JSON 默认保留可读中文；保留显式编码器、命名策略与转换器 |
| `Serialization/` | 序列化契约 |
| `Skills/` | 技能系统抽象 |
| `Skills/Portfolio/` | G3 组合预算策略（`IVersionedCriterion` 阈值外置） |
| `Skills/Curation/` | G7 提炼产物契约（`SkillDistillationContract` 等） |
| `Skills/Family/` | G4 家族归类（`SkillFamilyClustering` 等纯函数） |
| `Skills/Retrieval/` | G4 关键词归一的唯一定义（`SkillKeywordNormalization`） |

**关键语义**

**`Configuration/`**：配置抽象；`PuddingDataPaths` 提供临时子代理目录隔离根；`PuddingBuildOutputSync` 提供同卷暂存、逐文件回滚、路径边界和 SHA-256 点火部署原语；`llm.providers.json` 支持每模型协议与版本化 `priceWindows/profileVersion/sourceUrl`，供低价自动调度 fail-closed 解析；
V5-T2：模型条目可选 `vision` 合同节（`PuddingVisionCapabilityConfig`，字节口径显式区分解码后/wire）由 `ValidateVisionContract` fail-fast 校验（存在即必须完整有效），`PuddingFileLlmConfigService.GetAllModels()` 投影为 `LlmModelInfo.VisionContract`
**`Skills/Portfolio/`**：🆕 G3 组合预算策略：`SkillPortfolioPolicy`（`IVersionedCriterion`，只含**被消费**的阈值）+ `AppliedPortfolioPolicy` + `SkillScoreSnapshot`（两态：`Observed`/`Unavailable` —— `Unavailable` **无 `Score` 字段** ⇒ “把无数据当低分”在类型上不可表达）。默认 `ZeroRegressionDefault(实测启用数, headroom)`。
⚠️ 该目录**不在**算子架构门禁扫描范围（门禁只扫 `PuddingCore/Operators/**` 与 `PuddingRuntime/Operators/**`）⇒ 门禁 PASS **不构成**本目录纯净性证据
**`Skills/Curation/`**：🆕 G7 提炼产物契约：`SkillCurationPolicy`（`IVersionedCriterion`，`MinRetainedValueRatio`/`ApplicabilityHeadings`/`PitfallHeadings`/`MinMarkdownLength`；
**无默认实参、无默认策略工厂** —— 比例下限是治理结论）+ `AppliedSkillCurationPolicy` + `SkillDistillationContract`（反笔记不变式 P1–P6 违规码 + `SourceTurnsOf`/`SourceSessionsOf` tag 级抽取，供 C1/C2 复用）。⚠️ 同 `Skills/Portfolio/`：**不在**算子架构门禁扫描范围
**`Skills/Family/`**：🆕 G4 家族归类：`SkillFamilyPolicy`（`IVersionedCriterion`，阈值外置：家族名相似度下限等；`Create` 无默认实参 ⇒ 阈值是治理结论）、`SkillFamilyClustering`（**静态纯函数** `Cluster(subjects, policy)`：并查集 + 确定性排序；
输出**含单成员簇**、按 `FamilyKey` 序数序、`FamilyKey = Members[0]`）、`SkillNameTokenization`（`StandardSeparators`/`Tokenize`/`Jaccard`；其分隔符与 `Skills/Retrieval/SkillKeywordNormalization.NameTokenSeparators` 是**同一份字符集**的两处表达，**有用例钉住相等**）、`SkillMergePolicy`（`IVersionedCriterion`，
`MinimumProceduralTextSimilarity` 合法区间 (0,1]）。⛔ 家族标签**只用于分组**，不参与生死判定
**`Skills/Retrieval/`**：🆕 G4 关键词**归一**的唯一定义（`SkillKeywordNormalization`）：`Collect(keywords, tags, skillId, name)` **逐字复现**原 `SkillEnforcerService.CollectKeywords`（口径唯一 ⇒ G7-C3 与 G4 必须共享同一来源）、`KeywordComparer = OrdinalIgnoreCase`（⛔ 不得改为序数敏感）、
`NameTokenSeparators`/`MinimumNameTokenLength`/`IsInjectableKeyword`。⚠️ 该归一口径**保留原始大小写**：消费侧一律用 `KeywordComparer` 比较，⛔ 不得自建折叠步骤（G4-D6c 教训：判据曾用序数比较 ⇒ 真实数据 116 处被误拒）

## 事件 & 观测

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `Events/` | 事件定义目录 | — | — | — |
| `Models/InternalEvent.cs` | 跨 Runtime/Platform 的事件信封与队列 DTO | `InternalEvent` | `Abstractions/IInternalEventBus.cs` | 目标补齐 aggregate/version/partition、producer、actor、classification 及 run/turn/call 身份 |
| `Abstractions/IInternalEventBus.cs` | 进程内 pub/sub 抽象 | `IInternalEventBus` | `Models/InternalEvent.cs` | 不作为长期 durable replay 或同步 Hook 合同 |
| `Observability/` | 观测抽象目录 | — | — | — |
| `Observability/LlmCallDiagnosticsScope.cs` | 单次 LLM 尝试的阶段、体积与独立计时；同一逻辑调用共享有界重试事实 | `LlmCallDiagnosticsScope` | `../PuddingRuntime/Services/DirectLlmClient.cs` | 不同调用不得共用尝试列表 |
| `Diagnostics/` | 诊断抽象目录 | — | — | — |

## 判定算子与分类契约（Operators/ · Classification/，2026-09-21）

S1a/S1b 已落地（`f577add` / `7cfc198`）。抽象在 PuddingCore，实现全在 PuddingRuntime（与 Classification 域同构），**需重启才生效**。

⚠️ **两条可执行边界**（不是口头约定）：① 契约层不得依赖任何具体供应商；② 契约层不得出现 `Rsi`/`Goal`/`ToolApproval` 等领域标识。二者由 `TestScripts/test-operators-architecture-gates.ps1` 强制，**已并入主门禁，违规即便主门禁非零**。

> 注：本次同步补登了此前一直缺失的 `PuddingCode.Classification` 契约（Note 该命名空间与目录名 `PuddingCore` 不一致，是真实命名而非笔误）。

| 文件 | 用途 |
|------|------|
| `Classification/ToolCallClassification.cs` | 既有工具审批分类契约（命名空间 `PuddingCode.Classification`） |
| `Operators/OperatorEnums.cs` | 判定枚举（`ConfidenceKind`/`JudgementTier`/`JudgeOutcome`） |
| `Operators/OperatorContextContracts.cs` | 算子上下文契约（`IOperatorContext` 等） |
| `Operators/ThresholdPolicy.cs` | 阈值外置为一等对象（S1a 判据核心） |
| `Operators/JudgementEnvelope.cs` | 统一判定信封（身份 + 结果 + 置信） |
| `Operators/OperatorResultProjections.cs` | 三投影结果（同一信封的不同视图） |
| `Operators/OperatorPorts.cs` | 三原语端口（`IScorer`/`IJudge`/`IClassifier`） |
| `Operators/ModelJudgementContracts.cs` | 模型判定请求/答案契约 |
| `Operators/OperatorRuntimeSeams.cs` | 运行时接缝（指令/输出/降级/旁挂） |
| `Operators/OperatorRegistry.cs` | 场景注册表契约 `IOperatorRegistry`（**键控**解析，取代此前的非键控单例缺口；缺失 ⇒ fail-closed 抛错，不静默放行） |

**关键语义**

**`Classification/ToolCallClassification.cs`**：既有工具审批分类契约（命名空间 `PuddingCode.Classification`）：`ClassificationOutcome`（`Unknown=0`，**仅允许末尾追加**）、`ClassificationVerdict`（`PerOutcomeConfidence` 用字典而非四个具名字段，注释明确理由「抽象层同时服务于非准入类决策」）、`ToolCallClassificationContext`（承载审批出题单；
**契约层不裁剪不校验长度，有界截断由调用方负责**）、`IToolCallClassifier`（**禁止向调用方冒泡异常**，不可用/取消均需按降级契约返回确定结论）、`ClassifierHealth`/`ClassifierStatus`/`IClassifierHealthReporter`
**`Operators/OperatorEnums.cs`**：`ConfidenceKind`（`ModelSelfReported` vs `Calibrated`：**未校准时禁止下游把它当概率做统计推断**）、`JudgementTier`、`JudgeOutcome`（**三值** `Yes/No/Abstain`；`Abstain` 为一等结果，**禁止折叠为 Yes/No**——折叠成 No 退化为「不敢判就拒绝」，折叠成 Yes 退化为「沉默放行」）
**`Operators/OperatorContextContracts.cs`**：`IOperatorContext`（`SceneKey` + `InputDigest`）、`OperatorIdentity`（自审批上下文提升的四元组，不改变其既有定义）、`JudgementEvidence`
**`Operators/ThresholdPolicy.cs`**：**阈值外置**为一等对象（S1a 判据核心）：`Apply` 为确定性三区间——`>= YesAtOrAbove ⇒ Yes`、`<= NoAtOrBelow ⇒ No`（**边界含等**）、中间开区间 ⇒ `Abstain`；`IsValid` 要求**严格大于**以保证 Abstain 带非空；非法配置**构造期即拒**（不静默产生全 Yes/全 No/全 Abstain）、NaN 显式拒绝。外置理由：使「为什么放行」可事后解释，且**自我改进候选无法悄悄移动判据**
**`Operators/JudgementEnvelope.cs`**：统一信封（收敛既有多个 verdict record 的公共核）：身份与可复现性（`JudgementId`/`InputDigest`/`OperatorId`/`ModelId`/`InstructionVersion`/`AppliedThreshold`/`SchemaVersion`）+ 结果（`Score`/`Outcome`/`PrimaryLabel`/`LabelDistribution`）+ 置信（**必带 `ConfidenceKind`**）+ 证据与成本 
+ 溯源
**`Operators/OperatorResultProjections.cs`**：三投影结果 `ScoreResult` / `JudgeResult` / `ClassificationResult`——**同一信封的不同视图，不是三套实现**
**`Operators/OperatorPorts.cs`**：三原语端口 `IScorer` / `IJudge` / `IClassifier` + `IClassifierModel`（**供应商隔离的唯一端口**）+ `IThresholdPolicyProvider`。**禁止一个类型实现多于一个原语端口**
**`Operators/ModelJudgementContracts.cs`**：`JudgementQuestion`/`ModelJudgementRequest`/`ModelAnswer`/`ModelJudgement`（支持**一次往返多问**，含 choice 与全选项概率）
**`Operators/OperatorRuntimeSeams.cs`**：运行时接缝：`OperatorInstruction`/`OperatorOutputShape`/`OperatorReasonCodes`/`OperatorDegradation`、健康观察者、**审计旁挂**、判定缓存、`OperatorScope`（含确定性 `BuildJudgementId`）

> **S2b 已接线（2026-09-21，实现落在 PuddingRuntime）**：本文件的两个旁挂端口 `IOperatorHealthObserver` / `IOperatorAuditSink` 此前**没有任何生产实现**（惰性抽象），现由 `PuddingRuntime/Operators/Adapters/` 下的两个适配器接上——健康 ⇒ 既有 `ClassifierHealthReporter`（计数键含**场景维度**），审计 ⇒ 既有审计存储（严守「**裁决先于留痕**」）。
> **本契约层未改动一行**（append-only 纪律：只允许追加，不得改既有字段），因此上述两行的字段清单保持有效。

## 改进落点契约（Improvement/ · L3-a，2026-09-21）

L3-a 已落地（纯类型 + 守卫测试，**零行为**：不写技能、不写记忆、不调任何服务）。设计依据见
`Docs/12_features/RSI-Harness-分类器抽象与可递归进化服务设计-2026-09-21.md` §15.4 / §15.6。

**立场**：改进的输出货币是**变更**，不是新增物——于是「更新 / 合并 / 取代 / 淘汰既有资产」与「新增」
在同一份契约里并列，且**新增必须自证为什么不能更新或合并**。

⚠️ **已知缺口**：本目录**不在** `TestScripts/test-operators-architecture-gates.ps1` 的扫描范围内
（该门禁只扫 `Operators/**`）。两条边界（不得依赖具体供应商 / 不得出现领域标识）在 L3 同样成立，
但目前**没有机械守卫**——待 L3-b 一并补上。

| 文件 | 用途 |
|------|------|
| `Improvement/ImprovementEnums.cs` | 改进枚举（`ArtifactKind`/`ImprovementOperation`/`ChangeDecision`） |
| `Improvement/ArtifactRef.cs` | 资产句柄（统一寻址 + 幂等键） |
| `Improvement/ImprovementProposal.cs` | 改进提案（统一货币 + 字段级守卫） |
| `Improvement/ChangeVerdict.cs` | 变更裁决（唯一可驱动落点动作的凭据） |

**关键语义**

**`Improvement/ImprovementEnums.cs`**：`ArtifactKind`（资产种类 `Skill`/`MemoryChapter`/`Rule`/`Doc`/`CodeFile`；`Unknown=0` 为非法哨兵，**不得当作「默认落到某通道」**）、`ImprovementOperation`（`Create`/`Update`/`Merge`/`Replace`/`Retire`；`Unknown=0`）、`ChangeDecision`（`ApplyShadow`/`Apply`/`Reject`/`Defer`；
`Unknown=0` **不得当作放行**；`ApplyShadow` 为默认）。三者均**只允许末尾追加**（序列化兼容）
**`Improvement/ArtifactRef.cs`**：资产句柄（统一寻址）：`Kind`+`Id`+`Version?`+`EvidenceRefs`；`Handle` 产出规范化串 `kind:id@version` 供幂等键与对账。守卫：`Kind=Unknown` 拒、空 id 拒、**空白版本归一为 null**（防 `kind:id@` 假句柄）；`Equals` **显式按内容比较证据列表**（record 自动相等对集合成员用引用相等，会让去重与幂等键静默失效）
**`Improvement/ImprovementProposal.cs`**：改进提案（统一货币）：`Operation`+`Target?`+`ProposedBy`+`IdempotencyKey`+`EvidenceRefs`+`WhyNotUpdateOrMerge`+`Payload`+`ExpectedGain`+`Rationale`。⭐ **`Create` 必带 `WhyNotUpdateOrMerge`**（把「add 不是默认」做成**字段级强制**）；非 Create 必带 `Target` 且**禁**带该字段；
`Update`/`Merge`/`Replace` 必带 `Payload`；`Retire` 必**无** `Payload`；证据非空（无证据的变更不进裁决）；`ExpectedGain` 标注为**未标定刻度**，不得当阈值输入，且**无数据 ≠ 低价值**（缺省 null，不用 0 填充）
**`Improvement/ChangeVerdict.cs`**：变更裁决（**唯一**可驱动落点动作的凭据）：`Decision`+`PolicyId`+`PolicyVersion`+`ReasonCode`+`RollbackHandle?`。守卫：`Apply` **必带** `RollbackHandle`（不可回滚的自动变更不允许存在，对应 C4）；**非 `Apply` 禁带**（防「看起来能回滚、实际无动作」的假象）；`Unknown` 决策拒；`PolicyVersion >= 1`

**契约测试**：`../PuddingCoreTests/Improvement/ImprovementContractTests.cs` —— **29/29 ✅**（2026-09-21）。

## 飞书消息路由契约

| 文件 | 用途 |
|------|------|
| `Platform/FeishuRecipientContracts.cs` | 收件人目录端口、无凭据 DTO 与稳定连接器身份 |
| `Platform/GatewayCommandRouteContracts.cs` | 仅按当前 CommandId 解析可信回复路由 |
| `Platform/MessageGatewayContracts.cs` | 区分入站会话与主动 ReceiveId/ReceiveIdType 的消息元数据 |

## 运行时抽象


- `Runtime/ITurnExecutor.cs`：`TurnExecutionContext` 除 Agent 预算外携带 canonical TaskPlan/TaskNode/ParentNode identity，供 Platform→Runtime 交接；`TurnTerminalInfo.Diagnostic` 经 `Platform/ExecutionRunContracts.cs` 的 `TurnTerminal.Diagnostic` 保留脱敏错误报告。

| 目录/文件 | 用途 |
|------|------|
| `Runtime/` | 运行时抽象 |
| `Runtime/RuntimeControlService.cs` | Session 运行控制与滑动窗口熔断；精确调用去重之外，按 kind+component+归一化错误的同失败族 5 次止损，总量队列容量与配置阈值一致 |
| `Services/` | 服务接口 |
| `Swarm/` | Swarm 协作抽象 |

## 测试

`../PuddingCoreTests/` — 工具契约、LLM 网关、消息围栏、MessageFabric；2026-08-11 Orchestration 定向测试 81/81 ✅
