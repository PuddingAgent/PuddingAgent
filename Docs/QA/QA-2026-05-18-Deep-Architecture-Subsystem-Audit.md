# PuddingAgent 深入架构子系统审计报告

审计日期：2026-05-18  
审计范围：上下文合成引擎、LLM 执行引擎、会话层、潜意识 LLM、记忆图书馆、事件系统、子代理系统、网关与连接器  
审计方式：直接阅读 `Source/` 代码路径，重点评估执行链路、状态一致性、持久化、失败隔离、安全边界和可演进性。

## 总体结论

当前架构已经具备“单进程 Agent 平台”的核心骨架：`PuddingAgent` 作为宿主，聚合 Runtime、Platform API、MemoryEngine、Connector、P2P、事件系统与前端静态资源。上下文合成、Agent loop、会话事件日志、潜意识记忆、子代理和连接器都有实际实现，不只是文档设计。

主要问题不在能力缺失，而在 **多套迁移中间态并存**：

- 会话流式层同时存在新 `SessionStateManager` 和旧 `SessionEventHub`。
- 子代理有 `SubAgentTool` 与 `SubAgentManager` 两套执行入口。
- 事件队列接口声明持久化，实际仍是内存队列。
- 启动初始化中仍有 destructive DDL。
- LLM 同步/流式路径实现不一致。
- 记忆图书馆检索依赖 `SELECT * + ordinal`，对 schema 演进脆弱。

当前建议优先级：先修正数据丢失和状态一致性，再优化上下文与记忆质量。

## P0 风险

### 1. 启动时清空会话事件和子代理状态

位置：

- `Source/PuddingAgent/Program.cs`
- 启动初始化中的 `pendingTableDdl`

当前代码在启动时执行：

```sql
DROP TABLE IF EXISTS session_event_log;
DROP TABLE IF EXISTS session_sub_agents;
```

影响：

- 破坏 `SessionStateManager` “append-only SQLite 事件日志”的设计目标。
- 每次重启都会丢失历史 SSE frame、会话重放数据和子代理追踪。
- 前端历史恢复、异步子代理完成通知、诊断日志都不可靠。

改进建议：

1. 立即移除启动期 `DROP TABLE`。
2. 使用 EF Core migration 或 `CREATE TABLE IF NOT EXISTS`。
3. 给 `session_event_log` 和 `session_sub_agents` 加 migration regression test，验证重启后数据仍存在。

### 2. 会话层存在两套流式通道

涉及文件：

- `Source/PuddingPlatform/Services/SessionStateManager.cs`
- `Source/PuddingPlatform/Services/SessionEventHub.cs`
- `Source/PuddingPlatform/Controllers/Api/ChatApiController.cs`
- `Source/PuddingRuntime/Services/AgentExecutionService.cs`

观察：

- 新架构目标是 `ISessionStateManager.AppendAsync` 统一持久化和实时推送。
- `AgentExecutionService.ExecuteStreamAsync` 已经写入 SSM。
- `ChatApiController` 仍注入并写入 `SessionEventHub`。

影响：

- Chat API 路径与 connector/event 路径行为不一致。
- 某些 frame 只进入旧 hub，不进入 append-only 日志。
- 前端重连和历史分页依赖 SSM 时可能拿不到完整流。

改进建议：

1. `ChatApiController` 改为只写 `ISessionStateManager`。
2. `SessionEventHub` 降级为兼容适配器，禁止新业务代码直接依赖。
3. 建立 contract test：同一条消息通过 Chat API、HTTP connector、WebSocket connector 进入后，最终都能在 `GetEventsAsync` 中重放完整 frame。

### 3. 子代理系统存在重复执行入口和重复通知风险

涉及文件：

- `Source/PuddingRuntime/Services/Skills/SubAgentTool.cs`
- `Source/PuddingPlatform/Services/SubAgentManager.cs`
- `Source/PuddingAgent/Services/Events/AgentEventHandler.cs`
- `Source/PuddingPlatform/Services/SessionStateManager.cs`

