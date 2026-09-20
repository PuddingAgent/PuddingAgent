# PuddingRuntime CodeMAP

> 运行时核心 | Agent Loop · LLM 调用 · 工具系统 · 上下文管线 · Git 20 工具

## 入口 & 配置

| 文件 | 用途 |
|------|------|
| `DependencyInjection.cs` | Runtime 服务注册入口；含 `FrozenVisionContextAccessor` singleton（V5 视觉冻结快照通道，DependencyInjection.cs:119） |
| `Services/PuddingConfigLoader.cs` | JSON 配置加载 |
| `Services/PuddingJsonConfig.cs` | 配置模型定义 |
| `Services/RuntimeExecutionConfigService.cs` | 执行配置：忠实加载 runtime.execution.json（`Math.Max(1, cfg)`，已删除 600/2400/24h 强抬 normalize）；600/2400/24h 仅为系统 profile 默认（非下限/上限/强制统一值）；20 轮/30 分钟收尾宽限；规范化临时子代理目录保留/隔离参数；请求级预算覆盖见 SubAgentInvocationContracts（int? MaxRounds 等） |

## Agent Loop

| 文件 | 用途 |
|------|------|
| `Services/AgentExecutionService.cs` | 🔑 执行编排入口，session 单写者，liveness/progress 报告；把日期、召回和 inbound context 与当前消息组成 volatile User tail；同一 prefix epoch 冻结 message-zero system bytes，真实稳定头变化一次性提交并显式归因；以 `CURRENT USER TURN/input_sha256` 围栏当前输入；提供 Harness 对齐的 warm-prefix checkpoint（原样 replay、有效缩小时原子提交、失败保留 history、每 dispatch 一次）和 compaction Token 归因；构造注入 `FrozenVisionContextAccessor`（V5 视觉冻结快照，AgentExecutionService.cs:97/:159/:207） |
| `Services/AgentExecution/AgentExecutionService.Buffered.cs` | 非流式主循环（partial）；共用冻结 system 与 warm-prefix checkpoint；dispatch 冻结 tool catalog/schema，`search_tools` 激活在下一 LLM round 单调生效并标记 `tool_spec_changed`；prefix-v2 事件带 history anchor/reason/serialization；预算裁剪后以当前轮围栏 fail-closed；canonical 相同调用第二次得到不变失败时转 `execution_stalled`；最终回复边界命中 late Steering 时继续同一 Turn；ExecuteAsync 入口 `Push(CallerLlmSnapshot)` 冻结视觉上下文（V5，AgentExecutionService.Buffered.cs:50） |
| `Services/AgentExecution/AgentExecutionService.Streaming.cs` | SSE 流式主循环（partial）；与 Buffered 共用 round-boundary 动态工具激活、冻结 system、warm-prefix checkpoint、prefix-v2、当前轮围栏、canonical Token attribution 与失败熔断；direct Token 先提交、usage SSE 后发布；provider length/incomplete 只允许一次立即行动恢复，再截断显式失败；最终流式回复边界命中 late Steering 时继续同一 Turn；ExecuteStreamAsync 入口 `Push(CallerLlmSnapshot)` 冻结视觉上下文（V5，AgentExecutionService.Streaming.cs:38） |
| `Services/AgentExecution/FailedToolCallTracker.cs` | 第一层止损：对 canonical tool+args 的有界失败结果做 SHA-256 指纹；第二次不变失败标记 `execution_stalled`，后续阻断；参数变化后的同失败族由 Core `RuntimeControlService` 第 5 次熔断 |
| `Services/AgentExecution/ToolDiscoveryLoopTracker.cs` | 动态工具发现止损；不同查询文本仍归一为 discovery-only 进展族，连续 8 次只调用 `search_tools` 而不执行已发现业务工具时触发 `tool_discovery_stalled`，任一实际业务工具会重置计数 |
| `Services/AgentExecution/ExecutionUsageBudgetTracker.cs` | WorkUnit 调用边界 input/output/cache-hit/cost 累计账本；生成剩余预算供工具/子代理继承并输出含同步后代的累计 usage；provider/child call 后先记账，再决定工具/下一 LLM round，Buffered/Streaming 共用；剩余值诚实归零并打 IsDerivedRemainder 标（含单轮峰值输入追踪），0 值轴=父级已耗尽 |
| `Services/AgentExecution/ToolResultContextPolicy.cs` | 工具结果进入模型历史前的统一 8 KiB 边界；完整原文作为 workspace-scoped artifact 保存，sidecar manifest 固化 SHA-256、UTF-8 字节、行数和 session/tool/call 身份；模型输入不做脱敏并提供渐进读取路径，存储失败时 fail-open |
| `Services/Messaging/MessageDeliveryDispatcher.cs` | durable Message Fabric 投递；`execute` 按 deliveryId 精确领取，**所有**投递（用户/Agent/心跳/sub-agent 结果）一律经 `AcceptCanonicalConversationTurnAsync` 受理 canonical Turn：身份由 `ResolveCanonicalTurnIdentityAsync` 显式给出——sub-agent 结果的会话归属取持久父身份（`parent_conversation_id/parent_session_id/parent_session/conversation_id`），解析失败即 retry/dead-letter，绝不回退 `profile.MainSessionId`、绝不落 `msg-*`；其 `client_request_id/client_message_id` 均由确定性 resultId（`claimed.MessageId`）派生（`fabric-subagent-result*`，不含 deliveryId），与 acceptance 层 `(workspace_id, client_request_id)` 幂等键两层同源；受理成功即 ACK；父/子执行身份（`parent_turn_id/parent_command_id/child_run_id/result_id`）按白名单透传进 turn metadata；`notify` 按 workspace/Agent 跨 room 一次领取最多 20 条，逐条写 Conversation 消息事实后 ACK，Busy 时也可排空且不唤醒模型；Busy/foreground heartbeat ACK/drop；恢复扫描 claim=null 时淘汰无 durable row 的 stale target，避免每 10 秒永久 `no_claim` |
| `Tools/BuiltIns/Messaging/SendMessageTool.cs` | Agent 发消息；默认 `intent=inform, requires_response=false`，只有 ask/request_review/delegate 创建对方执行；未知 intent fail closed，终态回复由平台一次性投影 |
| `Services/Messaging/AgentExecutionAdmissionCoordinator.cs` | workspace/agent 级前后台准入协调器；用户 Turn/Connector handoff 形成 foreground demand，抢占活动后台投递并阻止 recovery/idle drain 抢跑 |
| `Services/AgentExecution/AgentToolArguments.cs` | tool-call JSON → 参数转换 |
| `Services/AgentLoop/CanonicalWorkReport.cs` | 子代理五段报告解析/校验；无 native tool call、非结构化响应且完整满足 canonical 合同时同轮提升 DONE，显式结构化 CONTINUE 不被覆盖 |
| `Services/AgentLoop/AgentOutputTruncationPolicy.cs` | provider `length/incomplete` 输出的有界恢复策略；仅允许一次“不重放 reasoning、立即工具行动”的短恢复，再次截断显式失败 |
| `Services/GoalMode/` | 🆕 Goal 模式 v2 执行器 |
| `Services/TurnExecutorAdapter.cs` | Turn 执行适配器；用户 Turn 获取 foreground admission，Busy 等待采用 100ms→1s 有界指数退避与 10 秒节流日志；透传 canonical TaskPlan/TaskNode/ParentNode identity 到 RuntimeDispatchRequest |

