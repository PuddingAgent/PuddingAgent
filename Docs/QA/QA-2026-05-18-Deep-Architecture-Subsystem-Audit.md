# PuddingAgent 深入架构子系统审计报告

审计日期：2026-05-18  
审计范围：上下文合成引擎、LLM 执行引擎、会话层、潜意识 LLM、记忆图书馆、事件系统、子代理系统、网关和连接器。  
审计目标：识别当前架构中的一致性、可靠性、可恢复性、安全边界和扩展性风险，并提出可执行的改进顺序。

## 1. 总体判断

当前项目已经形成了较完整的 agent runtime 架构：请求进入连接器或平台 API 后，经过事件系统、会话状态、执行引擎、上下文合成、记忆检索与子代理协作，最终回流到会话事件流和外部连接器。整体方向是合理的，但目前存在几个系统性问题：

1. 会话事件和子代理状态的持久化边界不稳，启动期 DDL 存在破坏性删除。
2. 会话流式输出有两套并行机制，导致 Chat API 路径和事件/连接器路径行为不等价。
3. 子代理系统存在重复实现和重复发布状态的风险。
4. 事件系统接口承诺持久队列，但实现仍是内存队列。
5. LLM 同步路径与流式路径配置不一致，且同步路径绕过统一网关。
6. 上下文合成、潜意识召回和记忆图书馆已经具备方向，但 token 预算、历史摘要、FTS 读取和候选集裁剪仍偏脆弱。

这些问题会在长会话、多 connector、多 agent 并发、重启恢复和生产安全边界下放大。建议先修复 P0 级一致性和数据保留问题，再推进 P1 级可靠性与扩展性改造。

## 2. 关键发现

### P0：启动时会丢失会话事件和子代理状态

涉及文件：

- `Source/PuddingAgent/Program.cs:656`

观察到 `pendingTableDdl` 中包含：

```sql
DROP TABLE IF EXISTS session_event_log;
DROP TABLE IF EXISTS session_sub_agents;
```

影响：

- `session_event_log` 按设计应承担 append-only 会话事件日志职责，启动期删除会破坏审计、回放、恢复和前端断线重连语义。
- `session_sub_agents` 保存子代理追踪状态，启动删除会导致子代理历史和运行状态丢失。
- 这会使会话层、事件系统和子代理系统的持久化承诺失效。

建议：

1. 立即移除启动期 destructive DDL。
2. 使用 EF migration 或显式 `CREATE TABLE IF NOT EXISTS` 维护 schema。
3. 对所有启动期 schema 变更加保护测试，禁止出现 `DROP TABLE IF EXISTS session_event_log`、`DROP TABLE IF EXISTS session_sub_agents`。
4. 如果确实需要开发环境重建表，应放入单独的开发工具命令，不能在应用启动路径执行。

### P0：会话流式层存在两套通道

涉及文件：

- `Source/PuddingPlatform/Controllers/Api/ChatApiController.cs:30`
- `Source/PuddingPlatform/Controllers/Api/ChatApiController.cs:180`
- `Source/PuddingRuntime/Services/AgentExecutionService.cs:880`
- `Source/PuddingPlatform/Services/SessionStateManager.cs`
- `Source/PuddingPlatform/Controllers/Api/SessionEventsController.cs`

当前状态：

- `ChatApiController` 仍直接依赖旧的 `SessionEventHub`。
- `AgentExecutionService.ExecuteStreamAsync` 走 `ISessionStateManager` 写入流式事件。
- `SessionEventsController` 又围绕 `ISessionStateManager` 提供事件读取和 SSE。

影响：

- Chat API 路径与连接器/事件系统路径可能产生不同的 session event。
- 客户端如果订阅 SSM 事件流，可能看不到 `ChatApiController` 旧 hub 路径完整事件。
- 执行引擎、控制器和 SSM 的职责边界不清，后续引入断线续传、回放、checkpoint 会变困难。

