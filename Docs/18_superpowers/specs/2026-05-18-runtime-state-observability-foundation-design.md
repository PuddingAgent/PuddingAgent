# Runtime State And Observability Foundation Design

日期：2026-05-18  
状态：待评审  
范围：事件系统、执行引擎、会话层、子代理系统、基础可观测性  
不在本阶段范围：完整 SQLite 持久事件队列替换、LLM gateway 全量重构、潜意识 LLM recall 重排、前端大规模信息架构重做

## 1. 背景

审计发现当前 Pudding 的关键基础设施处于迁移中间态：

1. 启动路径仍可能删除 `session_event_log` 和 `session_sub_agents`。
2. 会话输出同时存在 `ISessionStateManager` 和旧 `SessionEventHub` 两套路径。
3. 子代理生命周期由 `SubAgentTool` 和 `SubAgentManager` 重复管理。
4. 事件系统接口承诺持久化，但当前队列实现仍是内存队列。
5. 事件、会话、执行、子代理之间缺少统一 trace/correlation 语义，日志和界面上难以还原执行顺序。

本阶段目标不是对单点 bug 打补丁，而是建立一个可靠的运行时状态和可观测性基座，让后续事件队列持久化、执行引擎收敛、连接器治理和前端诊断面板有稳定依赖。

## 2. 目标

第一阶段交付以下能力：

1. 应用启动不再破坏会话事件和子代理状态数据。
2. 会话事件写入路径收敛到 `ISessionStateManager`。
3. 子代理生命周期收敛到 `ISubAgentManager`。
4. 引入统一运行时追踪模型，至少覆盖 session、execution、event、sub-agent 四类活动。
5. 日志中能通过同一组 ID 关联一次用户请求的完整链路。
6. 界面/API 能读取近期运行时事件，展示关键组件的执行顺序、状态和结果。
7. 为后续 SQLite 持久事件队列、LLM gateway 统一和 connector ingress 服务拆分预留兼容接口。

## 3. 非目标

本阶段不做以下事项：

1. 不一次性替换整个事件队列为 SQLite 持久实现，但会补齐 trace/status 字段和接口边界，为下一阶段替换做准备。
2. 不重写 `AgentExecutionService` 的完整执行循环，只收敛它与会话状态层、子代理和可观测性之间的接口。
3. 不删除所有 legacy 类型，但会停止新路径依赖旧 `SessionEventHub`。
4. 不重做管理后台 UI，只提供一个最小可用的运行时事件读取 API 和可接入的数据结构。
5. 不处理所有安全边界，仅在新增诊断 API 上避免匿名暴露敏感数据。

## 4. 方案对比

### 方案 A：基础设施基座优先，分阶段收敛

做法：

- 先修启动期 destructive DDL。
- 统一会话事件写入入口。
- 收敛子代理 lifecycle owner。
- 引入 trace/correlation 模型和运行时观测事件。
- 保留现有内存事件队列，下一阶段替换为 SQLite。

优点：

- 风险可控，单阶段可验证。
- 先解决状态一致性和诊断盲区，能降低后续大改成本。
- 不要求一次性理解和改写全部执行引擎。

缺点：

- 事件队列持久化需要第二阶段继续推进。
- 旧代码仍会短期存在，但职责会被限制。

结论：推荐。

### 方案 B：一次性重构事件系统、执行引擎、会话层和子代理

做法：

- 同时替换事件队列、执行引擎输出模型、会话层、子代理 lifecycle、连接器 ingress。

优点：

- 理论上最终形态更干净。
- legacy 代码可以更快删除。

缺点：

- 变更面过大，测试失败时难以定位。
- 对当前已有未提交/近期改动干扰大。
- 可观测性尚未建立前做大重构，诊断成本高。

结论：不推荐。

### 方案 C：只修 P0 bug，不引入观测基座

做法：

- 删除启动 `DROP TABLE`。
- 将几个直接调用点改到 SSM。
- 修复子代理重复通知。

优点：

- 最快。
- 短期代码量少。

缺点：

- 仍然无法从日志和界面判断完整执行顺序。
- 下一次跨子系统问题仍然难诊断。
- 不能满足“增强基础设施和可观测性”的核心目标。

结论：不足以支撑后续开发。

## 5. 推荐设计

采用方案 A，建立“运行时状态与可观测性基座”。

核心思想：

1. `ISessionStateManager` 是会话事件唯一写入入口。
2. `ISubAgentManager` 是子代理生命周期唯一控制面。
3. `IRuntimeTraceContext` 或等价模型在请求进入时创建，并贯穿事件、执行、会话和子代理。
4. `IRuntimeActivitySink` 接收轻量运行时活动事件，负责写日志、可选持久化和诊断 API。
5. 现有事件系统继续运行，但所有入队、预处理、派发、处理结果都带 trace metadata。