## 上下文管线

2026-09-15：`AgentSessionManager` 的可见工具有序投影贯穿 `BuildFrozenToolManifest`、Streaming/Buffered 发现边界和 Composition 恢复。`ContextPipelineLayers` 拆分稳定规则与可变目录，`ContextPipelineOrchestrator.BuildCatalogUpdate` 按模型可见历史的最新完整目录去重，更新仅追加 User tail，固定记忆裁剪不受目录去重影响。见[修复记录](../../Docs/Reports/主代理缓存前缀修复-2026-09-15.md)。

| 文件 | 用途 |
|------|------|
| `Services/ContextPipeline.cs` | 🔑 上下文组装管线；区分执行 `AgentInstanceId` 与持久 `ConfigurationAgentInstanceId`，私有 Skill/人格/记忆/日志只读稳定身份；稳定 system prefix 与本轮 User tail 分离；已在模型可见历史中的完全相同 L6 recall 不再重复注入，召回变化或历史被压缩时仍正常追加；Tool 层强制 Direct/Delegated 判定与前三次调用委派合同；L1 TOOLS 层索引文本从 session 已加载工具集合（append-only）生成（Core ∪ Loaded 不收缩），消除每轮全量重建导致的 prefix 漂移；Skills 层只在 `search_tools` 实际可见时声明可用递延发现；已拆为 `ContextPipelineLayers.cs`（层装配）与 `ContextPipelineOrchestrator.cs`（编排执行）两个 partial |
| `Services/Skills/AgentSkillFileService.cs` | Agent 私有 Skill 文件服务；缺失索引的 Get/List 为无副作用空读取，只有显式初始化或写操作创建目录 |
| `Services/ContextWindowManager.cs` | Token 窗口管理；DB/JSONL 回填与内存裁剪已从扁平截断改为 `ContextTierPlanner` 分级填充（T0 全保 → T4 先弃，保新弃旧）；memory DB 冷水合先在 session 压缩锁内同步 canonical `ChatMessages`，同步不可用时 fail-closed；按稳定 turn/message 身份排除当前 Turn，避免与围栏输入重复；非空 live history 不被 assistant 投影前的 DB 快照覆盖，自动压缩刷新只合并 opening/closing 与 64 位 hash 完整的 live 当前轮，禁止把无围栏历史 user 提升为当前 Turn；JSONL 冷启动路径经 `CompactionCoverageFilter` 过滤已压缩消息 |
| `Services/CanonicalChatTranscriptSynchronizer.cs` | platform `ChatMessages` → memory `Messages` 的共享增量同步器；session metadata 持久高水位、稳定 `chat-{session}-{platformId}` 恢复兜底，按 256 条分页幂等追平并越过非语义空行；保存 canonical turn/message 身份，正文与 typed parts 共同参与 hash，供压缩与冷水合共同使用 |
| `Services/CompactionCoverageFilter.cs` | 压缩覆盖过滤器；加载 session 最新 `CompactionCoverageManifest`（SourceMessageIds/SourceHashes）为覆盖集合，供 JSONL 冷启动路径去重；null factory / 无 manifest / 非法 JSON 均 no-op；P1-2 起亦供 `SubconsciousRecallPipeline` 管道内 covered 过滤（hash 命中覆盖集合 → 丢弃 recall 片段） |
| `Services/ContextAssemblyService.cs` | 上下文装配 |
| `Services/ContextBudgetAllocator.cs` | 预算分配 |
| `Services/ContextCompactionService.cs` | 压缩服务；压缩前调用共享 canonical 转录同步器；若当前有围栏 Turn 或最后一个未围栏 user 消息落入压缩候选，`CurrentTurnCompactionGuard` 在任何摘要/数据库写入前 fail-closed 并返回 `current_turn_in_compaction_scope`；active 消息按页全量读取，80 条仅作为 Map-Reduce 块大小；所有待压缩消息进入 map 输入并通过覆盖校验后才写 `CompactedBy`；同一事务写 `CompactionCoverageManifests` 覆盖清单（OmittedCount==0 门禁）与 session 递增 `CompactionGeneration`（Source/TargetGeneration） |
| `Services/ContextHealthEvaluator.cs` | 健康评估 |
| `Services/SystemPromptBuilder.cs` | 系统提示构建（24KB） |