建议：

1. 将 `ISessionStateManager.AppendAsync` 定为唯一会话事件写入入口。
2. `ChatApiController` 不再直接写 `SessionEventHub`，改为调用统一执行服务并订阅/返回 SSM frame。
3. `AgentExecutionService` 只负责生成标准 frame，不直接承担多种 UI/connector 适配。
4. 旧 `SessionEventHub` 进入兼容层或删除计划，避免新增路径继续依赖它。

### P0：子代理系统重复实现，存在重复完成通知风险

涉及文件：

- `Source/PuddingRuntime/Services/Skills/SubAgentTool.cs:77`
- `Source/PuddingPlatform/Services/SubAgentManager.cs:15`
- `Source/PuddingRuntime/Services/Skills/SubAgentTool.cs:263`
- `Source/PuddingRuntime/Services/Skills/SubAgentTool.cs:272`
- `Source/PuddingRuntime/Services/Skills/SubAgentTool.cs:283`
- `Source/PuddingRuntime/Services/SubAgents/MemoryExplorerSubAgent.cs`
- `Source/PuddingRuntime/Services/Tools/QuerySubAgentsTool.cs`

当前状态：

- `SubAgentTool` 自己实现 spawn、上下文组装、执行、完成跟踪和事件发布。
- `SubAgentManager` 也实现子代理追踪、创建、状态更新和事件发布。
- 完成路径中可能同时调用 `TrackSubAgentCompleteAsync`、`AppendAsync(SubAgentCompleted)` 和 `PublishAsync(agent.sub_completed)`。

影响：

- 子代理状态可能重复完成、重复通知或状态顺序不稳定。
- 子代理能力扩展时需要同时理解 tool 和 manager 两套逻辑。
- 后续做取消、超时、重试、资源限制和权限隔离时缺少单一控制面。

建议：

1. 保留 `ISubAgentManager` 作为唯一权威入口。
2. `spawn_sub_agent` tool 只做参数校验和调用 manager，不直接执行子代理生命周期。
3. 子代理完成、失败、取消、超时统一由 manager 写 SSM 并发布内部事件。
4. `QuerySubAgentsTool` 只读 manager/SSM 状态，避免另建状态源。

### P1：事件系统接口声明持久队列，但实现是内存队列

涉及文件：

- `Source/PuddingRuntime/Services/Events/IPriorityEventQueue.cs:7`
- `Source/PuddingRuntime/Services/Events/PriorityEventQueue.cs:18`
- `Source/PuddingRuntime/Services/Events/PriorityEventQueue.cs:114`
- `Source/PuddingRuntime/Services/Events/InternalEventBus.cs`
- `Source/PuddingRuntime/Services/Events/EventPreprocessor.cs`
- `Source/PuddingRuntime/Services/Events/EventDispatcher.cs`
- `Source/PuddingAgent/Services/Events/EventIngressBridge.cs`
- `Source/PuddingAgent/Services/Events/AgentEventHandler.cs`

当前状态：

- 接口注释描述 SQLite 持久化优先级队列。
- 实现使用内存 `Queue<T>`。
- `UpdateStatusAsync` 仅记录日志，没有真实持久状态更新。

影响：

- 进程重启会丢失未处理事件。
- 无法可靠支持 lease、retry、dead-letter、幂等处理和 backpressure。
- connector 入口与 agent event handler 之间缺少可恢复缓冲。

建议：

1. 新增 SQLite-backed event queue 实现，保留内存实现仅用于测试。
2. event record 至少包含 `event_id`、`source`、`type`、`priority`、`payload`、`status`、`attempts`、`available_at`、`lease_until`、`created_at`、`updated_at`。
3. `DequeueAsync` 应获取 lease，处理失败后按 retry 策略回队列，超过阈值进入 dead-letter。
4. 对外接口文档与实际实现保持一致，避免接口承诺和运行时行为分裂。