## 6. 关键数据模型

### 6.1 Runtime Trace Identity

建议新增运行时追踪标识：

- `TraceId`：一次用户请求或外部事件的全链路 ID。
- `CorrelationId`：跨组件关联 ID，默认等于 `TraceId`，外部 connector 可传入。
- `SessionId`：会话 ID。
- `ExecutionId`：一次 agent 执行 ID。
- `ParentExecutionId`：子代理执行时指向父执行。
- `SubAgentId`：子代理实例 ID。
- `EventId`：内部事件 ID。
- `ConnectorId`：外部连接器来源。
- `WorkspaceId`：工作区或租户边界。
- `UserId`：用户或 connector identity。

### 6.2 Runtime Activity

建议新增统一活动事件，面向日志和界面：

- `ActivityId`
- `TraceId`
- `CorrelationId`
- `SessionId`
- `ExecutionId`
- `SubAgentId`
- `Component`
- `Operation`
- `Status`
- `StartedAtUtc`
- `EndedAtUtc`
- `DurationMs`
- `Severity`
- `Summary`
- `MetadataJson`
- `ErrorCode`
- `ErrorMessage`

`Component` 的首批枚举：

- `connector`
- `event_queue`
- `event_dispatcher`
- `session_state`
- `agent_execution`
- `context_pipeline`
- `llm_gateway`
- `tool_runner`
- `sub_agent`
- `memory`

`Status` 的首批枚举：

- `started`
- `succeeded`
- `failed`
- `cancelled`
- `deferred`
- `retried`

### 6.3 Session Event Metadata

`SessionStateManager.AppendAsync` 写入的 session event 应补充 trace metadata：

- `traceId`
- `correlationId`
- `executionId`
- `parentExecutionId`
- `subAgentId`
- `component`
- `operation`

这样前端 timeline 可以同时展示用户可见消息和系统内部执行顺序。

## 7. 组件设计

### 7.1 启动 Schema 管理

变更：

- 移除 `Program.cs` 中针对 `session_event_log` 和 `session_sub_agents` 的 `DROP TABLE`。
- 保留或新增非破坏性 `CREATE TABLE IF NOT EXISTS`。
- 对启动 SQL 加测试或扫描保护，禁止 destructive DDL 回归。

验收：

- 应用重启后 session event 和 sub-agent 状态仍存在。
- 测试能捕获相关 `DROP TABLE` 回归。

### 7.2 会话层

变更：

- `ISessionStateManager.AppendAsync` 成为唯一会话事件写入入口。
- `ChatApiController` 停止直接依赖旧 `SessionEventHub` 写事件。
- 旧 `SessionEventHub` 如需保留，只作为兼容适配器，不作为业务状态源。
- `SessionEventsController` 的读取接口继续围绕 SSM。

验收：

- 同一 session 的可见消息、thinking、tool call、tool result、sub-agent event 都可从 SSM 读取。
- 前端断线后重新读取能够补齐事件。
- 日志中 session append 包含 trace metadata。

### 7.3 执行引擎

变更：

- `AgentExecutionService` 在每次执行开始时获取或创建 `ExecutionId`。
- 执行开始、上下文合成、LLM 请求、工具调用、流式 frame、执行结束都写入 runtime activity。
- 执行引擎不直接面向旧 hub。
- 输出 frame 写入 SSM 时带 trace metadata。

验收：

- 日志能显示一次请求从 execution started 到 execution completed 的顺序。
- 失败时 activity 记录 `failed`，并包含错误摘要。
- 取消时 activity 记录 `cancelled`，不伪装成 success。

### 7.4 事件系统

变更：

- 内部事件 envelope 增加 trace metadata。
- `InternalEventBus.PublishAsync`、`PriorityEventQueue.EnqueueAsync`、`EventDispatcher` 处理前后写 runtime activity。
- 当前内存队列保留，但状态更新和日志语义与未来持久队列对齐。

验收：

- 单个外部事件可在日志中看到 publish、enqueue、dequeue、dispatch、handler result。
- handler 失败时可观测事件记录错误和 retry 意图。
- 后续 SQLite 队列可以替换实现而不改变上层 trace 语义。

### 7.5 子代理系统

变更：

- `ISubAgentManager` 成为唯一 lifecycle owner。
- `SubAgentTool` 只负责 tool 参数验证和调用 manager。
- 子代理创建、开始、完成、失败、取消、超时都由 manager 写 SSM 和 runtime activity。
- 子代理完成事件应具备幂等键，避免重复完成通知。