## LLM 调用

| 文件 | 用途 |
|------|------|
| `Services/DirectLlmClient.cs` | 🔑 直接 LLM 客户端；只按选中模型 protocol 路由；Provider 成功后以共享 ActivityId 必达写入逐请求 usage 账本；operation 按 `chat[:approval|:compaction]` 区分任务数据面与控制面；流式路径分别记录 rate-limit wait 与 provider first-chunk wait；V5 视觉单源化：构造可选注入 `FrozenVisionContextAccessor`（:38/:56/:76），`supportsVision` 只读冻结快照 `frozenRoute?.SupportsVision ?? false`（:863-864，无冻结上下文 fail closed，删除原热目录 CapabilityTags 二次判定），三个 Gateway（Responses/OpenAI/Anthropic）注入快照 `VisionPolicy`（:879） |
| `Services/FrozenVisionContextAccessor.cs` | 🆕 V5 视觉冻结上下文通道（AsyncLocal）；`Current` / `Push(LlmRouteSnapshot?)` 返回 IDisposable scope（:14/:29），Buffered 入口写入；Streaming 每次 LLM MoveNext 通过 `MoveNextAsync` 重绑快照并恢复调用方上下文，避免 yield 后丢失；DirectLlmClient 读取；无上下文 = null = fail closed |
| `Services/CompositionSnapshot.cs` | 前缀缓存归因：逐请求计算 systemPromptHash/toolSpecHash/prefixHash（SHA-256 小写 hex）与 compositionVersion（进程内按 session 递增） |
| `Services/SqliteCompositionStore.cs` | 🆕 P0-5 `ICompositionStore` SQLite 实现：落 `CompositionSnapshots` 表（MemoryDbContext 同库），append-only（版本严格递增，重写/乱序抛 InvalidOperationException）、写穿、GetLatest 取最大版本 |
| `Services/LlmInvocationService.cs` | LLM 调用编排；把模型配置解析出的 protocol 传给 Direct/Controller 路径，并以 invocation purpose scope 透传非模型可见计费归因 |
| `Services/LlmInvocationPurposeAccessor.cs` | AsyncLocal LLM purpose scope；默认 `agent`，嵌套调用完成后恢复，供 provider ledger 区分 approval/compaction |
| `Services/LlmProfileResolver.cs` | Profile 解析 |
| `Services/LlmRequestBudgetGuard.cs` | 预算守卫 |
| `Services/WarmPrefixCompaction.cs` | 长循环压缩计划与 checkpoint 合同；复用当前 warm prefix，固定尾部摘要指令，只接受真实缩小结果 |
| `Services/ProviderRateLimiter.cs` | Provider/model 并发速率限制；租约携带等待时长与 acquire 前后可用槽位的只读诊断 |
| `Services/Diagnostics/RuntimeDiagnosisEngine.cs` | 🆕 确定性诊断引擎（服务 `agent_diagnostics action=diagnose`）：纯函数、无 I/O、无时钟，把工具/缓存/上下文/子代理四维聚合量转成带证据的 findings（code/severity/category/observation/evidence/suggested_action）。三条诚实性硬规则：①无证据（或所有检查因样本不足跳过）判 `unknown`，绝不判 `healthy`；②`checks_skipped` 逐条记录被跳过的检查及原因；③四个数据源未全通时 `coverage.scope=partial` 且不得声称整体健康。阈值：失败率 0.20/0.50、错误集中度 0.60、耗时离群 3× 中位数、缓存命中 0.50/0.20、上下文占用 0.80/0.92、子代理失败率 0.25、最小样本 5 次 |