### P1：LLM 执行引擎同步与流式路径不一致

涉及文件：

- `Source/PuddingRuntime/Services/AgentExecutionService.cs`
- `Source/PuddingRuntime/Services/DirectLlmClient.cs:67`
- `Source/PuddingRuntime/Services/DirectLlmClient.cs:148`
- `Source/PuddingRuntime/Services/DirectLlmClient.cs:231`
- `Source/PuddingRuntime/Services/DirectMemoryLlmClient.cs`
- `Source/PuddingCore/Core/OpenAiLlmGateway.cs`

当前状态：

- `DirectLlmClient.ChatAsync` 内部直接 `new HttpClient()`。
- 流式路径使用 `IHttpClientFactory` 和 `OpenAiLlmGateway`。
- 同步路径硬编码 `temperature=0.7`、`max_tokens=2048`。

影响：

- 同步和流式行为可能不同，包括模型参数、超时、重试、代理、日志和错误处理。
- 连接管理绕过 `IHttpClientFactory`，不利于统一 timeout、diagnostics 和 handler 生命周期。
- prompt template 或 runtime config 无法完整控制 LLM 参数。

建议：

1. 同步与流式统一通过 `OpenAiLlmGateway` 或同等级 gateway 接口。
2. 所有模型参数进入 `LlmConfig` 或模板运行时配置，禁止执行路径硬编码。
3. 对 gateway 建立 contract tests：非流式、流式、错误响应、取消、超时、工具调用 frame。
4. `DirectMemoryLlmClient` 与普通 LLM client 共用基础 gateway 能力，只在 prompt/schema 层区分。

### P1：上下文合成引擎方向合理，但预算和历史摘要过粗

涉及文件：

- `Source/PuddingRuntime/Services/ContextPipeline.cs:82`
- `Source/PuddingRuntime/Services/ContextPipeline.cs:628`
- `Source/PuddingRuntime/Services/ContextPipeline.cs:934`
- `Source/PuddingRuntime/Services/ContextWindowManager.cs`

当前状态：

- `ContextPipeline.AssembleAsync` 已形成系统提示、Persona、工作区、历史、记忆、子代理结果等分层结构。
- token 估算使用字符数 `/4`。
- 旧历史摘要存在占位式描述，例如偏泛化的 general discussion。

影响：

- token 预算在中文、多语言、代码块和 tool payload 下误差较大。
- 长会话中历史摘要质量不足，会让模型失去关键约束和决策上下文。
- 记忆、子代理结果和当前任务之间缺少明确的优先级与压缩策略。

建议：

1. 引入 provider-aware token estimator，至少按当前模型族封装估算器接口。
2. 持久化 session summary，按 turns/checkpoints 增量更新，而不是临时生成粗摘要。
3. 上下文分层应有明确预算：system/persona、current user intent、recent turns、retrieved memory、workspace facts、sub-agent outputs。
4. 增加 golden tests，覆盖长会话、中文、代码块、多 memory hit、子代理输出过长等场景。

### P1：潜意识 LLM deep recall 读取全库候选，规模不稳定

涉及文件：

- `Source/PuddingMemoryEngine/Services/SubconsciousOrchestrator.cs:400`
- `Source/PuddingMemoryEngine/Services/SubconsciousOrchestrator.cs:422`
- `Source/PuddingMemoryEngine/Services/SubconsciousOrchestrator.cs:428`
- `Source/PuddingRuntime/Services/Background/SubconsciousWorkerService.cs`
- `Source/PuddingRuntime/Services/Background/SubconsciousConsolidationHook.cs`
- `Source/PuddingMemoryEngine/Services/MemoryRecallService.cs`

当前状态：

- deep recall 描述为让 LLM 读取所有事实和偏好。
- 实现中 active facts `Take(200)`，preferences `Take(100)`。
- 候选集主要依赖数量截断，而非查询相关性、时间衰减、标签或图关系。

