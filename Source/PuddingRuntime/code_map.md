# PuddingRuntime CodeMAP

> 源指纹: Source/PuddingRuntime/**=dc2ba84b0ee2 · 条目数: 350 · 最近整理: 2026-10-07
> 定位：Agent 运行时核心（Agent Loop / LLM 调用 / 工具系统 / 上下文管线 / 子代理 / 后台学习）；.NET 10 类库，唯一服务注册入口 `DependencyInjection.cs`。
> 本文件只做本项目文件级索引；跨项目调用链、测试工程索引、架构文档索引、运行时目录与构建入口只在根 [`code_map.md`](../../code_map.md)。
> 每对象一行；空字段写 `—`。角色 `observe` 的对象只在文末附录列路径。

## 入口与配置

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `DependencyInjection.cs` | Runtime 全部服务的注册入口 | `RuntimeServiceExtensions` | `Tools/Platform/PuddingToolServiceCollectionExtensions.cs` | 阈值策略对象须由版本化实现提供，组合根不得凭空编造 |
| `PuddingRuntime.csproj` | 项目文件：目标框架与包引用 | — | — | — |
| `Properties/launchSettings.json` | 本机开发启动档案 | — | — | — |
| `IdleDetector.cs` | 运行时最近活动时间的空闲判定 | `IIdleDetector` | `Services/Background/SubconsciousJobScheduler.cs` | — |
| `Models/GoalQueueState.cs` | Goal 队列的持久化状态模型 | `GoalQueueState` | `Services/GoalMode/GoalModeService.cs` | — |
| `Models/HeartbeatPreference.cs` | 心跳频率的持久化偏好模型 | `HeartbeatPreference` | `Tools/BuiltIns/Agents/AgentSleepTool.cs` | — |
| `Models/MemoryCropModels.cs` | 记忆裁剪前后的原始数据包模型 | `RawContentBundle` | `Services/CroppedLayersProvider.cs` | — |
| `Models/SessionRuntimeRecord.cs` | 会话运行时记录模型 | `SessionRuntimeRecord` | `Services/InMemoryRuntimeSessionStore.cs` | — |
| `Controllers/NativeCapabilityController.cs` | 原生能力执行 API 的入口 | `NativeCapabilityController` | `Services/NativeCapabilityExecutor.cs` | 权限与审批校验完成后才转发执行 |
| `Controllers/PluginCatalogController.cs` | 插件目录的只读 API | `PluginCatalogController` | `Services/Plugins/PluginManifestCatalog.cs` | — |
| `Controllers/RuntimeExecuteController.cs` | Runtime 执行 API 的请求入口 | `RuntimeExecuteController` | `Services/TurnExecutorAdapter.cs` | — |
| `Controllers/RuntimeSessionController.cs` | Runtime 会话 API 的入口 | `RuntimeSessionController` | `Services/AgentSessionManager.cs` | — |
| `Services/PuddingConfigLoader.cs` | JSON 配置文件的加载器 | `PuddingConfigLoader` | `Services/PuddingJsonConfig.cs` | — |
| `Services/PuddingJsonConfig.cs` | 服务商与工作区等的配置模型定义 | `PuddingJsonConfig` | `Services/PuddingConfigLoader.cs` | — |
| `Services/RuntimeExecutionConfigService.cs` | 运行时执行配置的加载与规范化 | `IRuntimeExecutionConfigService` | `Services/AgentLoop/AgentExecutionGuardrails.cs` | 请求级轮次与预算覆盖以调用参数为准，配置文件只给默认 |
| `Services/DefaultExecutionEnvironmentProvider.cs` | 执行环境信息的默认提供者 | `IExecutionEnvironmentProvider` | `Services/SandboxExecutor.cs` | — |
| `Services/StartupEnvironmentInfo.cs` | 启动环境信息的一次性采集与注入 | `StartupEnvironmentInfo` | `Services/SystemPromptBuilder.cs` | — |

## 安全分类器 · 判定算子 · 阈值判据

> 抽象在 `PuddingCore`，实现全在本工程；消费方只依赖抽象端口。准入链路的规则与闸门见 `Services/AgentFirewall.cs`。

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `Classification/AgentFullAccessGrantService.cs` | 临时「完全访问模式」授予的签发与失效判定 | `IAgentFullAccessGrantService` | `Services/AgentFirewall.cs` | 只放宽授权与审批闸门；不放宽 Yolo、沙箱、工作区与资源边界 |
| `Classification/ClassificationRuleCurator.cs` | 白名单规则的落库策展与同键冲突裁决 | `ClassificationRuleCurator` | `Tools/Approval/ToolApprovalPortalService.cs` | 五元组规则键禁止通配；同键相反 Effect 时 deny 胜 |
| `Classification/ClassifierArbiterRegistrationState.cs` | 仲裁位注册状态的进程内记录 | `ClassifierArbiterRegistrationState` | `Tools/Approval/ClassifierStatusTool.cs` | — |
| `Classification/ClassifierHealthReporter.cs` | 分类器连续 deferred 计数的健康分档与退避 | `IClassifierHealthReporter` | `Classification/OperatorSceneKeys.cs` | 成功返回即清零该分类器全部键 |
| `Classification/OperatorSceneKeys.cs` | 健康计数场景键的取值与归一 | `OperatorSceneKeys.Normalize` | `Classification/ClassifierHealthReporter.cs` | 默认键赋具名常量，绝不使用空串 |
| `Classification/SystemRuleClassifier.cs` | 零网络的规则快路径分类器 | `SystemRuleClassifier` | `Classification/ClassificationRuleCurator.cs` | 未命中一律 `Unknown`，绝不默认放行 |
| `Classification/ToolCallClassifierPipeline.cs` | 规则与仲裁的求值序编排 | `ToolCallClassifierPipeline` | `Classification/SystemRuleClassifier.cs` | 永久类须达逐分类可信度门槛；仲裁不可用不得折叠为 Deny |
| `Operators/OperatorBase.cs` | 算子的横切基类：超时、异常兜底与审计旁挂 | `OperatorBase<TCtx,TResult>` | `Operators/OperatorEnvironment.cs` | 审计写入失败不改变已定裁决 |
| `Operators/ProjectionBases.cs` | 打分、判定与分类三类投影基类 | `ScorerBase<TCtx>` | `Operators/OperatorBase.cs` | 非虚，不引入新抽象成员 |
| `Operators/OperatorEnvironment.cs` | 算子运行环境的端口集合与判定缓存 | `OperatorEnvironment` | `Operators/OperatorBase.cs` | — |
| `Operators/OperatorRegistry.cs` | 场景算子的注册表与注册期守卫 | `IOperatorRegistry` | `Operators/OperatorEnvironment.cs` | 同场景重复注册或一型多端口一律拒绝 |
| `Operators/Adapters/ToolApprovalOperatorAdapter.cs` | 既有审批分类器到算子端口的适配 | `ToolApprovalOperatorAdapter` | `Tools/Approval/ClassifierToolApprovalReviewer.cs` | 被包装者行为逐位不变；无分数就留空，不伪造 |
| `Operators/Adapters/ToolApprovalOperatorContext.cs` | 审批输入的算子上下文包装 | `ToolApprovalOperatorContext` | `Tools/Approval/ToolApprovalPromptBuilder.cs` | `InputDigest` 只在单一位置计算，否则缓存键失效 |
| `Operators/Adapters/OperatorHealthObserverAdapter.cs` | 算子健康接缝到既有健康面的生产接线 | `OperatorHealthObserverAdapter` | `Classification/ClassifierHealthReporter.cs` | 吞掉自身异常但记 Warning 并可探查 |
| `Operators/Adapters/OperatorAuditSinkAdapter.cs` | 算子审计接缝到既有审计存储的生产接线 | `OperatorAuditSinkAdapter` | `Tools/Platform/AuditLogger.cs` | 存储未接线时丢弃但只记一次 Warning |
| `Thresholds/AcceptanceThresholdPolicyCatalog.cs` | 三个逐标签验收门槛的 id 与默认值定义 | `AcceptanceThresholdPolicyIds` | `Thresholds/DefaultAcceptanceThresholdPolicyProvider.cs` | id 留在运行时层，契约层不得出现供应商名词 |
| `Thresholds/DefaultAcceptanceThresholdPolicyProvider.cs` | 判据解析端口：配置缺失时回落到内置默认 | `IAcceptanceThresholdPolicyProvider` | `Thresholds/AcceptanceThresholdPolicyCatalog.cs` | 未知 id 抛错，不得静默返回默认值 |

## Agent Loop · 上下文管线

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `Services/AgentCompactionNotifier.cs` | 压缩生命周期通知的对外发布 | `AgentCompactionNotifier` | `Services/ContextCompactionService.cs` | — |
| `Services/AgentContextCompactionSummaryGenerator.cs` | 由当前运行时 Agent 生成压缩摘要 | `IContextCompactionSummaryGenerator` | `Services/FlashContextCompactionSummaryGenerator.cs` | — |
| `Services/AgentExecution/AgentExecutionLlmInvoker.cs` | LLM 调用的统一封装（facade 与 legacy 两条路径） | `AgentExecutionLlmInvoker` | `Services/LlmInvocationService.cs` | — |
| `Services/AgentExecution/AgentExecutionResponseHandler.cs` | LLM 输出解析为 Loop 响应并评估终态 | `AgentExecutionResponseHandler` | `Services/AgentLoop/CompletionPolicy.cs` | — |
| `Services/AgentExecution/AgentExecutionService.Buffered.cs` | 非流式主循环（partial） | `AgentExecutionService` | `Services/AgentExecution/AgentExecutionService.Streaming.cs` | 工具目录在 dispatch 时冻结，新发现定义只在下一次 LLM round 生效 |
| `Services/AgentExecution/AgentExecutionService.Streaming.cs` | SSE 流式主循环（partial） | `AgentExecutionService` | `Services/AgentExecution/AgentExecutionService.Buffered.cs` | 与 Buffered 共用冻结前缀与止损语义 |
| `Services/AgentExecution/AgentToolArguments.cs` | tool-call JSON 到参数的纯转换 | `AgentToolArguments` | `Tools/BuiltIns/Skills/AgentSkillTool.cs` | 纯函数，不读写状态 |
| `Services/AgentExecution/AgentTurnTimingCollector.cs` | 单轮耗时检查点的采集 | `AgentTurnTimingCollector` | `Tools/BuiltIns/Diagnostics/AgentDiagnosticsTool.cs` | — |
| `Services/AgentExecution/ExecutionUsageBudgetTracker.cs` | 单次 dispatch 的 Token 与成本账本 | `ExecutionUsageBudgetTracker` | `Services/SubAgentInvocationService.cs` | 剩余值诚实归零并带派生标记，零值轴表示父级已耗尽 |
| `Services/AgentExecution/FailedToolCallTracker.cs` | 同一工具与参数的不变失败检测 | `FailedToolCallTracker` | `Services/AgentLoop/ExecutionJournal.cs` | 指纹按 canonical 工具与参数计算 |
| `Services/AgentExecution/NoOpKeyVaultService.cs` | 无密钥库宿主的降级实现 | `NoOpKeyVaultService` | `Services/JevDecisionOptionsProvider.cs` | — |
| `Services/AgentExecution/StreamPipelineDiagnosticsAccumulator.cs` | 流式热路径诊断量的线程安全聚合 | `StreamPipelineDiagnosticsAccumulator` | `Tools/BuiltIns/Diagnostics/AgentDiagnosticsTool.cs` | — |
| `Services/AgentExecution/ToolDiscoveryLoopTracker.cs` | 只发现不执行的循环检测 | `ToolDiscoveryLoopTracker` | `Tools/BuiltIns/Search/SearchToolsTool.cs` | 不同查询文本归入同一进展族 |
| `Services/AgentExecution/ToolResultContextPolicy.cs` | 工具结果进入模型历史前的体积边界 | `ToolResultContextPolicy` | `Tools/Platform/ToolInvocationService.cs` | 边界不可协商；存储失败也必须返回有界预览 |
| `Services/AgentExecutionService.cs` | 执行编排入口与 session 单写者 | `AgentExecutionService` | `Services/AgentExecution/AgentExecutionService.Buffered.cs` | 本轮输入须以 `CURRENT USER TURN/input_sha256` 围栏，缺失即 fail-closed |
| `Services/AgentFirewall.cs` | 八道闸门的顺序准入 | `AgentFirewall` | `Classification/AgentFullAccessGrantService.cs` | 完全访问授予只放宽授权与审批闸门 |
| `Services/AgentInvocationDispatchFactory.cs` | 由消息元数据构造执行请求 | `IAgentInvocationDispatchFactory` | `Services/RuntimeAgentDispatcher.cs` | 父身份解析失败即报错，不伪造会话 id |
| `Services/AgentLoop/AgentExecutionGuardrails.cs` | Agent Loop 的执行预算与护栏配置 | `AgentExecutionGuardrails` | `Services/RuntimeExecutionConfigService.cs` | — |
| `Services/AgentLoop/AgentExecutionOutcomePolicy.cs` | 名义成功转降级判定的启发式 | `AgentExecutionOutcomePolicy` | `Services/AgentLoop/CompletionPolicy.cs` | — |
| `Services/AgentLoop/AgentLoopResponse.cs` | Agent Loop 单轮响应的模型 | `AgentLoopResponse` | `Services/AgentExecution/AgentExecutionResponseHandler.cs` | — |
| `Services/AgentLoop/AgentOutputTruncationPolicy.cs` | provider 截断输出的有界恢复策略 | `AgentOutputTruncationPolicy` | `Services/AgentExecution/AgentExecutionService.Streaming.cs` | 只允许一次立即行动恢复，再次截断即显式失败 |
| `Services/AgentLoop/CanonicalWorkReport.cs` | 五段式工作报告的解析与校验 | `CanonicalWorkReport` | `Tools/BuiltIns/Agents/SubAgentTool.cs` | 显式 CONTINUE 不得被同轮 DONE 提升覆盖 |
| `Services/AgentLoop/CompletionPolicy.cs` | 完成判定的裁定枚举 | `CompletionVerdict` | `Services/AgentLoop/CanonicalWorkReport.cs` | — |
| `Services/AgentLoop/ExecutionControlRegistry.cs` | 每会话的取消与冻结标志注册表 | `ExecutionControlRegistry` | `Services/SessionExecutionGate.cs` | — |
| `Services/AgentLoop/ExecutionJournal.cs` | 执行轮次的流水记录模型 | `TurnRecord` | `Services/AgentExecution/FailedToolCallTracker.cs` | — |
| `Services/AgentLoop/IAgentLoopHook.cs` | Loop 生命周期的钩子契约 | `IAgentLoopHook` | `Services/AgentLoop/LoggingAgentLoopHook.cs` | — |
| `Services/AgentLoop/LoggingAgentLoopHook.cs` | Loop 各阶段事件写入结构化日志 | `LoggingAgentLoopHook` | `Services/AgentLoop/IAgentLoopHook.cs` | — |
| `Services/AgentLoop/SubAgentBudgetLifecycle.cs` | 子代理预算窗口的状态机 | `SubAgentBudgetLifecycle` | `Services/SubAgentInvocationService.cs` | 通知档位与收尾宽限不可跳过 |
| `Services/AgentMemorySummaryContextBuilder.cs` | 由会话摘要文件构造历史上下文层 | `AgentMemorySummaryContextBuilder` | `Services/ContextPipelineOrchestrator.cs` | — |
| `Services/AgentSessionManager.cs` | 活跃 Agent 实例的会话管理 | `AgentSessionManager` | `Services/AgentExecutionService.cs` | — |
| `Services/AgentWakeQueue.cs` | 心跳唤醒队列的到期判定与出队 | `AgentWakeQueue` | `Tools/BuiltIns/Agents/AgentSleepTool.cs` | 出队须扫描全部已到期项，不得只看队首 |
| `Services/Background/SubconsciousConsolidationHook.cs` | 主对话完成后投递后台整合任务 | `SubconsciousConsolidationHook` | `Services/Background/SubconsciousWorkerService.cs` | — |
| `Services/Background/SubconsciousDiagnosticLog.cs` | 潜意识管道诊断日志的选项与写入 | `SubconsciousDiagnosticLogOptions` | `Tools/BuiltIns/Management/SubconsciousTriggerTool.cs` | — |
| `Services/Background/SubconsciousJobScheduler.cs` | 潜意识作业的可租约窗口决策 | `SubconsciousJobScheduler` | `IdleDetector.cs` | — |
| `Services/Background/SubconsciousRuntimeControlService.cs` | 潜意识管道的运行时开关 | `ISubconsciousRuntimeControl` | `Tools/BuiltIns/Management/SubconsciousTriggerTool.cs` | — |
| `Services/Background/SubconsciousWorkerService.cs` | 持久潜意识作业的消费循环 | `SubconsciousWorkerService` | `Services/SubconsciousPlanGenerationService.cs` | — |
| `Services/CanonicalChatTranscriptSynchronizer.cs` | 平台转录到记忆库的增量镜像 | `CanonicalChatTranscriptSynchronizer` | `../PuddingMemoryEngine/` | 高水位持久化加分页幂等，可重复执行 |
| `Services/CompactionCoordinator.cs` | 会话压缩的单飞锁与冷却限流 | `CompactionCoordinator` | `Services/ContextCompactionService.cs` | — |
| `Services/CompactionCoverageFilter.cs` | 压缩覆盖集合的加载与片段过滤 | `CompactionCoverageFilter` | `Services/ContextCompactionService.cs` | 清单缺失或非法时按无覆盖处理 |
| `Services/CompositeContextCompactionSummaryGenerator.cs` | 组合式压缩摘要生成器 | `CompositeContextCompactionSummaryGenerator` | `Services/AgentContextCompactionSummaryGenerator.cs` | — |
| `Services/CompositionRecoveryService.cs` | 由持久化 Composition 水合工具集合 | `CompositionRecoveryService` | `Services/PersistentCompositionVersionRegistry.cs` | 恢复失败须显式报出，不得静默降级为空集合 |
| `Services/CompositionSnapshot.cs` | 逐请求的提示词与工具投影哈希归因 | `CompositionSnapshot` | `Services/SqliteCompositionStore.cs` | — |
| `Services/ContextAssemblyService.cs` | 上下文合成的稳定对外契约 | `IContextAssemblyService` | `Services/ContextPipeline.cs` | — |
| `Services/ContextBudgetAllocator.cs` | 上下文预算的算术与压缩档位 | `ContextBudgetAllocator` | `Services/ContextWindowManager.cs` | — |
| `Services/ContextCompactionOptions.cs` | 上下文压缩的配置选项 | `ContextCompactionOptions` | `Services/ContextCompactionService.cs` | — |
| `Services/ContextCompactionService.cs` | 上下文压缩执行与覆盖清单写入 | `IContextCompactionService` | `Services/CurrentTurnCompactionGuard.cs` | 围栏 Turn 落入候选时须在任何写入前 fail-closed |
| `Services/ContextCompactionService.TokenEstimation.cs` | 压缩服务的 Token 估算（partial） | `ContextCompactionService` | `Services/ContextCompactionService.cs` | — |
| `Services/ContextCompactionStrategy.cs` | 压缩策略的选择与分级填充 | `ContextCompactionStrategy` | `Services/ContextWindowManager.cs` | — |
| `Services/ContextHealthEvaluator.cs` | 上下文健康度的评估 | `ContextHealthEvaluator` | `Tools/BuiltIns/Diagnostics/AgentDiagnosticsTool.cs` | — |
| `Services/ContextLayerContracts.cs` | 上下文分层的契约模型 | `ContextLayerContracts` | `Services/ContextPipelineLayers.cs` | — |
| `Services/ContextPipeline.cs` | 分层上下文组装的对外实现 | `ContextPipeline` | `Services/ContextPipelineLayers.cs` | 稳定 system prefix 与本轮 User tail 必须分离 |
| `Services/ContextPipelineLayers.cs` | 上下文各层的装配 | `ContextPipelineLayers` | `Services/ContextPipelineOrchestrator.cs` | — |
| `Services/ContextPipelineOrchestrator.cs` | 上下文组装编排与分层占比快照 | `ContextPipelineOrchestrator` | `Services/ContextPipelineLayers.cs` | — |
| `Services/ContextWindowConstants.cs` | 上下文窗口的命名常量 | `ContextWindowConstants` | `Services/ContextWindowManager.cs` | — |
| `Services/ContextWindowManager.cs` | 上下文窗口与会话历史的裁剪 | `ContextWindowManager` | `Services/ContextCompactionService.cs` | 压缩三态事件必须携带同一 compactionId |
| `Services/ControllerRoutedLlmClient.cs` | 经 Controller 中转的 LLM 客户端 | `ControllerRoutedLlmClient` | `Services/LlmInvocationService.cs` | — |
| `Services/CroppedLayersProvider.cs` | 用 Flash 模型裁剪记忆原始层 | `CroppedLayersProvider` | `Models/MemoryCropModels.cs` | — |
| `Services/CurrentTurnCompactionGuard.cs` | 当前轮是否落入压缩候选的守卫 | `CurrentTurnCompactionGuard` | `Services/ContextCompactionService.cs` | — |

## LLM 调用 · 多模态 · 会话事件 · 记忆写入

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `Services/DashScopeAsrProvider.cs` | 百炼 DashScope 语音识别 Provider | `IAsrHttpRecognizer` | `Services/VoiceProviderFactory.cs` | — |
| `Services/DashScopeTtsProvider.cs` | 百炼 DashScope 语音合成 Provider | `ITtsProvider` | `Services/VoiceProviderFactory.cs` | — |
| `Services/Demo/DemoDesktopHostBridge.cs` | 演示用的嵌入式宿主桥接实现 | `DemoDesktopHostBridge` | `Services/INativeHostBridge.cs` | — |
| `Services/DesignCouncilRuntimeService.cs` | MOA 设计委员会的运行时适配与状态持久化 | `IDesignCouncilRuntimeService` | `Services/InMemorySubAgentOrchestrationRunStore.cs` | 派发为只读，结果由调用方回填 |
| `Services/Diagnostics/RuntimeDiagnosisEngine.cs` | 四维运行时指标的确定性诊断 | `RuntimeDiagnosisEngine` | `Tools/BuiltIns/Diagnostics/AgentDiagnosticsTool.cs` | 无证据判 unknown，绝不判 healthy |
| `Services/DirectLlmClient.cs` | 按选中模型协议直连 LLM 的客户端 | `DirectLlmClient` | `Services/FrozenVisionContextAccessor.cs` | 视觉能力只读冻结快照，无快照即 fail-closed |
| `Services/DirectMemoryLlmClient.cs` | 面向记忆的独立配置 LLM 客户端 | `IMemoryLlmClient` | `Services/LlmInvocationService.cs` | — |
| `Services/EmbeddingGenerationHook.cs` | 在 Loop 节点为章节生成嵌入向量 | `EmbeddingGenerationHook` | `Services/OpenAiEmbeddingService.cs` | — |
| `Services/Events/EventDispatcher.cs` | 持久事件队列的出队与分发 | `EventDispatcher` | `Services/Events/InternalEventBus.cs` | — |
| `Services/Events/EventPreprocessor.cs` | 事件的去重与批处理窗口 | `IEventPreprocessor` | `Services/Events/EventDispatcher.cs` | — |
| `Services/Events/InternalEventBus.cs` | 进程内即时事件总线 | `IInternalEventBus` | `Services/Events/EventDispatcher.cs` | 只承载非关键在线通知 |
| `Services/FlashContextCompactionSummaryGenerator.cs` | 用 Flash 模型生成压缩结构化摘要 | `FlashContextCompactionSummaryGenerator` | `Services/CompositeContextCompactionSummaryGenerator.cs` | — |
| `Services/FrozenVisionContextAccessor.cs` | 视觉路由快照的调用链冻结通道 | `FrozenVisionContextAccessor` | `Services/DirectLlmClient.cs` | 无上下文即 null，fail-closed |
| `Services/GoalMode/GoalModeOptions.cs` | Goal 模式的配置选项 | `GoalModeOptions` | `Services/GoalMode/GoalModeService.cs` | 默认关闭，不改变既有 Agent 行为 |
| `Services/GoalMode/GoalModeService.cs` | 连续自主目标循环的实现 | `IGoalModeService` | `Services/TaskTools/GoalResumeTool.cs` | — |
| `Services/GoalMode/IGoalModeService.cs` | Goal 模式的服务契约 | `IGoalModeService` | `Services/GoalMode/GoalModeService.cs` | — |
| `Services/HeartbeatService.cs` | 超时会话的资源清理扫描 | `HeartbeatService` | `Services/AgentSessionManager.cs` | 只管会话超时清理，不是 Agent 自主心跳编排 |
| `Services/HistoryPrefixReconciler.cs` | 规范化历史与 canonical 转录的对齐 | `HistoryPrefixReconciler` | `Services/AgentExecutionService.cs` | — |
| `Services/Hooks/HookPublisher.cs` | 生命周期事件到内部事件总线的薄适配 | `IHookPublisher` | `Services/Events/InternalEventBus.cs` | — |
| `Services/Hooks/SessionCompressedMemoryMaintenanceHook.cs` | 会话压缩事件到持久维护作业的桥接 | `SessionCompressedMemoryMaintenanceHook` | `Services/Background/SubconsciousWorkerService.cs` | — |
| `Services/Improvement/Rsi/IRsiTrajectorySource.cs` | 轨迹取数的身份上下文契约 | `RsiScope` | `Services/Improvement/Rsi/RsiTrajectorySource.cs` | 身份由调用方给出，本层不推导 |
| `Services/Improvement/Rsi/RsiToolOutcome.cs` | 工具调用结局的三态枚举 | `RsiToolOutcome` | `Services/Improvement/Rsi/RsiToolOutcomeDeriver.cs` | Unknown 占零值，缺省落在未知而非成功 |
| `Services/Improvement/Rsi/RsiToolOutcomeDeriver.cs` | 由退出码与错误字段推导工具结局 | `RsiToolOutcomeDeriver` | `Services/Improvement/Rsi/RsiToolOutcome.cs` | 两者皆缺一律 Unknown，不抛异常 |
| `Services/Improvement/Rsi/RsiTrajectoryAssembler.cs` | 事件行装配为带结局标注的轨迹 | `RsiTrajectoryAssembler` | `Services/Improvement/Rsi/RsiTypes.cs` | 失败步不丢弃；配对前须按序号稳定排序 |
| `Services/Improvement/Rsi/RsiTrajectorySource.cs` | 会话级轨迹源实现 | `IRsiTrajectorySource` | `Services/Improvement/Rsi/RsiTrajectoryAssembler.cs` | 只编排，不重写 |
| `Services/Improvement/Rsi/RsiTypes.cs` | 轨迹的步骤与事件行模型 | `RsiToolStep` | `Services/Improvement/Rsi/RsiTrajectoryAssembler.cs` | — |
| `Services/Improvement/SkillValue/SkillValueOperatorContext.cs` | 技能价值打分的场景输入 | `SkillValueOperatorContext` | `Services/Improvement/SkillValue/SkillValueScorer.cs` | 只承载真实存在的事实，不用零值占位 |
| `Services/Improvement/SkillValue/SkillValueScene.cs` | 技能价值场景的常量与刻度定义 | `SkillValueScene` | `Services/Improvement/SkillValue/SkillValueScorer.cs` | — |
| `Services/Improvement/SkillValue/SkillValueScorer.cs` | 技能价值的纯确定性打分器 | `SkillValueScorer` | `Services/Improvement/SkillValue/SkillValueScene.cs` | 无观测数据走不足档，不等于低分；阈值必须外置 |
| `Services/INativeHostBridge.cs` | 宿主原生能力桥接契约 | `INativeHostBridge` | `Controllers/NativeCapabilityController.cs` | — |
| `Services/InMemoryRuntimeSessionStore.cs` | 会话运行时记录的进程内存储 | `InMemoryRuntimeSessionStore` | `Models/SessionRuntimeRecord.cs` | — |
| `Services/InMemorySubAgentOrchestrationRunStore.cs` | MOA run 快照的进程内存储 | `ISubAgentOrchestrationRunStore` | `Services/DesignCouncilRuntimeService.cs` | 版本 CAS 防重复领取，不做跨重启恢复 |
| `Services/IRuntimeLlmClient.cs` | LLM 客户端抽象 | `IRuntimeLlmClient` | `Services/DirectLlmClient.cs` | — |
| `Services/JevDecisionOptionsProvider.cs` | Jev 连接参数的解析（资源池优先） | `IJevDecisionOptionsProvider` | `Services/JevDecisionService.cs` | 未配置即报错，绝不返回空端点；密钥不入日志 |
| `Services/JevDecisionService.cs` | Jev 决策模型的 HTTP 适配器 | `JevDecisionService` | `Tools/Approval/JevToolCallClassifier.cs` | 返回结构化决策数据；非 2xx 一律 fail-closed |
| `Services/KnowledgeAccessRuntime.cs` | Runtime 侧知识基础设施的访问桥接 | `KnowledgeAccessRuntime` | `Tools/BuiltIns/Memory/MemoryLibraryTool.cs` | — |
| `Services/LlmInvocationPurposeAccessor.cs` | 单次 LLM 调用的计费归因作用域 | `LlmInvocationPurposeAccessor` | `Services/DirectLlmClient.cs` | 嵌套调用完成后必须恢复外层作用域 |
| `Services/LlmInvocationService.cs` | LLM 调用的稳定对外门面 | `ILlmInvocationService` | `Services/IRuntimeLlmClient.cs` | — |
| `Services/LlmProfileResolver.cs` | provider 与 profile 到完整配置的解析 | `ILlmProfileResolver` | `Services/PuddingJsonConfig.cs` | — |
| `Services/LlmRequestBudgetGuard.cs` | 请求级预算守卫与软压缩触发 | `LlmRequestBudgetGuard` | `Services/ContextCompactionService.cs` | — |
| `Services/ManagedOggOpusTranscoder.cs` | 频道短音频的纯托管转码 | `IAudioTranscoder` | `Services/DashScopeTtsProvider.cs` | — |
| `Services/MemoryLlmInvocationClient.cs` | 面向记忆的共享门面 LLM 客户端 | `IMemoryLlmClient` | `Services/LlmInvocationService.cs` | — |
| `Services/MemoryMaintenancePlanWriteCommandMapper.cs` | 记忆维护方案到写命令的映射 | `MemoryMaintenancePlanWriteCommandMapper` | `Services/SubconsciousPlanGenerationService.cs` | — |
| `Services/MemoryQualityFilter.cs` | 记忆质量检查与拒收判定 | `MemoryQualityResult` | `Tools/BuiltIns/Memory/SaveMemoryTool.cs` | — |
| `Services/MemorySnippetRelevanceCalculator.cs` | 记忆线索关联度的计算 | `MemorySnippetRelevanceCalculator` | `Services/CroppedLayersProvider.cs` | — |
| `Services/MemoryWikiPageUpdateService.cs` | 记忆 Wiki 页面的更新 | `MemoryWikiPageUpdateService` | `Services/WikiPageWriteEntry.cs` | — |
| `Services/MemoryWriteCoordinator.cs` | 记忆写入的协调 | `MemoryWriteCoordinator` | `Tools/BuiltIns/Memory/SaveMemoryTool.cs` | — |
| `Services/MessageLogStripper.cs` | 消息日志中的隐私内容剥离 | `MessageLogStripper` | `Services/Messaging/MessageDeliveryDispatcher.cs` | — |
| `Services/Messaging/AgentExecutionAdmissionCoordinator.cs` | 前后台投递的准入与抢占协调 | `AgentExecutionAdmissionCoordinator` | `Services/Messaging/AgentExecutionStateRegistry.cs` | 前台需求到达时抢占活动后台投递 |
| `Services/Messaging/AgentExecutionStateRegistry.cs` | Agent 执行状态的进程内注册表 | `IAgentExecutionStateRegistry` | `Services/Messaging/DefaultAgentExecutionAvailabilityProvider.cs` | — |
| `Services/Messaging/DefaultAgentExecutionAvailabilityProvider.cs` | 由执行状态注册表派生可用性 | `IAgentExecutionAvailabilityProvider` | `Services/Messaging/AgentExecutionStateRegistry.cs` | 投影缺失报 unknown，不由队列缺席推导空闲 |
| `Services/Messaging/MessageDeliveryDispatcher.cs` | 持久消息投递的领取与受理 | `MessageDeliveryDispatcher` | `Services/Messaging/AgentExecutionAdmissionCoordinator.cs` | 会话身份解析失败即重试或死信，绝不回落到主会话 |

## 子代理 · 编排 · 任务 · 会话存储 · 技能

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `Services/NativeCapabilityExecutor.cs` | 宿主原生能力执行前的本地守门 | `NativeCapabilityExecutor` | `Services/SandboxExecutor.cs` | — |
| `Services/NoOpTerminalProcessManager.cs` | 终端进程管理器的空实现降级 | `NoOpTerminalProcessManager` | `Services/TerminalProcessManager.cs` | — |
| `Services/Observability/AmbientRuntimeTraceAccessor.cs` | 环境运行时追踪的访问器 | `IRuntimeTraceAccessor` | `Tools/BuiltIns/Diagnostics/AgentDiagnosticsTool.cs` | — |
| `Services/OpenAiEmbeddingService.cs` | OpenAI 兼容的嵌入向量生成 | `IEmbeddingService` | `Services/SessionChunkIndexer.cs` | — |
| `Services/Orchestration/AgentOrchestrationNodeInputResolver.cs` | 由图输入与上游端口解析节点输入 | `AgentOrchestrationNodeInputResolver` | `Services/Orchestration/AgentOrchestrationWorkerService.cs` | 未实现的 sourcePath 与 targetKey 一律拒绝 |
| `Services/Orchestration/AgentOrchestrationWorkerService.cs` | 编排节点的领取与原子推进 | `AgentOrchestrationWorkerService` | `Services/Orchestration/AgentOrchestrationNodeInputResolver.cs` | 续租与 fence 提交保证单写者 |
| `Services/Orchestration/ImageGenerateOrchestrationNodeExecutor.cs` | 图像生成节点的执行器 | `IAgentOrchestrationNodeExecutor` | `Services/VolcengineArkImageGenerationProvider.cs` | 使用稳定幂等键，避免重复计费 |
| `Services/Orchestration/ImagePreviewOrchestrationNodeExecutor.cs` | 图像预览节点的执行器 | `ImagePreviewOrchestrationNodeExecutor` | `Services/Orchestration/ImageGenerateOrchestrationNodeExecutor.cs` | 只引用上游 Artifact，不复写图片字节 |
| `Services/Orchestration/SubAgentOrchestrationNodeExecutor.cs` | 子代理节点的只读执行器 | `SubAgentOrchestrationNodeExecutor` | `Services/SubAgentInvocationService.cs` | 角色与模板冻结后不得改写 |
| `Services/PersistentCompositionVersionRegistry.cs` | Composition 版本的持久化与恢复结果 | `ICompositionVersionRegistry` | `Services/CompositionRecoveryService.cs` | 恢复失败必须可被调用方观察 |
| `Services/Plugins/PluginDiagnosticsSink.cs` | 插件基础设施诊断证据的写入 | `PluginDiagnosticsSink` | `Services/Plugins/PluginManifestCatalog.cs` | — |
| `Services/Plugins/PluginManifestCatalog.cs` | 数据根下插件描述符的目录 | `IPluginManifestCatalog` | `Controllers/PluginCatalogController.cs` | 本阶段只读清单，不加载插件代码 |
| `Services/Plugins/PluginPackageInstaller.cs` | 插件 ZIP 包的安全安装 | `PluginPackageInstaller` | `Services/Plugins/PluginManifestCatalog.cs` | 安装前校验路径与包结构 |
| `Services/PreCompactionFlushService.cs` | 压缩前的会话冲洗 | `IPreCompactionFlushService` | `Services/ContextCompactionService.cs` | — |
| `Services/ProviderRateLimiter.cs` | 按 Provider 与模型名的并发限流 | `ProviderRateLimiter` | `Services/DirectLlmClient.cs` | — |
| `Services/RuntimeAgentDispatcher.cs` | 运行时 Agent 的派发 | `IRuntimeAgentDispatcher` | `Services/AgentInvocationDispatchFactory.cs` | — |
| `Services/RuntimeSelfRegistrationService.cs` | 节点向 Controller 的自注册与续约 | `RuntimeSelfRegistrationService` | `Controllers/RuntimeExecuteController.cs` | — |
| `Services/SandboxExecutor.cs` | 沙箱执行的二级门控入口 | `SandboxExecutor` | `Services/AgentFirewall.cs` | — |
| `Services/Search/SearchAttemptLedger.cs` | 搜索尝试的失败账本与短路判定 | `SearchAttemptOutcome` | `Tools/BuiltIns/Search/SearchGrepTool.cs` | — |
| `Services/SessionArchiver.cs` | 会话原始记录到 Markdown 的导出 | `SessionArchiver` | `Services/SessionSummaryStore.cs` | — |
| `Services/SessionChunkBackfillService.cs` | 会话块向量的存量回填作业 | `SessionChunkBackfillService` | `Services/SessionChunkIndexer.cs` | — |
| `Services/SessionChunkIndexer.cs` | 会话块切分与向量写入 | `ISessionChunkIndexer` | `Services/OpenAiEmbeddingService.cs` | 不回查到哈希时按源文本现算兜底 |
| `Services/SessionExecutionGate.cs` | 会话状态的进程内单写者门控 | `SessionExecutionGate` | `Services/AgentSessionManager.cs` | — |
| `Services/SessionSummaryStore.cs` | 会话压缩摘要的持久化 | `SessionSummaryStore` | `Services/AgentMemorySummaryContextBuilder.cs` | — |
| `Services/Skills/AgentSkillEvolutionStore.cs` | 技能演进记录的存储 | `AgentSkillEvolutionStore` | `Services/Skills/AgentSkillFileService.cs` | — |
| `Services/Skills/AgentSkillFileService.cs` | Agent 私有技能文件的读写 | `AgentSkillFileService` | `Tools/BuiltIns/Skills/AgentSkillTool.cs` | 读取缺失索引时无副作用，只有显式写操作才建目录 |
| `Services/Skills/ConversationSkillEvolutionTrajectorySource.cs` | 对话侧技能演进轨迹源 | `ConversationSkillEvolutionTrajectorySource` | `Services/Improvement/Rsi/RsiTrajectorySource.cs` | — |
| `Services/Skills/SkillEnforcerService.cs` | 关键词命中技能正文的注入 | `SkillEnforcerService` | `Services/Skills/AgentSkillFileService.cs` | 遥测为旁路且 fail-open，不改匹配判定 |
| `Services/Skills/Telemetry/ISkillUsageTelemetrySink.cs` | 技能使用遥测的落点契约 | `ISkillUsageTelemetrySink` | `Services/Skills/Telemetry/JsonlSkillUsageTelemetrySink.cs` | — |
| `Services/Skills/Telemetry/JsonlSkillUsageTelemetrySink.cs` | 遥测记录按 UTC 分片的 JSONL 落盘 | `JsonlSkillUsageTelemetrySink` | `Services/Skills/Telemetry/SkillUsageRecord.cs` | 任何 IO 或序列化异常只记警告，绝不外抛 |
| `Services/Skills/Telemetry/SkillUsageRecord.cs` | 技能使用遥测的终态模型 | `SkillUsageOutcome` | `Services/Skills/Telemetry/ISkillUsageTelemetrySink.cs` | — |
| `Services/SqliteCompositionStore.cs` | Composition 快照的 SQLite 存储 | `ICompositionStore` | `Services/CompositionSnapshot.cs` | 追加写且版本严格递增，乱序即抛错 |
| `Services/SseEventForwarder.cs` | 流式事件到 SSE 帧的转发 | `SseEventForwarder` | `Controllers/RuntimeExecuteController.cs` | — |
| `Services/StreamingEventBus.cs` | 基于通道的流式事件总线 | `IStreamingEventBus` | `Services/SseEventForwarder.cs` | — |
| `Services/StreamWatchdog.cs` | 流式响应卡死的滑动窗口检测 | `StreamWatchdog` | `Services/AgentExecution/AgentExecutionService.Streaming.cs` | — |
| `Services/SubAgentInvocationService.cs` | 子代理调用的门面 | `ISubAgentInvocationService` | `Services/AgentLoop/SubAgentBudgetLifecycle.cs` | 批量预算等分不可行时整批拒绝 |
| `Services/SubAgents/MemoryExplorerSubAgent.cs` | 检索不足时的深入记忆探索 | `MemoryExplorerSubAgent` | `Services/SubconsciousRecallPipeline.cs` | — |
| `Services/SubconsciousPlanGenerationService.cs` | 后台维护任务的生成与校验 | `SubconsciousPlanGenerationService` | `Services/MemoryMaintenancePlanWriteCommandMapper.cs` | 只生成与校验，不执行 |
| `Services/SubconsciousRecallPipeline.cs` | 潜意识召回管道 | `SubconsciousRecallPipeline` | `Services/CompactionCoverageFilter.cs` | 已覆盖片段与同源哈希片段不得重复注入 |
| `Services/SubconsciousTextProcessingService.cs` | 日摘要与滚动摘要的文本处理门面 | `SubconsciousTextProcessingService` | `Services/MemoryLlmInvocationClient.cs` | — |
| `Services/SystemPromptBuilder.cs` | 分层系统提示的拼装 | `SystemPromptBuilder` | `Services/ContextPipeline.cs` | — |
| `Services/TaskPlanning/TaskDelegationPolicy.cs` | 任务委派的准入策略 | `ITaskDelegationPolicy` | `Tools/BuiltIns/Agents/SubAgentTool.cs` | — |
| `Services/TaskPlanning/TaskPlannerContextBuilder.cs` | 任务规划上下文层的构造 | `TaskPlannerContextBuilder` | `Services/ContextPipelineOrchestrator.cs` | — |
| `Services/TaskTools/GoalLifecycleTools.cs` | Goal 生命周期工具的结果映射 | `GoalLifecycleTools` | `Services/TaskTools/GoalResumeTool.cs` | — |
| `Services/TaskTools/GoalResumeTool.cs` | `goal_resume`：自主恢复暂停或阻塞的目标 | `GoalResumeTool` | `Tools/BuiltIns/Agents/GoalReadTool.cs` | 只允许 paused 或 blocked 转活跃 |
| `Services/TaskTools/ManageTasksTool.cs` | `manage_tasks`：跨 Agent 的看板管理 | `ManageTasksTool` | `Services/TaskTools/TaskToolModels.cs` | 依赖非法即 fail-closed 并给错误码 |
| `Services/TaskTools/TaskClaimTool.cs` | `task_claim`：认领分配给我的任务 | `TaskClaimTool` | `Services/TaskTools/TaskToolModels.cs` | 活动上下文缺失时按归属安全重建 |
| `Services/TaskTools/TaskGetTool.cs` | `task_get`：单任务的完整详情 | `TaskGetTool` | `Services/TaskTools/TaskToolModels.cs` | — |
| `Services/TaskTools/TaskGoalStartTool.cs` | `task_goal_start`：单卡进入 Goal 模式 | `TaskGoalStartTool` | `Services/GoalMode/GoalModeService.cs` | — |
| `Services/TaskTools/TaskListTool.cs` | `task_list`：按范围列出任务 | `TaskListTool` | `Services/TaskTools/TaskToolModels.cs` | — |
| `Services/TaskTools/TaskToolModels.cs` | 任务工具的共享参数模型与守卫 | `TaskToolGuard` | `Services/TaskTools/TaskUpdateTool.cs` | 上下文重建失败只附加非泄露诊断 |
| `Services/TaskTools/TaskUpdateTool.cs` | `task_update`：提交状态迁移与处置 | `TaskUpdateTool` | `Services/TaskTools/TaskToolModels.cs` | 状态与处置合法性一律由服务端状态机裁决 |
| `Services/TerminalProcessManager.cs` | OS 级进程的完整生命周期管理 | `ITerminalProcessManager` | `Tools/BuiltIns/Terminal/TerminalTools.cs` | — |
| `Services/TerminalSecurity.cs` | 终端命令的白名单与危险模式拦截 | `ITerminalCommandPolicy` | `Tools/BuiltIns/Shell/HostShellTool.cs` | — |
| `Services/TimeClusterAnalyzer.cs` | 会话间隔的时间聚类分析 | `TimeClusterAnalyzer` | `Services/ContextPipeline.cs` | — |
| `Services/TodoTools/TodoCheckTool.cs` | `todo_check`：勾选单项待办状态 | `TodoCheckTool` | `Services/TodoTools/TodoToolModels.cs` | — |
| `Services/TodoTools/TodoReadTool.cs` | `todo_read`：读取某作用域的待办表 | `TodoReadTool` | `Services/TodoTools/TodoToolModels.cs` | — |
| `Services/TodoTools/TodoToolModels.cs` | 待办三工具的共享参数模型 | `TodoToolJson` | `Services/TodoTools/TodoWriteTool.cs` | — |
| `Services/TodoTools/TodoWriteTool.cs` | `todo_write`：全量替换待办表 | `TodoWriteTool` | `Services/TodoTools/TodoReadTool.cs` | — |
| `Services/Tools/ToolExposurePlanner.cs` | 与 Provider 无关的工具暴露规划 | `ToolExposurePlanner` | `Tools/Platform/PuddingToolRegistry.cs` | 当前请求的目录冻结，新定义只在下一次 LLM round 生效 |
| `Services/TurnExecutorAdapter.cs` | Turn 执行的门控与请求适配 | `TurnExecutorAdapter` | `Services/Messaging/AgentExecutionAdmissionCoordinator.cs` | 后台忙碌时采用有界退避与节流日志 |
| `Services/UserPreferenceService.cs` | 用户偏好的预取注入与写入 | `UserPreferenceService` | `Tools/BuiltIns/Memory/SavePreferenceTool.cs` | — |
| `Services/VoiceProviderFactory.cs` | 语音 Provider 的按需构造 | `IVoiceProviderFactory` | `Services/DashScopeAsrProvider.cs` | — |
| `Services/VolcengineArkImageGenerationProvider.cs` | 火山方舟的图片生成适配 | `VolcengineArkImageGenerationProvider` | `Services/Orchestration/ImageGenerateOrchestrationNodeExecutor.cs` | — |
| `Services/WarmPrefixCompaction.cs` | 长循环的暖前缀压缩预案与检查点契约 | `WarmPrefixCompaction` | `Services/AgentExecutionService.cs` | 只接受真实缩小的结果 |
| `Services/WikiPageWriteEntry.cs` | 记忆 Wiki 页面的确定性写入入口 | `WikiPageWriteRequest` | `Services/MemoryWikiPageUpdateService.cs` | — |
| `Services/WorkspaceAgentsContextBuilder.cs` | 工作区 Agent 名册上下文层的构造 | `WorkspaceAgentsContextBuilder` | `Services/ContextPipelineOrchestrator.cs` | — |
| `Services/YoloSignalService.cs` | 监听工作区 yolo.signal 的后台服务 | `YoloSignalService` | `Services/AgentFirewall.cs` | 只读文件信号，不放宽任何资源边界 |

## 工具审批与准入实现

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `Tools/Approval/ClassifierStatusTool.cs` | `classifier_status`：分类器健康面的只读探查 | `ClassifierStatusTool` | `Classification/ClassifierHealthReporter.cs` | 只读探查，不授予权限也不触发分类器调用 |
| `Tools/Approval/ClassifierToolApprovalReviewer.cs` | 分类器驱动的审批评审适配器 | `IToolApprovalReviewer` | `Classification/ToolCallClassifierPipeline.cs` | 未知结论一律 deferred，不折叠为拒绝 |
| `Tools/Approval/FakeToolApprovalReviewer.cs` | 硬化规则集的审批评审器 | `FakeToolApprovalReviewer` | `Tools/Approval/InMemoryToolApprovalService.cs` | — |
| `Tools/Approval/FileToolApprovalStores.cs` | 审批票据与规则的文件存储 | `IToolApprovalTicketStore` | `Tools/Approval/InMemoryToolApprovalTicketStore.cs` | — |
| `Tools/Approval/InMemoryToolApprovalAllowlistStore.cs` | 白名单规则的进程内存储 | `IToolApprovalAllowlistStore` | `Classification/ClassificationRuleCurator.cs` | — |
| `Tools/Approval/InMemoryToolApprovalAuditStore.cs` | 审批审计事件的进程内存储 | `IToolApprovalAuditStore` | `Operators/Adapters/OperatorAuditSinkAdapter.cs` | — |
| `Tools/Approval/InMemoryToolApprovalService.cs` | 审批判定的主实现与检查次序 | `InMemoryToolApprovalService` | `Services/AgentFirewall.cs` | 授权与审批的判定次序不可调换 |
| `Tools/Approval/InMemoryToolApprovalTicketStore.cs` | 审批票据的进程内存储 | `IToolApprovalTicketStore` | `Tools/Approval/FileToolApprovalStores.cs` | — |
| `Tools/Approval/InMemoryToolAuthorizationService.cs` | 工具授权的进程内服务 | `IToolAuthorizationService` | `Tools/Approval/InMemoryToolApprovalService.cs` | — |
| `Tools/Approval/JevToolApprovalOptions.cs` | Jev 评审的配置选项 | `JevToolApprovalOptions` | `Tools/Approval/JevToolApprovalReviewer.cs` | — |
| `Tools/Approval/JevToolApprovalReviewer.cs` | Jev 决策模型驱动的审批评审器 | `JevToolApprovalReviewer` | `Services/JevDecisionService.cs` | 危险模式命中即不调用模型；不可用一律 deferred |
| `Tools/Approval/JevToolCallClassifier.cs` | Jev 仲裁分类器 | `JevToolCallClassifier` | `Services/JevDecisionService.cs` | 解析失败即 Unknown；每次至多请求一次 |
| `Tools/Approval/ListToolApprovalsTool.cs` | `list_tool_approvals`：待审批票据查询 | `ListToolApprovalsTool` | `Tools/Approval/InMemoryToolApprovalTicketStore.cs` | — |
| `Tools/Approval/LlmToolApprovalReviewer.cs` | 由 LLM 出题的审批评审器 | `LlmToolApprovalReviewer` | `Tools/Approval/InMemoryToolApprovalService.cs` | 保留为可配置回退路径，不删除 |
| `Tools/Approval/RequestToolApprovalTool.cs` | `request_tool_approval`：发起一次性授权请求 | `RequestToolApprovalTool` | `Tools/Approval/InMemoryToolApprovalService.cs` | — |
| `Tools/Approval/ToolApprovalBuiltInAllowlistRules.cs` | 内置白名单规则集 | `ToolApprovalBuiltInAllowlistRules` | `Classification/ClassificationRuleCurator.cs` | — |
| `Tools/Approval/ToolApprovalClassifierOptions.cs` | 分类器降级配置 | `ToolApprovalClassifierOptions` | `Classification/ClassifierHealthReporter.cs` | 只新增键，不改既有默认 |
| `Tools/Approval/ToolApprovalCommandFirewall.cs` | 危险命令行模式的确定性拒绝 | `ToolApprovalCommandFirewall` | `Tools/Approval/JevToolApprovalReviewer.cs` | — |
| `Tools/Approval/ToolApprovalPortalService.cs` | 分类门户：分类与规则管理 | `ToolApprovalPortalService` | `Classification/ClassificationRuleCurator.cs` | 人工规则绝不冒充分类器终局 |
| `Tools/Approval/ToolApprovalPromptBuilder.cs` | 审批出题单的构造 | `ToolApprovalPromptBuilder` | `Tools/Approval/LlmToolApprovalReviewer.cs` | — |
| `Tools/Approval/ToolApprovalReviewParser.cs` | 评审答复的解析 | `ToolApprovalReviewParser` | `Tools/Approval/LlmToolApprovalReviewer.cs` | — |
| `Tools/Approval/ToolDefinitionHash.cs` | 工具定义的 canonical 哈希 | `ToolDefinitionHash` | `Tools/Platform/PuddingToolRegistry.cs` | — |
| `Tools/Approval/WorkspaceAuditAgentProvider.cs` | 工作区审计 Agent 的运行时侧适配 | `IWorkspaceAuditAgentProvider` | `Services/AgentFirewall.cs` | — |
| `Tools/Legacy/AgentSkillPackageRegistry.cs` | 运行时技能包注册表 | `AgentSkillPackageRegistry` | `Tools/Legacy/SkillRuntime.cs` | — |
| `Tools/Legacy/IAgentSkill.cs` | 运行时侧 Agent 技能接口 | `IAgentSkill` | `Tools/Legacy/SkillRuntime.cs` | — |
| `Tools/Legacy/SkillPackageDownloadService.cs` | 技能包下载与解压 | `SkillPackageDownloadService` | `Tools/Legacy/AgentSkillPackageRegistry.cs` | — |
| `Tools/Legacy/SkillRuntime.cs` | 可供 Agent 调用的技能套件管理 | `SkillRuntime` | `Tools/BuiltIns/Skills/AgentSkillTool.cs` | — |

## 内置工具（Tools/BuiltIns）

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `Tools/BuiltIns/Agents/AgentSleepTool.cs` | `sleep`：Agent 自适应心跳间隔设置 | `AgentSleepTool` | `Services/AgentWakeQueue.cs` | 空闲间隔被夹取在合法区间内 |
| `Tools/BuiltIns/Agents/AgentStatusTool.cs` | `agent_status`：工作区 Agent 状态的只读查询 | `AgentStatusTool` | `Services/Messaging/DefaultAgentExecutionAvailabilityProvider.cs` | 投影缺失或过期一律报 unknown |
| `Tools/BuiltIns/Agents/AgentTestTool.cs` | `test_tool`：工具试跑入口 | `AgentTestTool` | `Tools/Platform/ToolInvocationService.cs` | — |
| `Tools/BuiltIns/Agents/GoalReadTool.cs` | `goal_read`：读取调用方私有的目标文件 | `GoalReadTool` | `Services/GoalMode/GoalModeService.cs` | — |
| `Tools/BuiltIns/Agents/GoalUpdateTool.cs` | `goal_update`：追加或整体覆盖目标文件 | `GoalUpdateTool` | `Tools/BuiltIns/Agents/GoalReadTool.cs` | — |
| `Tools/BuiltIns/Agents/QuerySubAgentsTool.cs` | `query_sub_agents`：查询子代理状态 | `QuerySubAgentsTool` | `Services/SubAgentInvocationService.cs` | — |
| `Tools/BuiltIns/Agents/SubAgentTool.cs` | `spawn_sub_agent`：派生子代理执行任务 | `SubAgentTool` | `Services/SubAgentInvocationService.cs` | 模型须写全 providerId 与 modelId；大交付物写外部文件 |
| `Tools/BuiltIns/CodeIntelligence/CodeOutlineTool.cs` | `code_outline`：文件顶层结构的树形输出 | `CodeOutlineTool` | `Tools/BuiltIns/CodeIntelligence/OutlineSyntaxVisitor.cs` | — |
| `Tools/BuiltIns/CodeIntelligence/CodeProjectManagementTools.cs` | 代码索引项目的注册与查询工具 | `CodeProjectAddTool` | `Services/Tools/ToolExposurePlanner.cs` | — |
| `Tools/BuiltIns/CodeIntelligence/CodeQueryTools.cs` | 符号检索与调用关系查询工具集 | `CodeQueryToolHelper` | `Tools/BuiltIns/CodeIntelligence/CodeSummaryTool.cs` | — |
| `Tools/BuiltIns/CodeIntelligence/CodeSummaryTool.cs` | `code_summary`：文件用途摘要的快速抽取 | `CodeSummaryTool` | `Tools/BuiltIns/CodeIntelligence/CodeQueryTools.cs` | — |
| `Tools/BuiltIns/CodeIntelligence/OutlineNode.cs` | 大纲节点的模型 | `OutlineNode` | `Tools/BuiltIns/CodeIntelligence/OutlineSyntaxVisitor.cs` | — |
| `Tools/BuiltIns/CodeIntelligence/OutlineSyntaxVisitor.cs` | Roslyn 语法树的顶层结构遍历 | `OutlineSyntaxVisitor` | `Tools/BuiltIns/CodeIntelligence/OutlineNode.cs` | — |
| `Tools/BuiltIns/CodeIntelligence/ProjectMapTool.cs` | `project_map`：项目模块概览 | `ProjectMapTool` | `Tools/BuiltIns/CodeIntelligence/CodeSummaryTool.cs` | — |
| `Tools/BuiltIns/Context/SessionCompactTool.cs` | `compact_session`：会话上下文的手动压缩 | `SessionCompactTool` | `Services/ContextCompactionService.cs` | 当前轮围栏守卫优先于手动压缩 |
| `Tools/BuiltIns/Diagnostics/AgentDiagnosticsTool.cs` | `agent_diagnostics`：Agent 自我诊断 | `AgentDiagnosticsTool` | `Services/Diagnostics/RuntimeDiagnosisEngine.cs` | 数据源不可用时显式报出，不当作无问题 |
| `Tools/BuiltIns/Documents/ReadOfficeDocumentTool.cs` | `read_office_document`：办公文档读取 | `ReadOfficeDocumentTool` | `Tools/BuiltIns/Files/FileChunkService.cs` | — |
| `Tools/BuiltIns/Events/EventSubscriptionTool.cs` | `event_subscribe`：事件类型的订阅与退订 | `EventSubscriptionTool` | `Services/Events/EventDispatcher.cs` | — |
| `Tools/BuiltIns/Files/FileChunkService.cs` | 大文件的分块与流式窗口 | `FileChunkService` | `Tools/BuiltIns/Files/FileTools.cs` | — |
| `Tools/BuiltIns/Files/FileMutationQueue.cs` | 同一文件写入的串行队列 | `FileMutationQueue` | `Tools/BuiltIns/Files/FilePatchTool.cs` | — |
| `Tools/BuiltIns/Files/FilePatchTool.cs` | `file_patch`：文本文件的增量补丁 | `FilePatchTool` | `Tools/BuiltIns/Files/FileMutationQueue.cs` | 删除须显式空替换；行尾等价匹配；new_text 未带换行即删除；容忍歧义边界拒绝写入；预览 diff 按行对齐；scope 行范围按原文计偏移；无变化时上报结构化 status=no_match（成功态，与失败严格区分） |
| `Tools/BuiltIns/Files/FileSearchTool.cs` | `file_search`：按文件名检索 | `FileSearchTool` | `Tools/BuiltIns/Search/SearchGrepTool.cs` | 无命中只在覆盖完整时输出；参数合同失败上报 contract_error（宿主可用性与目标不存在保持 null） |
| `Tools/BuiltIns/Files/FileTools.cs` | 文件读写与目录列举工具集 | `HostFileToolPaths` | `Tools/BuiltIns/Files/FilePatchTool.cs` | 相对路径解析到工作区根 |
| `Tools/BuiltIns/Git/GitAddTool.cs` | `git_add`：暂存指定文件 | `GitAddTool` | `Tools/BuiltIns/Git/GitConstants.cs` | — |
| `Tools/BuiltIns/Git/GitBlameTool.cs` | `git_blame`：逐行归属查询 | `GitBlameTool` | `Tools/BuiltIns/Git/GitConstants.cs` | — |
| `Tools/BuiltIns/Git/GitBranchCreateTool.cs` | `git_branch_create`：创建分支 | `GitBranchCreateTool` | `Tools/BuiltIns/Git/GitBranchSwitchTool.cs` | — |
| `Tools/BuiltIns/Git/GitBranchListTool.cs` | `git_branch_list`：列出分支与工作树 | `GitBranchListTool` | `Tools/BuiltIns/Git/GitConstants.cs` | — |
| `Tools/BuiltIns/Git/GitBranchSwitchTool.cs` | `git_branch_switch`：切换分支 | `GitBranchSwitchTool` | `Tools/BuiltIns/Git/GitCheckoutTool.cs` | — |
| `Tools/BuiltIns/Git/GitCheckoutTool.cs` | `git_checkout`：检出文件或提交 | `GitCheckoutTool` | `Tools/BuiltIns/Git/GitResetTool.cs` | — |
| `Tools/BuiltIns/Git/GitCloneTool.cs` | `git_clone`：克隆仓库 | `GitCloneTool` | `Tools/BuiltIns/Git/GitInitTool.cs` | — |
| `Tools/BuiltIns/Git/GitCommitTool.cs` | `git_commit`：提交暂存内容 | `GitCommitTool` | `Tools/BuiltIns/Git/GitAddTool.cs` | files 参数同时接受字符串与字符串数组 |
| `Tools/BuiltIns/Git/GitConstants.cs` | Git 工具的共享常量 | `GitConstants` | `Tools/BuiltIns/Git/GitStatusTool.cs` | — |
| `Tools/BuiltIns/Git/GitDiffTool.cs` | `git_diff`：差异输出 | `GitDiffTool` | `Tools/BuiltIns/Git/GitStatusTool.cs` | — |
| `Tools/BuiltIns/Git/GitFetchTool.cs` | `git_fetch`：抓取远端更新 | `GitFetchTool` | `Tools/BuiltIns/Git/GitPullTool.cs` | — |
| `Tools/BuiltIns/Git/GitInitTool.cs` | `git_init`：初始化仓库 | `GitInitTool` | `Tools/BuiltIns/Git/GitCloneTool.cs` | — |
| `Tools/BuiltIns/Git/GitLogTool.cs` | `git_log`：提交历史查询 | `GitLogTool` | `Tools/BuiltIns/Git/GitBlameTool.cs` | — |
| `Tools/BuiltIns/Git/GitMergeTool.cs` | `git_merge`：合并分支 | `GitMergeTool` | `Tools/BuiltIns/Git/GitPullTool.cs` | — |
| `Tools/BuiltIns/Git/GitPullTool.cs` | `git_pull`：拉取并合并 | `GitPullTool` | `Tools/BuiltIns/Git/GitFetchTool.cs` | — |
| `Tools/BuiltIns/Git/GitPushTool.cs` | `git_push`：推送本地提交 | `GitPushTool` | `Tools/BuiltIns/Git/GitRemoteTool.cs` | — |
| `Tools/BuiltIns/Git/GitRemoteTool.cs` | `git_remote`：远端配置的查看与设置 | `GitRemoteTool` | `Tools/BuiltIns/Git/GitCloneTool.cs` | — |
| `Tools/BuiltIns/Git/GitResetTool.cs` | `git_reset`：重置暂存区或提交 | `GitResetTool` | `Tools/BuiltIns/Git/GitCheckoutTool.cs` | — |
| `Tools/BuiltIns/Git/GitStashTool.cs` | `git_stash`：工作区暂存与恢复 | `GitStashTool` | `Tools/BuiltIns/Git/GitStatusTool.cs` | — |
| `Tools/BuiltIns/Git/GitStatusTool.cs` | `git_status`：工作区状态查询 | `GitStatusTool` | `Tools/BuiltIns/Git/GitDiffTool.cs` | — |
| `Tools/BuiltIns/Git/GitTagTool.cs` | `git_tag`：标签管理 | `GitTagTool` | `Tools/BuiltIns/Git/GitLogTool.cs` | — |
| `Tools/BuiltIns/Http/FlurlWebClient.cs` | 基于 Flurl 的 HTTP 传输实现 | `IWebClient` | `Tools/BuiltIns/Http/HttpFetchContracts.cs` | — |
| `Tools/BuiltIns/Http/HtmlContentExtractor.cs` | HTML 正文的抽取 | `HtmlContentExtractor` | `Tools/BuiltIns/Http/HttpFetchContentFormatter.cs` | — |
| `Tools/BuiltIns/Http/HttpFetchContentFormatter.cs` | 抓取结果到可读文本的格式化 | `IHttpFetchContentFormatter` | `Tools/BuiltIns/Http/HtmlContentExtractor.cs` | — |
| `Tools/BuiltIns/Http/HttpFetchContracts.cs` | HTTP 抓取的端口与数据契约 | `IWebClient` | `Tools/BuiltIns/Http/HttpFetchSkill.cs` | — |
| `Tools/BuiltIns/Http/HttpFetchSkill.cs` | `http_fetch`：发起 HTTP 请求并返回正文 | `HttpFetchSkill` | `Tools/BuiltIns/Http/HttpFetchContentFormatter.cs` | — |
| `Tools/BuiltIns/Http/ReverseMarkdownHtmlToMarkdownConverter.cs` | HTML 到 Markdown 的转换适配 | `IHtmlToMarkdownConverter` | `Tools/BuiltIns/Http/HttpFetchContentFormatter.cs` | — |
| `Tools/BuiltIns/Llm/ListLlmProvidersTool.cs` | `list_llm_providers`：资源池路由表查询 | `ListLlmProvidersTool` | `Services/LlmProfileResolver.cs` | 严禁输出 apiKey 与 baseUrl |
| `Tools/BuiltIns/Management/AgentStateTool.cs` | `agent_state`：Agent 私有状态的读写 | `AgentStateTool` | `Services/AgentSessionManager.cs` | — |
| `Tools/BuiltIns/Management/BootstrapRebootTool.cs` | `bootstrap_reboot`：点火式重建重启 | `BootstrapRebootTool` | `Services/RuntimeSelfRegistrationService.cs` | 默认走构建与事务部署并校验哈希 |
| `Tools/BuiltIns/Management/LlmResourcePoolTool.cs` | `llm_resource_pool`：服务商与模型清单 | `LlmResourcePoolTool` | `Tools/BuiltIns/Llm/ListLlmProvidersTool.cs` | — |
| `Tools/BuiltIns/Management/SubconsciousTriggerTool.cs` | `subconscious_trigger`：手动触发潜意识管道 | `SubconsciousTriggerTool` | `Services/Background/SubconsciousWorkerService.cs` | — |
| `Tools/BuiltIns/Memory/GrepMemoryTool.cs` | `grep_memory`：记忆全文与混合检索 | `GrepMemoryTool` | `Tools/BuiltIns/Memory/MemoryLibraryTool.cs` | — |
| `Tools/BuiltIns/Memory/Handlers/BookHandler.cs` | 记忆 Book 的创建与列举删除 | `BookHandler` | `Tools/BuiltIns/Memory/ManageMemoryTool.cs` | — |
| `Tools/BuiltIns/Memory/Handlers/ChapterHandler.cs` | 记忆 Chapter 的增删改查 | `ChapterHandler` | `Tools/BuiltIns/Memory/ManageMemoryTool.cs` | — |
| `Tools/BuiltIns/Memory/Handlers/DedupHandler.cs` | Book 去重与章节合并 | `DedupHandler` | `Tools/BuiltIns/Memory/Handlers/BookHandler.cs` | — |
| `Tools/BuiltIns/Memory/Handlers/GraphHandler.cs` | 知识图谱关联的增列查 | `GraphHandler` | `Tools/BuiltIns/Memory/ManageMemoryTool.cs` | — |
| `Tools/BuiltIns/Memory/Handlers/ReferenceHandler.cs` | 引用指针的增列 | `ReferenceHandler` | `Tools/BuiltIns/Memory/ManageMemoryTool.cs` | — |
| `Tools/BuiltIns/Memory/ManageMemoryTool.cs` | `manage_memory`：记忆操作的 action 分发 | `ManageMemoryTool` | `Tools/BuiltIns/Memory/Handlers/BookHandler.cs` | 只做分发，逻辑在 Handler |
| `Tools/BuiltIns/Memory/MemoryLibraryTool.cs` | `search_memory`：记忆库检索 | `MemoryLibraryTool` | `Tools/BuiltIns/Memory/GrepMemoryTool.cs` | — |
| `Tools/BuiltIns/Memory/MemoryToolArgs.cs` | 记忆工具的参数模型 | `SaveMemoryArgs` | `Tools/BuiltIns/Memory/ManageMemoryTool.cs` | — |
| `Tools/BuiltIns/Memory/MemoryToolHelper.cs` | 记忆工具的共享辅助 | `MemoryToolHelper` | `Tools/BuiltIns/Memory/MemoryLibraryTool.cs` | — |
| `Tools/BuiltIns/Memory/MemoryTools.cs` | 记忆工具的聚合注册 | `MemoryTools` | `Tools/BuiltIns/Memory/MemoryLibraryTool.cs` | — |
| `Tools/BuiltIns/Memory/MemoryToolsHelpers.cs` | 记忆工具辅助类型的归集 | `MemoryToolsHelpers` | `Tools/BuiltIns/Memory/MemoryTools.cs` | — |
| `Tools/BuiltIns/Memory/SaveMemoryTool.cs` | `save_memory`：主动写入事实与摘要 | `SaveMemoryTool` | `Services/MemoryQualityFilter.cs` | — |
| `Tools/BuiltIns/Memory/SavePreferenceTool.cs` | `save_preference`：用户偏好的写入 | `SavePreferenceTool` | `Services/UserPreferenceService.cs` | — |
| `Tools/BuiltIns/Messaging/ListAgentsTool.cs` | `list_agents`：消息可达 Agent 名册 | `ListAgentsTool` | `Services/Messaging/MessageDeliveryDispatcher.cs` | — |
| `Tools/BuiltIns/Messaging/ReceiveMessagesTool.cs` | `receive_messages`：拉取收件箱 | `ReceiveMessagesTool` | `Services/Messaging/MessageDeliveryDispatcher.cs` | — |
| `Tools/BuiltIns/Messaging/SendMessageTool.cs` | `send_message`：向其他 Agent 或用户发消息 | `SendMessageTool` | `Services/Messaging/MessageDeliveryDispatcher.cs` | 默认只通知；未知 intent fail-closed |
| `Tools/BuiltIns/Search/AnySearchSearchTool.cs` | `anysearch_search`：通用网页搜索 | `AnySearchSearchTool` | `Tools/BuiltIns/Search/DoubaoSearchTool.cs` | — |
| `Tools/BuiltIns/Search/DoubaoSearchTool.cs` | `doubao_search`：豆包搜索 | `DoubaoSearchTool` | `Tools/BuiltIns/Search/AnySearchSearchTool.cs` | — |
| `Tools/BuiltIns/Search/GitHubSearchTool.cs` | `github_search`：GitHub 仓库与代码检索 | `GitHubSearchTool` | `Tools/BuiltIns/Search/AnySearchSearchTool.cs` | — |
| `Tools/BuiltIns/Search/SearchGrepTool.cs` | `search_grep`：工作区文本检索 | `SearchGrepTool` | `Services/Search/SearchAttemptLedger.cs` | 索引只产候选，匹配以磁盘当前内容为准 |
| `Tools/BuiltIns/Search/SearchToolsTool.cs` | `search_tools`：工具目录检索与加载 | `SearchToolsTool` | `Services/Tools/ToolExposurePlanner.cs` | 只返回权限过滤后的条目 |
| `Tools/BuiltIns/Search/ZhihuGlobalSearchTool.cs` | `zhihu_global_search`：全网检索 | `ZhihuGlobalSearchTool` | `Tools/BuiltIns/Search/ZhihuSearchShared.cs` | — |
| `Tools/BuiltIns/Search/ZhihuSearchShared.cs` | 知乎检索的共享配置与渲染 | `ZhihuSearchShared` | `Tools/BuiltIns/Search/ZhihuSearchTool.cs` | — |
| `Tools/BuiltIns/Search/ZhihuSearchTool.cs` | `zhihu_search`：知乎站内检索 | `ZhihuSearchTool` | `Tools/BuiltIns/Search/ZhihuSearchShared.cs` | — |
| `Tools/BuiltIns/Sessions/QuerySessionLogsTool.cs` | `query_session_logs`：原始会话日志检索 | `QuerySessionLogsTool` | `Services/SessionArchiver.cs` | — |
| `Tools/BuiltIns/Sessions/QuerySessionsTool.cs` | `query_sessions`：会话消息转录查询 | `QuerySessionsTool` | `Tools/BuiltIns/Sessions/QuerySessionLogsTool.cs` | — |
| `Tools/BuiltIns/Shell/HostShellExecutor.cs` | 宿主命令的直执行与输出收集 | `HostShellExecutor` | `Services/TerminalSecurity.cs` | — |
| `Tools/BuiltIns/Shell/HostShellTool.cs` | `shell`：短命令的宿主执行 | `HostShellTool` | `Tools/BuiltIns/Shell/HostShellExecutor.cs` | — |
| `Tools/BuiltIns/Skills/AgentSkillTool.cs` | `agent_skill`：技能文件的读取与增改 | `AgentSkillTool` | `Services/Skills/AgentSkillFileService.cs` | — |
| `Tools/BuiltIns/Skills/SkillHubTool.cs` | `skill_hub`：技能市场清单与安装 | `SkillHubTool` | `Services/Skills/AgentSkillFileService.cs` | — |
| `Tools/BuiltIns/SmartWorkflow/SmartDeployTool.cs` | `smart_deploy`：部署角色入口 | `SmartDeployTool` | `Tools/BuiltIns/SmartWorkflow/SmartWorkflowToolBase.cs` | — |
| `Tools/BuiltIns/SmartWorkflow/SmartDevelopTool.cs` | `smart_develop`：开发角色入口 | `SmartDevelopTool` | `Tools/BuiltIns/SmartWorkflow/SmartWorkflowToolBase.cs` | — |
| `Tools/BuiltIns/SmartWorkflow/SmartExploreTool.cs` | `smart_explore`：探索角色入口 | `SmartExploreTool` | `Tools/BuiltIns/SmartWorkflow/SmartWorkflowToolBase.cs` | 只读探索，不产生副作用 |
| `Tools/BuiltIns/SmartWorkflow/SmartPlanTool.cs` | `smart_plan`：方案角色入口 | `SmartPlanTool` | `Tools/BuiltIns/SmartWorkflow/SmartWorkflowToolBase.cs` | — |
| `Tools/BuiltIns/SmartWorkflow/SmartResearchTool.cs` | `smart_research`：调研角色入口 | `SmartResearchTool` | `Tools/BuiltIns/SmartWorkflow/SmartWorkflowToolBase.cs` | — |
| `Tools/BuiltIns/SmartWorkflow/SmartReviewTool.cs` | `smart_review`：审阅角色入口 | `SmartReviewTool` | `Tools/BuiltIns/SmartWorkflow/SmartWorkflowToolBase.cs` | — |
| `Tools/BuiltIns/SmartWorkflow/SmartTestTool.cs` | `smart_test`：测试角色入口 | `SmartTestTool` | `Tools/BuiltIns/SmartWorkflow/SmartWorkflowToolBase.cs` | — |
| `Tools/BuiltIns/SmartWorkflow/SmartWorkflowToolBase.cs` | 角色化工作流工具的公共基类 | `SmartWorkflowToolBase<TArgs>` | `Tools/BuiltIns/Agents/SubAgentTool.cs` | 校验失败时原样返回子代理产出并附说明 |
| `Tools/BuiltIns/Terminal/TerminalSkill.cs` | 终端命令执行的技能封装 | `TerminalSkill` | `Tools/BuiltIns/Terminal/TerminalTools.cs` | — |
| `Tools/BuiltIns/Terminal/TerminalTools.cs` | `terminal_*`：终端任务的完整工具集 | `TerminalToolJson` | `Services/TerminalProcessManager.cs` | 长任务用阻塞式等待，禁止秒级轮询 |

## 工具平台与展示投影

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `Tools/Platform/AuditLogger.cs` | 工具与审批审计事件的写入 | `AuditLogger` | `Tools/Approval/InMemoryToolApprovalAuditStore.cs` | — |
| `Tools/Platform/HarnessToolCompatibilityAdapter.cs` | 训练框架别名到 canonical 工具的适配 | `HarnessToolCompatibilityAdapter` | `Tools/Platform/ToolInvocationService.cs` | canonical 工具保持唯一；搜索退出码 1 识别为无命中 |
| `Tools/Platform/JsonElementExtensions.cs` | JsonElement 的读取扩展 | `JsonElementExtensions` | `Tools/BuiltIns/Memory/MemoryToolArgs.cs` | — |
| `Tools/Platform/OperationZone.cs` | 工具操作分区的枚举 | `OperationZone` | `Tools/Platform/ToolPermissionPolicyService.cs` | — |
| `Tools/Platform/PuddingToolRegistry.cs` | 工具注册表与 schema 投影 | `IPuddingToolRegistry` | `Services/Tools/ToolExposurePlanner.cs` | 内置工具在进程生命周期内保持稳定 |
| `Tools/Platform/PuddingToolServiceCollectionExtensions.cs` | 工具与分类器的装配注册 | `PuddingToolServiceCollectionExtensions` | `DependencyInjection.cs` | 新增注册一律追加，不改既有注册行 |
| `Tools/Platform/ToolInvocationService.cs` | 工具调用的统一执行边界 | `IToolInvocationService` | `Services/AgentFirewall.cs` | callId 进入注册表后保持不变 |
| `Tools/Platform/ToolLoopInstructionBuilder.cs` | 工具循环指引的稳定生成 | `ToolLoopInstructionBuilder` | `Services/Tools/ToolExposurePlanner.cs` | 只在 `search_tools` 可见时才宣称可发现延迟工具 |
| `Tools/Platform/ToolPermissionPolicyService.cs` | 默认的工具权限策略 | `IToolPermissionPolicyService` | `Services/AgentFirewall.cs` | — |
| `Tools/Presentation/ToolPresentationCatalog.cs` | 工具展示声明的登记表 | `ToolPresentationCatalog` | `Tools/Presentation/ToolPresentationProjector.cs` | — |
| `Tools/Presentation/ToolPresentationJson.cs` | 展示投影用的 JSON 读取助手 | `ToolPresentationJson` | `Tools/Presentation/ToolPresentationCatalog.cs` | 只读事实，不猜值 |
| `Tools/Presentation/ToolPresentationProjector.cs` | 工具调用的展示投影器 | `IToolPresentationProjector` | `Tools/Presentation/ToolPresentationCatalog.cs` | — |

## 本项目约束与坑

- 并发与单写者：`Services/AgentExecutionService.cs` 是会话的单写者；`Services/SessionExecutionGate.cs` 提供进程内门控；跨进程互斥靠租约与 fence。
- 输入围栏：本轮输入以 `CURRENT USER TURN/input_sha256` 围栏，缺失即 fail-closed；压缩候选命中本轮时在任何写入前 fail-closed。
- 前缀稳定：稳定 system 前缀只被真实稳定头变化打破；召回与 inbound 内容只能进本轮 User tail，否则缓存命中率会掉。
- 能力边界：`Services/AgentFirewall.cs` 的完全访问授予只放宽授权与审批闸门；Yolo、沙箱、工作区与资源边界不受影响。
- 判定与留痕：算子与分类器的审计写入失败只记警告，不改变已定裁决；健康计数成功返回后按分类器维度重置。
- 未命中不放大：`Unknown` 表示无证据或不可判定，绝不折叠为放行或拒绝；分类器健康面同样如此。
- 预算与止损：预算裁剪、失败指纹、发现循环三类止损各自有阈值；剩余预算诚实归零并带派生标记。
- 长任务执行：终端与构建类任务走后台作业加阻塞式等待，不进行秒级轮询。
- 心跳职责：`Services/HeartbeatService.cs` 只做会话超时清理；Agent 自主心跳编排在 `Services/Background/` 与 `Services/AgentWakeQueue.cs`。

## 观察项（observe，不写语义）

- `build_output.txt`
- `PuddingRuntime.csproj.lscache`
- `runtimes/win-x64/native/Everything64.dll`