## 工具系统

| 文件 | 用途 |
|------|------|
| `Tools/BuiltIns/` | 内置工具（Git 20 工具在此） |
| `Tools/BuiltIns/Llm/ListLlmProvidersTool.cs` | `list_llm_providers` LLM 路由表查询；数据来自 ILlmConfigService 内存快照（llm.providers.json），输出 providerId/modelId/route/protocol/capabilityTags/价格/isEnabled/isDeprecated 与 ambiguous_model_ids 歧义清单（与 FileLlmResolver 裸 modelId 解析语义一致）；严禁输出 apiKey/baseUrl；已入 ToolExposurePlanner.CoreToolIds 常驻可见，spawn_sub_agent 描述同步指向 |
| `Tools/BuiltIns/Search/SearchGrepTool.cs` | 代码文本搜索（ADR-089 U0，含 R1–R4 返工与 G2 glob 统一）；Lucene **仅产出去重候选路径**并按存在性/scope/完整 glob/扩展名/排除五重准入，随后读取**当前内容**重新匹配（不输出索引旧文本、不预填去重键）；候选与枚举共用 `processedFiles` 与 `RetrievalMatcher`；**入口唯一 deadline**（默认 10s 可注入）覆盖候选/枚举/扫描，行间检查取消，正则超时 → `Timeout` 绝不落 `no_match`；覆盖完整性 = `errors==0 && 子目录枚举失败==0 && !timedOut && !regexTimedOut && !enumerationTruncated && !scanBudgetExceeded && !maxResultsReached && !totalCapReached && skippedLargeFiles==0`；`max_results` 统一作用于**合并后结果集**（恰好达上限且自然结束仍为 Complete）；排除目录在枚举前裁剪；默认额外排除 `.pudding`，结果默认 20 条/16 KiB。**G2**：glob 判定统一走 `PuddingCode.Tools.Retrieval.RetrievalGlobMatcher`（`ignoreCase: true`，Windows First）；候选与枚举**共用同一准入谓词** `IsAdmissiblePath`（存在性/scope/排除目录/glob/扩展名）；枚举不再把 glob 交给 Win32——目录内取全部文件后由 matcher 过滤，`MaxEnumeratedFiles` **只统计命中文件**（matched-only，杜绝假截断）；含分隔符 glob 按 cwd 相对路径匹配且 `*` 不跨 `/`；扫描循环内重复扩展名过滤已删除 |
| `Tools/BuiltIns/Files/FileSearchTool.cs` | 文件名搜索工具（ADR-089 U0-S3 + **G3 glob 统一**）；Everything 清单不可自证时差分补足内置枚举——`no_match` 仅在覆盖 Complete 时输出，非 Complete 附恰好一次覆盖声明；auto 模式降级显式声明 fallbackFrom/fallbackReason。**G3**：文件级过滤统一由 `FileSearchPatternMatcher` 承担并二分——含通配符走共享 `PuddingCode.Tools.Retrieval.RetrievalGlobMatcher`（`ignoreCase: true`，`*`/`?` 不跨 `/`），**不含通配符保留大小写不敏感子串包含（not-glob 契约，U1 拆参迁移点）**；本地 `GlobLikeMatch`（`*` 跨 `/` 旧语义）与 `**/` 剥离正则已删；legacy `BuiltInRecursiveFileSearchProvider.SearchAsync` 改为 `"*"` 全量枚举 → matcher 过滤 → `Take`（**先过滤再截断**，消除 Win32 前导匹配怪癖 `*.txt` 命中 `a.txtx` 与截断漏文件）；零调用方的 provider 级 `ToDirectorySearchPattern` 及专属 `using System.Text.RegularExpressions` 已删除 |
| `Tools/BuiltIns/Git/GitCommitTool.cs` | git_commit 提交工具；files 数组反序列化兼容 `string` 与 `string[]`（`StringOrStringArrayConverter`） |
| `Tools/BuiltIns/Files/FileChunkService.cs` | Runtime 文件工具的大文件分块/流式读取服务；不再反向依赖 Platform |
| `Tools/BuiltIns/Diagnostics/AgentDiagnosticsTool.cs` | Agent 自我诊断工具，十种 action：tool_stats / slowest_tools / cache_health / sub_agent_stats / compaction_stats / latency_breakdown / token_breakdown / entropy_probe / context_health / **diagnose**；`diagnose` 先采集四维聚合量（活动流一次查询后内存分组、缓存按会话、上下带走 capacity+health、子代理带 HoursBack）再交给 `Services/Diagnostics/RuntimeDiagnosisEngine` 做确定性判定，任一数据源不可用时以 `source.unavailable` + `checks_skipped` 显式报出，而非当作无问题 |
| `Tools/BuiltIns/Management/BootstrapRebootTool.cs` | `bootstrap_reboot` 点火遥控；默认请求 Desktop `desktop-build` 构建+事务部署+哈希校验，也支持 `prebuilt-artifact` 交付 Agent 已编译产物与显式 `restart-only` |
| `Tools/BuiltIns/SmartWorkflow/` | 七个角色化 Smart 入口；统一 `task` schema、历史参数归一化、子代理报告校验；`SmartWorkflowToolBase.cs` 校验失败时 partial-salvage：附验证说明后原样返回子代理实际产出，父 Agent 仍可用 |
| `Tools/Platform/` | 平台工具实现 |
| `Tools/Platform/ToolInvocationService.cs` | 统一调用入口；模型 callId、执行身份、CapabilityPolicy、deadline、剩余 usage budget 与 delegated cumulative usage 的双向传递边界；Harness 别名和参数在 RuntimeControl/WorkspaceGuard/Firewall/哈希/执行前归一化；目标协议要求 callId 进入 Registry 后保持不变 |
| `Tools/Platform/HarnessToolCompatibilityAdapter.cs` | `rg/exec_command/write_stdin/read_file/write_file/list_directory/apply_patch/pwsh/WSL` 的窄范围 deterministic 兼容；保持 canonical 工具唯一并识别搜索 exit 1 no_match；统一入口记录 requested/canonical/adaptation/version 遥测 |
| `Tools/Platform/ToolLoopInstructionBuilder.cs` | 按当前真实可见 descriptor 生成稳定工具循环指引；只有 `search_tools` 可见时才宣称可发现 deferred tools |
| `Tools/Platform/PuddingToolRegistry.cs` | Tool Registry、LLM schema 投影、AgentFirewall 门控与统一执行服务；canonical output/结构化错误/分阶段执行管线的主要改造入口 |
| `Services/Tools/` | 工具运行时服务 |
| `Services/Tools/ToolExposurePlanner.cs` | Provider 无关的工具暴露规划；名称稳定排序，超过阈值时保留核心工具并通过 `search_tools` 激活能力；当前 provider request 冻结，已发现定义在下一 LLM round 单调生效（不是下一外部 Turn），由 AgentSessionManager 在 live session 内 append-only 保持 |
| `Services/TerminalProcessManager.cs` | 终端进程管理 |
| `Services/TerminalSecurity.cs` | 终端安全 |
| `Tools/BuiltIns/Terminal/TerminalTools.cs` | terminal_start/wait/read/status/cancel/input 六件套；`terminal_wait` 阻塞语义（2026-08-22 能耗修复）：等到任务退出或输出超过预览上限才返回，wait_seconds 0-600 默认 60，禁止短等待轮询（旧轮询语义曾占全库 16% token） |
| `Tools/Approval/JevToolApprovalReviewer.cs` | 🔑 Jev 决策模型驱动的审批评审器（`IToolApprovalReviewer` 可切换实现，取代 LLM 审批评审器；开关 `ToolApproval:Reviewer=jev`，未配置时容器已注册 `IJevDecisionService` 即自动选 jev，否则旧行为 llm）：一次 round trip 四问（decision/risk/scope/allowlist）共享 state（argumentsJson 截断默认 8KiB）；I1 确定性 deny 前置——命中 `ToolApprovalCommandFirewall` 危险模式绝不调 Jev（`jev_skipped_deny_rule`）；I2 不可用/超时/坏答案一律 `DeferredDependency`（fail-closed，`jev_unavailable`/`jev_invalid_response`）；I3 白名单提案仅精确匹配（含 `; & \| > < $ `` ( ) { } * ? ~`、反斜杠、换行即拒绝提案）；I4 校准概率低于阈值（默认 0.90）不提案；I5 提案 Reason 携带模型/概率/风险 provenance。配套 `Tools/Approval/JevToolApprovalOptions.cs`（节 `ToolApproval:Jev`：Enabled/AllowlistProbabilityThreshold/StateTruncateBytes=8192/ReviewTimeoutSeconds=30/MaxQuestions）；注册点 `Tools/Platform/PuddingToolServiceCollectionExtensions.cs` 工厂；离线测试 `PuddingRuntimeTests/Tools/JevToolApprovalReviewerTests.cs` |