影响：

- 记忆量增长后召回成本和质量不可控。
- 重要但不在前 N 条的记忆可能被忽略。
- LLM 被迫承担过多检索职责，导致成本、延迟和稳定性变差。

建议：

1. deep recall 改为两阶段：先用 FTS/vector/tag/graph relation 取候选，再让 LLM rerank/compile。
2. 为潜意识任务定义预算：最大候选数、最大 token、最大耗时、最大写入条数。
3. 将 consolidation、recall、preference extraction 的输入输出 schema 固定化，减少 prompt 漂移。
4. 建立评估集：同一用户事实、冲突偏好、过期记忆、跨会话召回。

### P1：记忆图书馆 FTS Schema 读取脆弱

涉及文件：

- `Source/PuddingMemoryEngine/Data/MemoryLibrary.cs:374`
- `Source/PuddingMemoryEngine/Data/MemoryLibrary.cs:453`
- `Source/PuddingMemoryEngine/Data/MemoryLibraryConvenience.cs`

当前状态：

- FTS 查询使用 `SELECT b.*`、`SELECT c.*`。
- 读取逻辑按 ordinal 获取字段。
- nullable 字段读取保护不足。

影响：

- 表结构追加字段或列顺序变化时，读取会崩溃或错位。
- NULL 字段会触发运行时异常。
- 已观察到 `MemoryLibrary.SearchBooksFtsAsync` / `SearchChaptersFtsAsync` 相关测试失败，风险不是理论问题。

建议：

1. FTS 查询改为显式列清单，不使用 `SELECT *`。
2. 所有 nullable 字段读取前使用 `IsDBNull`。
3. 为 book/chapter FTS 增加回归测试，覆盖 NULL、列追加、空结果、中文查询。
4. 将 row mapping 抽成单一函数，避免普通查询和 FTS 查询各自维护 ordinal。

### P1：网关和连接器入口过度集中在组合根

涉及文件：

- `Source/PuddingAgent/Program.cs:430`
- `Source/PuddingAgent/Services/ConnectorHost.cs`
- `Source/PuddingAgent/Connectors/WebSocketConnector.cs`
- `Source/PuddingAgent/Connectors/WebhookConnector.cs`
- `Source/PuddingAgent/Connectors/HttpConnector.cs`
- `Source/PuddingAgent/Connectors/MqttConnector.cs`
- `Source/PuddingGateway/GatewayAdapterHost.cs`

当前状态：

- connector ingress 到事件系统、WebSocket SSM 转发、延迟订阅等逻辑集中在 `Program.cs`。
- 连接器负责协议适配，但 envelope 标准化、sessionId、eventType、traceId 和 identity 归属不够集中。

影响：

- 新增 connector 时容易复制分发逻辑。
- trace、身份、重放和错误处理难以统一。
- 组合根承担业务编排，测试难度较高。

建议：

1. 抽出 `ConnectorIngressService`。
2. 该服务负责协议 envelope 标准化、identity 绑定、sessionId 解析、traceId 生成、事件类型映射和 SSM 转发。
3. connector 只负责协议收发，不能直接决定业务事件落点。
4. 为 HTTP/WebSocket/MQTT/Webhook 建立统一 ingress contract tests。

### P2：安全边界缺口

涉及文件：

- `Source/PuddingPlatform/Controllers/Api/SessionEventsController.cs:93`
- `Source/PuddingAgent/Connectors/WebSocketConnector.cs:75`
- `Source/PuddingAgent/Connectors/HttpConnector.cs:79`

当前状态：

- `SessionEventsController.GetSubAgents` 标记为 `[AllowAnonymous]`。
- `WebSocketConnector` 接收 `authenticatedUser` 参数并记录 auth frame，但 connector 内部不是统一认证边界。
- `HttpConnector` 存在 anonymous fallback。