观察：

- `SubAgentTool` 直接解析参数、创建 child request、调用 `AgentExecutionService`。
- `SubAgentManager` 也负责 spawn、track、dispatch、publish completion。
- 异步完成后会同时：
  - `TrackSubAgentCompleteAsync`
  - `AppendAsync(SubAgentCompleted)`
  - `PublishAsync(agent.sub_completed)`
  - `AgentEventHandler` 再次追加父代理通知 frame

影响：

- 前端可能收到重复 `subagent.completed`。
- 父代理可能被重复唤醒。
- 取消、超时、失败状态难以统一。
- 未来扩展远程子代理或 P2P 子代理时会增加分叉。

改进建议：

1. 只保留 `ISubAgentManager` 作为权威入口。
2. `spawn_sub_agent` 工具仅调用 Manager，不直接调用 `AgentExecutionService`。
3. 子代理完成事件只由 Manager 写一次 SSM；事件系统只负责“是否唤醒父代理”。
4. 为 async 子代理增加幂等 completion key，避免重复完成事件。

## P1 风险

### 4. 事件系统接口目标与实现不一致

涉及文件：

- `Source/PuddingCore/Abstractions/IPriorityEventQueue.cs`
- `Source/PuddingRuntime/Services/Events/PriorityEventQueue.cs`
- `Source/PuddingRuntime/Services/Events/EventDispatcher.cs`
- `Source/PuddingAgent/Services/Events/EventIngressBridge.cs`

观察：

- 接口注释写明“SQLite 持久化，进程重启不丢事件”。
- 当前实现是三个内存 `Queue<QueuedEvent>`。
- `UpdateStatusAsync` 只记录日志，没有持久状态。
- 重试通过 `Task.Run + Delay + 重新入队` 实现。

影响：

- 进程重启会丢事件。
- handler 执行中崩溃无法恢复。
- dead letter 只存在日志里，无法被后台 UI 或诊断 API 查询。
- 事件队列无法支撑 connector、cron、P2P 的可靠交付。

改进建议：

1. 实现 SQLite `event_queue` 表，字段包括 `id/type/priority/payload/status/retry_count/available_at/locked_until/error`。
2. `DequeueAsync` 使用 lease 语义，避免多 worker 重复消费。
3. `UpdateStatusAsync` 真正更新状态。
4. 为 `dead_letter` 提供查询 API 和重放操作。

### 5. LLM 执行引擎同步和流式路径不一致

涉及文件：

- `Source/PuddingRuntime/Services/DirectLlmClient.cs`
- `Source/PuddingCore/Core/OpenAiLlmGateway.cs`
- `Source/PuddingRuntime/Services/AgentExecutionService.cs`

观察：

- `ChatAsync` 自己构造 OpenAI-compatible JSON，并直接 `new HttpClient()`。
- `ChatStreamAsync` 委托 `OpenAiLlmGateway`，使用 `IHttpClientFactory`。
- 同步路径硬编码 `temperature=0.7`、`max_tokens=2048`。
- 工具序列化、reasoning/thinking 参数在两条路径中不完全一致。

影响：

- 同模型在同步与流式下行为可能不同。
- 连接池、超时、重试策略无法统一。
- 后续支持多 provider 会出现更多条件分支。

改进建议：

1. 同步和流式都统一走 `OpenAiLlmGateway` 或新的 `OpenAiCompatibleClient`。
2. `LlmConfig` 增加 generation config：temperature、max output tokens、tool choice、thinking mode。
3. 删除 `DirectLlmClient.ChatAsync` 中的裸 `new HttpClient()`。
4. 增加 LLM request snapshot 测试，验证 sync/stream 的消息、tools、reasoning 参数一致。

### 6. 上下文合成引擎有完整模型，但预算和摘要仍偏粗糙