## 子代理 & 计划

| 文件 | 用途 |
|------|------|
| `Services/SubAgentInvocationService.cs` | 子代理调用；继承父剩余 usage budget，批量任务等分预算（诚实除法+派生标记，除后份额过可执行性判据，不可行拒绝整批 sub_agent_batch_budget_infeasible），返回同步 child 累计 usage，并把公开 `resume_sub_agent_id` 映射为稳定 SubSessionId 续跑 |
| `Services/AgentLoop/SubAgentBudgetLifecycle.cs` | 子代理预算状态机：启动/80%/50% 通知、10-50 轮收尾宽限、可恢复终止判定 |
| `Services/DesignCouncilRuntimeService.cs` | MOA 运行时适配器；精确 provider/model 路由、可见性裁剪、只读派发、结果回填与暂停输入 |
| `Services/InMemorySubAgentOrchestrationRunStore.cs` | 进程内 MOA run 快照 store；Version CAS 防止重复 claim，不支持跨重启恢复 |
| `Services/Orchestration/AgentOrchestrationWorkerService.cs` | 通用编排 Runtime worker；领取已注册 executor 的 Ready 节点，90 秒续租 5 分钟 claim，以 fence 提交按端口输出/真实 child 身份并原子推进后继与 Run 终态 |
| `Services/Orchestration/AgentOrchestrationNodeInputResolver.cs` | 从冻结 Graph Inputs 与上游 `outputs[sourcePortId]` 解析节点输入；当前显式支持 `$`、Replace/Append、inline text 与 Artifact 列表，拒绝未实现 sourcePath/targetKey |
| `Services/Orchestration/SubAgentOrchestrationNodeExecutor.cs` | 只读 `pudding.agent.subagent` executor；冻结 role/template/provider/model，复用 `ISubAgentInvocationService` 与系统预算，提交 `result` 文本和 child Run/SubSession；用 workspace/graph 派生安全 archive owner，不把审计主体当目录 |
| `Services/Orchestration/ImageGenerateOrchestrationNodeExecutor.cs` | `pudding.media.image-generate` executor；经共享 resolver 读取 prompt/参考图，复用 `IImageGenerationService`，使用稳定 paid-call idempotency key，并把 Artifact 列表写入 `outputs.images` |
| `Services/Orchestration/ImagePreviewOrchestrationNodeExecutor.cs` | `pudding.media.image-preview` executor；经共享 resolver 读取上游 `images` Artifact 列表，并以同一引用写入自己的 `outputs.images`，不复制或内联图片 bytes |
| `Services/SubconsciousRecallPipeline.cs` | 潜意识召回管道（25KB）；P1-2：`SearchHit` 携带 `CanonicalContentHash/SourceMessageId`，注入前经 `CompactionCoverageFilter` 过滤 covered 片段 + 同轮内 hash 去重（同一 source hash 的多个 chunk 只注入 1 条） |
| `Services/SubconsciousPlanGenerationService.cs` | 计划生成 |
| `Services/TaskPlanning/` | 任务规划 |