验收：

- 同一子代理只能产生一个 terminal state。
- 父 session timeline 可以看到子代理 started/completed/failed。
- 日志可通过 parent execution 和 sub-agent ID 关联父子执行链路。

### 7.6 可观测性 API

变更：

- 新增最小诊断读取接口，用于读取近期 runtime activity。
- 支持按 `traceId`、`sessionId`、`executionId`、`component` 过滤。
- 默认限制条数和时间窗口，避免无界查询。
- 接口需要认证，不允许匿名访问。

验收：

- 开发者可以根据 sessionId 查询一次会话的组件执行顺序。
- 可以根据 traceId 查询完整链路。
- API 不暴露 prompt 全文或敏感 payload，只暴露摘要和受控 metadata。

## 8. 日志规范

所有关键组件日志应遵循同一字段集合：

- `TraceId`
- `CorrelationId`
- `SessionId`
- `ExecutionId`
- `SubAgentId`
- `EventId`
- `Component`
- `Operation`
- `Status`
- `DurationMs`

日志原则：

1. 开始和结束成对记录。
2. 错误必须记录异常类型、错误摘要和 trace metadata。
3. 大 payload 不直接写入日志，使用摘要、长度、hash 或受控字段。
4. 可观测性不能影响主路径成功；sink 失败只记录自身错误，不阻断 agent 执行。

## 9. 测试策略

### 9.1 单元测试

- trace context 创建和继承。
- runtime activity sink 记录 started/succeeded/failed。
- session event metadata 写入。
- sub-agent terminal state 幂等。
- destructive DDL 保护。

### 9.2 集成测试

- Chat API 发送消息后，SSM 可读取完整事件。
- 子代理异步完成后，父 session timeline 可读取完成事件。
- 内部事件 publish 到 handler 的链路包含同一 traceId。
- 诊断 API 可按 traceId/sessionId 查询活动。

### 9.3 回归测试

- 应用初始化不会删除已有 session event。
- 旧 hub 不再作为主要业务写入路径。
- 子代理完成不会重复写 terminal event。

## 10. 实施阶段

### Phase 1：状态安全和 trace 基础

- 移除 destructive DDL。
- 新增 trace/activity 基础类型。
- 新增 runtime activity sink 的内存或 SQLite 轻量实现。
- 接入日志字段。

### Phase 2：会话层收敛

- `ChatApiController` 迁移到 SSM 写入。
- session event metadata 接入 trace。
- 补断线重连和历史读取测试。

### Phase 3：子代理生命周期收敛

- `SubAgentTool` 改为调用 manager。
- manager 统一写子代理状态、SSM 和 activity。
- 补 terminal state 幂等测试。

### Phase 4：事件系统可观测性

- event envelope 接入 trace metadata。
- publish/enqueue/dispatch/handler 写 activity。
- 保持内存队列实现，但接口语义对齐未来持久队列。

### Phase 5：诊断 API 和最小界面接入

- 新增 runtime activity 查询 API。
- 前端或管理后台增加最小 timeline 数据接入点。
- 暂不做复杂可视化，先保证数据可查。

## 11. 验收标准

第一阶段完成后，必须满足：

1. `session_event_log` 和 `session_sub_agents` 不会在启动时被删除。
2. Chat API 主路径写入 SSM，旧 hub 不再是状态源。
3. 子代理生命周期由 manager 统一管理。
4. 任意一次用户消息能通过 `traceId` 串起 connector/event/session/execution/sub-agent 活动。
5. 日志包含统一 trace metadata。
6. 诊断 API 能返回近期 runtime activity。
7. 新增/修改测试覆盖 P0 行为。
8. 不引入新的匿名诊断接口。

## 12. 风险和约束

1. 当前工作区存在既有未提交状态，实施时必须只提交本阶段相关文件。
2. `AgentExecutionService` 较大，实施时应避免大规模重排，优先通过小接口接入观测。
3. 前端诊断界面不应先行复杂化，否则会拖慢基础设施收敛。
4. 可观测性 sink 不能成为主路径单点故障。
5. trace metadata 不能泄露 prompt、API key、connector secret 或完整用户敏感 payload。

## 13. 后续阶段衔接

本阶段完成后，下一阶段建议按顺序推进：

1. SQLite 持久事件队列，补 lease、retry、dead-letter。
2. LLM sync/stream gateway 统一。
3. Connector ingress service 拆分。
4. ContextPipeline token estimator 和持久 session summary。
5. 潜意识 LLM retrieval + rerank。