涉及文件：

- `Source/PuddingRuntime/Services/ContextPipeline.cs`
- `Source/PuddingRuntime/Services/ContextWindowManager.cs`
- `Source/PuddingRuntime/Services/SystemPromptBuilder.cs`

正向信号：

- 已按层构建上下文：STATIC、TOOLS、SKILLS、USER、PINNED、RECENT、RECALLED、CURRENT、RUNTIME。
- 静态层和工具层有缓存设计。
- 支持 deep/instant memory recall。
- `ContextAssemblyStore` 可记录上下文快照。

问题：

- token 估算是字符数 `/4`，对中文、代码、工具 schema 都不准确。
- `SummarizeOlderHistory` 只是统计 user/assistant 数量，并输出泛化主题。
- 历史截断策略和模型真实上下文窗口脱节。
- 静态缓存 key 只看 session/template，Persona 文件或 DB 模板变化后可能不刷新。

改进建议：

1. 引入 provider-aware token estimator，至少按 OpenAI-compatible tokenizer 和中文 fallback 分开。
2. 把旧历史摘要做成持久化 `SessionSummary`，由后台压缩任务生成。
3. 给每个 context layer 加 `source_version`，模板、persona、工具 schema 变化时自动失效。
4. 增加 context budget test：给定模板、工具数、历史数，断言最终上下文不超过预算。

### 7. 潜意识 LLM 召回策略会随记忆增长线性变慢

涉及文件：

- `Source/PuddingMemoryEngine/Services/SubconsciousOrchestrator.cs`
- `Source/PuddingRuntime/Services/Background/SubconsciousWorkerService.cs`
- `Source/PuddingRuntime/Services/Background/SubconsciousConsolidationHook.cs`
- `Source/PuddingMemoryEngine/Services/MemoryRecallService.cs`

正向信号：

- 主对话完成后通过 Hook 投递后台 consolidation job。
- Worker 串行消费，避免并发写入过度冲突。
- consolidation 失败会写 job log。
- 支持 facts、preferences、library structured books 三条记忆写入路径。

问题：

- deep recall 当前读取最多 200 条 facts 和 100 条 preferences，然后交给 LLM 全量判断相关性。
- `SummarizeSessionAsync` 和 `SearchMemoriesAsync` 仍是阶段占位。
- LLM JSON 抽取靠 `ExtractJson` 截取，不够稳定。
- memory LLM 客户端也存在裸 `new HttpClient()`。

改进建议：

1. deep recall 改为两阶段：先 FTS/vector/tag 候选召回，再 LLM rerank/compile。
2. consolidation job 增加状态：queued/running/completed/failed/retryable。
3. 对 LLM JSON 输出使用 schema/response_format 或强约束解析器。
4. 将 memory LLM 的 HTTP 调用统一纳入 `IHttpClientFactory`。

### 8. 记忆图书馆检索对 schema 演进脆弱

涉及文件：

- `Source/PuddingMemoryEngine/Data/MemoryLibrary.cs`
- `Source/PuddingMemoryEngine/Data/MemoryLibraryConvenience.cs`
- `Source/PuddingMemoryEngine/Services/MemoryRecallService.cs`

问题：

- `SearchBooksFtsAsync` 使用 `SELECT b.*` 后按 ordinal 读取。
- `SearchChaptersFtsAsync` 使用 `SELECT c.*` 后按 ordinal 读取。
- 字段追加或 nullable 字段会导致读取错位或 NULL 崩溃。
- 上一轮验证中 MemoryEngine 测试已有 FTS NULL 读取失败。

改进建议：

1. 所有 FTS SQL 改为显式列清单。
2. nullable 字段统一 `IsDBNull`。
3. Book/Chapter DTO mapping 只通过命名列或 EF projection。
4. 为 FTS 搜索增加空字段、追加 embedding 字段、旧数据库迁移三类回归测试。

### 9. 网关与连接器处于新旧接口并存状态