## 任务工具（TaskTools）

| 文件 | 用途 |
|------|------|
| `Services/TaskTools/TaskListTool.cs` | `task_list` 工具（按 workspace/过滤列查询）|
| `Services/TaskTools/TaskGetTool.cs` | `task_get` 工具（单任务查询）|
| `Services/TaskTools/TaskClaimTool.cs` | `task_claim` 工具（领取任务；ActiveTask 丢失时经服务端反查归属安全重建上下文，缺陷 3f8df399）|
| `Services/TaskTools/TaskUpdateTool.cs` | `task_update` 工具（状态迁移/disposition；ActiveTask 丢失时同上重建，须 InProgress 或 Blocked（卡 813ad427）；Blocked 下仅 `todo`→Ready 合法，其余由服务端 fail closed）|
| `Services/TaskTools/ManageTasksTool.cs` | `manage_tasks` 工具（管理者视角跨 Agent 看板 CRUD + 命令；list 支持 children_of/include_child_summary；create/update 支持 parent_task_id、depends_on_task_ids（看板卡依赖，fail-closed 错误码 task.dependency_invalid / task.dependency_task_not_found）、task_type（可选透传，不传=general，小写归一 ≤64 字符）；get 返回 dependencies/dependency_tree，include_children=true 时内联子卡）|
| `Services/TaskTools/TaskToolModels.cs` | 工具参数/结果模型 + `TaskToolErrors` + `TaskToolGuard`（`ValidateActiveTaskOrRebuildAsync`：ActiveTask==null 时按 mine 归属+assignment 匹配+状态门槛+版本 CAS 重建等效上下文；注入路径不做 expected_version 快照比对（缺陷 2d5a2ebe，服务端活版本 CAS 唯一裁决）；重建失败时在 `task.active_context_missing` 上附加非泄露诊断 `context_rebuild{attempted,stage,outcome}`——inputs/incomplete_inputs、lookup/not_visible、ownership/agent_mismatch（卡 3133b149）；卡 813ad427（2026-09-14 裁定）：update 路径状态门槛放宽为 `InProgress｜Blocked`（仍需 active assignment 归属调用方 + 版本 CAS），disposition 合法性仍由服务端状态机 fail closed 裁决，裁定与测试见 `Docs/Reports/blocked-recovery-channel-decision-20260914.md`）|