影响：

- 会话和子代理元数据可能被匿名访问。
- connector identity 与业务事件之间缺少统一强约束。
- 生产环境与开发环境的匿名行为容易混淆。

建议：

1. 统一 connector identity 模型，事件系统只接受带 identity 的 envelope。
2. 匿名访问仅允许开发模式，并通过配置显式开启。
3. Session/sub-agent 查询接口默认要求认证和 session ownership 检查。
4. 安全相关接口补最小权限测试。

## 3. 子系统评估

### 上下文合成引擎

优势：

- 已经存在中心化 `ContextPipeline`，说明系统有意避免各执行路径自行拼 prompt。
- 有 `ContextWindowManager`，具备上下文预算意识。
- 能把 persona、workspace、history、memory、sub-agent outputs 组合到执行上下文。

主要不足：

- token 估算过粗。
- 历史压缩不是稳定持久能力。
- 多来源上下文的优先级和截断策略需要更明确。

改进重点：

- 将 token 估算、摘要、记忆选择从大 pipeline 中拆成可测试组件。
- 建立长上下文 golden tests，固定输出结构和裁剪行为。

### LLM 执行引擎

优势：

- 已经区分同步和流式能力。
- 流式路径开始接入统一 gateway 和 frame 输出。
- 执行服务能够与会话事件流、上下文和工具系统连接。

主要不足：

- 同步路径绕过 gateway。
- 参数散落和硬编码。
- 执行引擎承担了过多会话写入细节。

改进重点：

- 统一 gateway。
- 执行结果标准化为 frame/event。
- 将 session append、connector fanout 从执行核心中剥离到上层编排。

### 会话层

优势：

- `SessionStateManager` 是正确的架构方向，能承载事件日志、SSE、子代理追踪和恢复。
- `SessionEventsController` 已经围绕 session event stream 暴露 API。

主要不足：

- 和旧 `SessionEventHub` 并存。
- 启动期删除 session 表。
- session ownership 和匿名访问边界需要收紧。

改进重点：

- SSM 成为唯一状态源。
- 事件日志 append-only。
- 所有会话读写加 identity 和 ownership 校验。

### 潜意识 LLM

优势：

- 已有后台 worker、consolidation hook、recall service 和 orchestrator。
- 架构上已经把在线对话和后台记忆整理分开。

主要不足：

- recall 候选裁剪依赖粗暴数量截断。
- LLM 承担过多全库扫描和归纳职责。
- 缺少可量化的召回质量评估。

改进重点：

- retrieval first，LLM rerank second。
- 建立 recall/consolidation 评估集。
- 给后台任务加入预算、幂等键和失败恢复。

### 记忆图书馆

优势：

- 已有 book/chapter/fact/preference 等较丰富的记忆结构。
- FTS 能力已经接入。
- convenience 层降低了调用复杂度。

主要不足：

- FTS 查询 mapping 对 schema 变化敏感。
- NULL 读取保护不足。
- 记忆图谱关系和召回策略还没有充分成为主路径能力。

改进重点：

- 先修 FTS 稳定性。
- 再强化 graph relation、tag、temporal decay 和 semantic score 的统一排序。

### 事件系统

优势：

- 已经有 event bus、preprocessor、dispatcher、priority queue 的分层雏形。
- connector ingress 能进入统一事件系统。

主要不足：

- 队列实现与接口承诺不一致。
- 缺少持久化、lease、retry、dead-letter。
- Program 组合根中仍有较多业务事件编排。

改进重点：

- 将队列持久化。
- 事件 envelope 标准化。
- 建立端到端事件恢复测试：入队、重启、继续派发。

### 子代理系统

优势：

- 已有 spawn tool、sub-agent manager、memory explorer 和查询工具。
- 子代理已经能与 session event 和内部事件系统集成。

主要不足：

- lifecycle 控制面重复。
- 状态发布重复。
- 缺少统一取消、超时、权限和资源预算。