涉及文件：

- `Source/PuddingAgent/Services/ConnectorHost.cs`
- `Source/PuddingAgent/Connectors/*.cs`
- `Source/PuddingGateway/GatewayAdapterHost.cs`
- `Source/PuddingCore/Platform/IPuddingConnector.cs`
- `Source/PuddingCore/Platform/IGateway.cs`

观察：

- 新接口是 `IPuddingConnector`，支持 receive/send/manage/stream。
- 旧接口是 `IPuddingGatewayAdapter`，仍保留 `GatewayAdapterHost`。
- WebSocket、Webhook、HTTP、MQTT 已走新 connector。
- connector ingress 标准化逻辑写在 `Program.cs` 中。

影响：

- 新旧术语混用，贡献者难以判断应扩展 Connector 还是 GatewayAdapter。
- `Program.cs` 承担协议路由、eventType 构造、SSM-to-WS 转发。
- WebSocket 转发用延迟 2 秒订阅 session，属于时序脆弱点。

改进建议：

1. 明确 `GatewayAdapterHost` 为 legacy，并迁移/删除旧 adapter。
2. 抽出 `ConnectorIngressService`，从 `Program.cs` 移走 envelope → event 的转换。
3. WebSocket 绑定应在 session 创建/metadata frame 时建立，不应依赖固定 delay。
4. 所有 connector ingress 都应产出统一 `ConnectionIdentity` 和 trace id。

## P2 风险

### 10. 安全边界不一致

观察：

- `SessionEventsController.GetSubAgents` 标记 `[AllowAnonymous]`。
- WebSocket connector 内部只记录 auth frame，真实鉴权依赖外层传入。
- HTTP connector 默认允许 `http:anonymous`。
- Webhook 如果没有 signature header，会跳过验证逻辑中的失败路径，因为当前只在 header 非空时调用 `VerifySignature`。

改进建议：

1. 除 bootstrap/auth 外，所有诊断和子代理状态接口默认 `[Authorize]`。
2. connector ingress 必须携带 `ConnectionIdentity`，匿名能力只允许 dev mode。
3. Webhook 在启用签名验证时，无签名也必须拒绝。
4. 将 connector 权限映射到 workspace/channel policy。

### 11. CancellationToken 和 fire-and-forget 使用过多

涉及模式：

- `CancellationToken.None`
- `_ = Task.Run(...)`
- `async void Append(...)`

影响：

- 请求取消后后台任务仍继续执行。
- 失败只能靠日志发现。
- shutdown 时可能丢帧或丢 job。

改进建议：

1. 引入 `IBackgroundTaskQueue`，统一托管 fire-and-forget。
2. 所有后台任务记录 task id、session id、错误状态。
3. `ApplicationStopping` 时 drain 关键队列。

## 子系统逐项评估

| 子系统 | 当前成熟度 | 主要问题 | 优先级 |
| --- | --- | --- | --- |
| 上下文合成引擎 | B | token 估算粗糙，旧历史摘要占位，缓存失效不足 | P1 |
| LLM 执行引擎 | B- | sync/stream 分叉，裸 HttpClient，参数硬编码 | P1 |
| 会话层 | C+ | SSM 与旧 Hub 并存，启动清表 | P0 |
| 潜意识 LLM | B- | deep recall 全量读 facts，部分 API 占位 | P1 |
| 记忆图书馆 | B- | FTS ordinal 读取脆弱，测试已失败 | P1 |
| 事件系统 | C | 队列非持久，状态更新空实现 | P1 |
| 子代理系统 | C+ | 两套入口，重复通知，取消不完整 | P0 |
| 网关连接器 | B- | 新旧接口并存，ingress 写在 Program | P1 |

## 建议整改顺序

### 第一阶段：数据和状态一致性