## 记忆 & 知识

| 文件 | 用途 |
|------|------|
| `Services/MemoryWriteCoordinator.cs` | 记忆写入协调 |
| `Services/UserPreferenceService.cs` | 用户偏好管理：Prefetch 会话启动注入 System Prompt + save_preference 工具 Sync 存储 |
| `Services/KnowledgeAccessRuntime.cs` | 知识访问 |
| `Services/AgentLogRecallService.cs` | 日志召回 |
| `Services/SessionChunkIndexer.cs` | P1-2 会话块向量索引写侧：索引时回查 Messages 补齐 `CanonicalContentHash/ContextGeneration` 冗余列（T2），查不到时对 SourceText 现算 SHA-256 兜底，幂等语义不变 |
| `../PuddingMemoryEngine/Data/MemoryLibrary.cs` | P1-2 第 5 路召回查询侧：`SearchSessionChunksByVectorAsync` 同库 LEFT JOIN Messages 取 hash/generation/CompactedBy，默认过滤 covered chunk（`includeCovered=false`），返回专用 DTO `SessionChunkRankedResult`（含 MessageId/hash/generation/IsCovered） |
| `../PuddingCore/Abstractions/IMemoryRecallService.cs` | P1-2 契约：`RecalledMemory` 新增 `SourceMessageId/CanonicalContentHash/ContextGeneration`（可空向后兼容），chunk-vector 路召回项透传溯源元数据供 assembler 同源去重 |

## 多媒体

| 文件 | 用途 |
|------|------|
| `Services/DashScopeAsrProvider.cs` | 语音识别 |
| `Services/DashScopeTtsProvider.cs` | 语音合成 |
| `Services/VolcengineArkImageGenerationProvider.cs` | 图片生成 |
| `Services/JevDecisionService.cs` | 🔑 Jev 决策模型适配器（POST `{baseUrl}/api/v1/decide`，Bearer 鉴权）：返回**结构化决策数据**（choice/score/noul + probabilities + confidence + usage），不做自然语言解析；非 2xx fail-closed 抛 `JevDecisionException`（错误体截断 ≤4096 字符），502/503/504 按 `MaxRetries` 指数退避重试；端点/密钥/模型经 `IJevDecisionOptionsProvider` 每次调用前解析，密钥不入日志 |
| `Services/JevDecisionOptionsProvider.cs` | Jev 连接参数解析默认实现——**资源池优先**：端点取 `llm.providers.json` 的 provider `jev`.baseUrl，模型取该 provider 下首个未废弃模型（按 sortOrder），密钥链为 池 `apiKeyRef`→KeyVaultId 经 KeyVault → **池 `apiKey`（支持 `${ENV}` 占位）** → `Jev:ApiKey`/`JEV_API_KEY` → `Jev:ApiKeyRef`；逐项回退 `Jev` 配置节与环境变量；未配置时抛 `jev.not_configured`，绝不返回空端点。换解析来源只需注册另一个 `IJevDecisionOptionsProvider` |
| `Services/ManagedOggOpusTranscoder.cs` | 音频转码 |

## 会话 & 事件