改进重点：

- 单一 manager。
- 明确状态机：created、running、completed、failed、cancelled、timed_out。
- 每个状态转换只允许一个组件写入。

### 网关和连接器

优势：

- connector 类型覆盖 WebSocket、Webhook、HTTP、MQTT。
- gateway adapter host 提供进一步外部集成空间。

主要不足：

- ingress 编排过度集中在 `Program.cs`。
- connector identity 和事件 envelope 没有被强约束。
- 多 connector 共享行为缺少 contract tests。

改进重点：

- 抽 `ConnectorIngressService`。
- connector 只做协议适配。
- 统一认证、trace、session 映射和错误返回。

## 4. 建议改进顺序

1. 移除启动期 `DROP TABLE`，补 migration 或 `CREATE TABLE IF NOT EXISTS`，确保会话事件和子代理状态不再被启动流程删除。
2. 统一会话事件写入层，废弃业务路径里的 `SessionEventHub` 直接写入，所有 session frame 通过 `ISessionStateManager.AppendAsync`。
3. 合并子代理执行入口，保留 `ISubAgentManager` 作为唯一生命周期控制面。
4. 将事件队列从内存实现升级为 SQLite 持久队列，补 lease、retry、dead-letter。
5. 统一 LLM sync/stream gateway，消除 `new HttpClient()` 和硬编码模型参数。
6. 修复 `MemoryLibrary` FTS 显式列与 nullable 读取，并补回归测试。
7. 将 `ContextPipeline` 的 token 估算、历史摘要和来源预算做成独立、可测试组件。
8. 将 connector ingress 从 `Program.cs` 拆出，补 identity、trace、重放和 contract tests。
9. 将潜意识 recall 改为 retrieval + rerank 架构，并建立小型评估集。
10. 收紧 session/sub-agent API 安全边界，匿名能力仅允许开发模式显式开启。

## 5. 可转化任务清单

建议将整改拆成以下工程任务：

1. `P0/session-ddl-safety`：移除启动期 destructive DDL，补 schema migration 和保护测试。
2. `P0/session-event-unification`：统一 SSM 写入路径，迁移 `ChatApiController`。
3. `P0/sub-agent-lifecycle-owner`：让 `ISubAgentManager` 成为唯一子代理生命周期入口。
4. `P1/persistent-event-queue`：实现 SQLite priority event queue。
5. `P1/llm-gateway-unification`：统一 DirectLlmClient 同步与流式路径。
6. `P1/memory-library-fts-hardening`：修复 FTS 显式列和 nullable mapping。
7. `P1/context-pipeline-budgeting`：引入 token estimator、持久摘要和 golden tests。
8. `P1/connector-ingress-service`：抽出 connector ingress 编排服务。
9. `P1/subconscious-recall-ranking`：deep recall 改为候选检索加 LLM rerank。
10. `P2/session-security-hardening`：收紧匿名接口和 session ownership 校验。

## 6. 验证状态

本报告基于静态代码审查和先前局部测试结果形成。先前验证结果显示：

- `dotnet test PuddingAgentNetwork.slnx --no-restore --nologo`：120 秒超时。
- `dotnet test Source\PuddingCoreTests\PuddingCoreTests.csproj --no-restore --nologo --logger "console;verbosity=minimal"`：120 秒超时。
- `dotnet test Source\PuddingMemoryEngineTests\PuddingMemoryEngineTests.csproj --no-restore --nologo --logger "console;verbosity=minimal"`：失败，61 通过，3 失败，失败集中在 FTS 查询读取。
- `npm run tsc` in `Source\PuddingPlatformAdmin`：失败，存在 3 个 TypeScript 错误。

本报告未修改业务代码，只新增审计文档。后续进入修复阶段前，应先确认当前工作区已有未提交改动的归属，避免误改用户正在进行的工作。