1. 移除启动期 `DROP TABLE`。
2. 统一 Chat API、connector、event path 到 `ISessionStateManager`。
3. 将 `SessionEventHub` 降为 legacy adapter。
4. 修复 `GetSubAgents` 匿名访问。

### 第二阶段：子代理和事件系统收敛

1. `spawn_sub_agent` 只调用 `ISubAgentManager`。
2. 子代理完成只写一次 SSM。
3. 实现 SQLite 持久事件队列。
4. 增加 dead letter 查询和 retry API。

### 第三阶段：LLM 和上下文可测试化

1. 合并 sync/stream LLM client。
2. 引入真实 token estimator。
3. 建立 context assembly snapshot tests。
4. 将 generation config 从硬编码迁到模板/模型配置。

### 第四阶段：记忆系统质量提升

1. 修复 FTS 显式列和 nullable mapping。
2. deep recall 改为候选召回 + LLM rerank。
3. consolidation job 增加 retry/backoff。
4. memory LLM 输出使用 schema 化 JSON。

### 第五阶段：连接器产品化

1. 抽出 `ConnectorIngressService`。
2. 删除或标记 legacy gateway adapter。
3. connector ingress 统一身份、trace、workspace/channel policy。
4. 为 WebSocket、HTTP、Webhook、MQTT 增加端到端 contract tests。

## 关键审计文件清单

- `Source/PuddingRuntime/Services/ContextPipeline.cs`
- `Source/PuddingRuntime/Services/ContextWindowManager.cs`
- `Source/PuddingRuntime/Services/AgentExecutionService.cs`
- `Source/PuddingRuntime/Services/DirectLlmClient.cs`
- `Source/PuddingRuntime/Services/DirectMemoryLlmClient.cs`
- `Source/PuddingPlatform/Services/SessionStateManager.cs`
- `Source/PuddingPlatform/Controllers/Api/SessionEventsController.cs`
- `Source/PuddingPlatform/Controllers/Api/ChatApiController.cs`
- `Source/PuddingMemoryEngine/Services/SubconsciousOrchestrator.cs`
- `Source/PuddingMemoryEngine/Services/MemoryRecallService.cs`
- `Source/PuddingMemoryEngine/Data/MemoryLibrary.cs`
- `Source/PuddingMemoryEngine/Data/MemoryLibraryConvenience.cs`
- `Source/PuddingRuntime/Services/Events/InternalEventBus.cs`
- `Source/PuddingRuntime/Services/Events/PriorityEventQueue.cs`
- `Source/PuddingRuntime/Services/Events/EventDispatcher.cs`
- `Source/PuddingAgent/Services/Events/EventIngressBridge.cs`
- `Source/PuddingAgent/Services/Events/AgentEventHandler.cs`
- `Source/PuddingRuntime/Services/Skills/SubAgentTool.cs`
- `Source/PuddingPlatform/Services/SubAgentManager.cs`
- `Source/PuddingAgent/Services/ConnectorHost.cs`
- `Source/PuddingAgent/Connectors/WebSocketConnector.cs`
- `Source/PuddingAgent/Connectors/WebhookConnector.cs`
- `Source/PuddingAgent/Connectors/HttpConnector.cs`
- `Source/PuddingAgent/Connectors/MqttConnector.cs`
- `Source/PuddingGateway/GatewayAdapterHost.cs`

## 最终建议

当前不要继续优先扩展新功能。应先处理架构一致性问题，尤其是会话层、子代理和事件队列。这三处是 Agent 平台的状态中枢，一旦不一致，前端展示、后台任务、连接器和记忆系统都会被放大影响。

最小可执行整改包：

1. 修掉启动清表。
2. 让所有 frame 只通过 SSM 写入。
3. 让所有子代理只通过 Manager 派生。
4. 修复 MemoryLibrary FTS mapping。
5. 将 `npm run tsc` 和 MemoryEngine tests 恢复为绿色。

完成这组后，再投入上下文质量、潜意识召回和 connector 产品化，收益会更稳定。