| 文件 | 用途 |
|------|------|
| `Services/AgentSessionManager.cs` | 会话管理 |
| `Services/CompositionRecoveryService.cs` | P0-5 步骤 5：跨 1h 超时/Core 重启从持久化 Composition 水合工具集合（append-only） |
| `Services/SessionExecutionGate.cs` | 执行门控 |
| `Services/SessionArchiver.cs` | 会话归档 |
| `Services/HeartbeatService.cs` | 会话超时资源清理（**不是** Agent 自主心跳编排） |
| `Tools/BuiltIns/Agents/AgentStatusTool.cs` | Agent 状态只读诊断；优先返回持久 Availability version/reason/active Task/Goal/SubAgent，投影缺失或过期报告 unknown，不从 wake queue 缺席推导 idle |
| `Services/AgentInvocationDispatchFactory.cs` | 服务端 message metadata → Runtime dispatch；`WorkspaceAgentInvocation.ParentConversationId` 与 `ResolvePersistedParentConversationId` 固化父身份键优先级 `parent_conversation_id→parent_session_id→parent_session→conversation_id`，stream dispatch 按 显式父身份→元数据→事件 session→主会话 解析并输出 `sessionSource`，缺失时抛错而非伪造 `msg-*` 会话；Task-bound Goal 透传 task/assignment/version 与 reservation fencing token 到 ActiveTask |
| `Services/AgentWakeQueue.cs` | 唤醒队列（内存态，重启即空）。优先级键 = `LatestWakeAt`，而“是否到期”由 `EarliestWakeAt` 判定；`TryDequeueAsync` 整体扫描取已到期且最早者，**不得只判队首**（否则队首未到期会阻塞其后已到期条目）。自定义 `sleep` 标记在出队时清除 |
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

对应测试项目：`../PuddingRuntimeTests/` — Agent Loop、上下文管线、语音/图片 Provider；SubAgent/输入 resolver/图片生成/图片展示编排定向测试 4/4 ✅；list_llm_providers 工具合同测试（歧义/过滤/敏感字段/路由可解析）7/7 ✅

## 上下文压缩生命周期事件的活性契约（2026-09-19）

**症状**：未触发压缩却出现压缩 UI——会话内卡片显示「正在压缩上下文」、顶部 toast 不消失、状态文案长期停在压缩中，turn 显示「已运行 60m+」，而上下文用量远低于自动压缩阈值。

**根因**：压缩 UI 只由一个前端状态驱动（一个 `compaction:<id>` 生命周期 turn，assistant.status='executing' 且时间线含 `status='compacting'`），它只在 `context.compaction.started` 创建、只被携带同一 `compactionId` 的终态事件清除，中间没有任何活性保证。

| 文件 | 职责与边界 |
|------|------------|
| `src/pages/chat/utils/chatStateUtils.ts` | `resolveRunningCompactionId(events)`：按顺序取最后一个压缩生命周期事件，**仅当它是带非空 id 的 `started`** 才返回该 id。bootstrap 的 `lifecycleEvents` 不区分压缩是否仍在运行，判活必须由前端自查；payload 可能是对象/JSON 字符串/已展平事件，解析异常一律返回 null（宁可不点亮，不误报运行中） |
| `src/pages/chat/hooks/useCompaction.ts` | ① `replay===true` 且 started 的 id ≠ `runningCompactionId` → **整条忽略**（不建 turn、不 `setLoading`、不 `setCompactionStatus`、不弹 toast）；重放的 started 即使命中判活也**永不弹 toast**，只有实时 SSE 才弹。② `COMPACTION_LIVENESS_TIMEOUT_MS`（10min）活性 TTL：点亮运行态即挂表，超时由 `convergeStaleCompactions` 把仍为 executing 的压缩 turn 收敛为「压缩未完成（无终态记录）」并同步收敛 lifecycle map 副本（防 merge 复活）、清 loading/状态文案、destroy toast；终态事件/`resetCompaction`/卸载时清表。禁止「`duration: 0` 弹了就不管」 |
| `src/pages/chat/hooks/useSessionEventReplay.ts` | bootstrap 重放：先算 `runningCompactionId`，再以 `{allowSessionSwitch:false, notify:false, replay:true, runningCompactionId}` 逐事件下发；缺口重放与历史尾部重放一律 `applySessionEvent(event, {replay:true})`——历史 started 一律不点亮，真在跑的压缩由 live SSE 补亮（已知取舍：SSE 断档期间恰逢压缩启动时会出现漏亮，不再出现假运行态） |
| `src/pages/chat/hooks/useSessionEventProjection.ts` | `applySessionEvent(ev, options?)` 仅把 `replay` 语义透传给压缩三个事件的分发，其余投影行为不变 |
| `PuddingRuntime/Services/ContextWindowManager.cs` | Auto 压缩三态（started/completed/failed）必须携带**同一 compactionId**：`compactionId` 提升到 try 之外声明，catch 的 `context.compaction.failed` payload 此前漏发 id（异常发生在 id 赋值前时为 null） |
| `PuddingRuntimeTests/Services/ContextWindowManagerTests.cs` | `TrimHistoryAsync_FailedCompaction_EmitsFailedEventWithCompactionId`：断言 started→failed 顺序且 failed payload 携带与 started 相同的 compactionId |
