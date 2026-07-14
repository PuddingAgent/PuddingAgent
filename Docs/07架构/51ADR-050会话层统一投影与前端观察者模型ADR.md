# ADR-050 会话层统一投影与前端观察者模型

> **可靠输入补充（2026-07-13）**：[ADR-056 聊天消息受理与可靠事件流架构](57ADR-056聊天消息受理与可靠事件流架构ADR.md) 为本 ADR 的 ConversationProjection 提供稳定的 `turnId/messageId/sequenceNum`、committed-before-publish 事件输入、投影检查点以及 replay+live 追赶协议。前端观察者模型不得把易失 Channel 或 optimistic state 当作事实源。

> 状态：**Proposed**  
> 日期：2026-06-14  
> 范围：Admin Chat、SessionStateManager、session_event_log、ChatMessages、AgentConversationProjection、子代理状态、系统通知、前端同步模型  
> 关联：[16会话状态层与客户端解耦ADR](16会话状态层与客户端解耦ADR.md)、[20会话状态机与事件规范ADR](20会话状态机与事件规范ADR.md)、[32ADR-031聊天历史转录持久化与事件日志回放边界](32ADR-031聊天历史转录持久化与事件日志回放边界.md)、[46ADR-045双向消息系统与聊天室客户端ADR](46ADR-045双向消息系统与聊天室客户端ADR.md)、[47ADR-046事件驱动多AgentOS交互体验架构ADR](47ADR-046事件驱动多AgentOS交互体验架构ADR.md)

---

## 1. Context

Admin Chat 当前同时消费多类事实和投影：

- `ChatMessages` 用于历史转录。
- `session_event_log` / replay / SSE 用于流式事件追赶。
- Agent conversation projection 用于 Agent-first 会话视图。
- 子代理卡片由前端根据 `subagent.*` 事件合并。
- Composer 和状态栏分别读取上下文、缓存、子代理、队列、运行态等状态。

这导致前端承担了过多后端语义：

- 前端需要知道 `metadata`、`delta`、`thinking`、`tool_call`、`error`、`done`、`subagent.spawned`、`subagent.completed` 等事件如何归并。
- 前端需要维护 `messageId -> turnId`、active message、replay cursor、子代理卡片排序、本地队列等运行态。
- 断线、刷新、重复发送同一句、后端错误、子代理完成顺序等场景容易出现 UI 空白、重复、丢失或顺序漂移。
- 后端已经拥有会话层和事件日志，但 UI 仍绕过统一会话投影直接理解低层事件。

2026-06-14 的“发送 `你好` 后界面无反应”诊断说明了这个边界问题：后端已经收到 `POST /api/workspaces/default/chat/message`，写入 `ChatMessages` 和 `session_event_log`，并推送 error/done；前端仍可能因为本地 pending 合并、重复文本去重、事件映射或投影刷新时机而表现为无反馈。直接继续修前端条件分支只能缓解局部问题，不能消除架构摩擦。

---

## 2. Decision

Pudding 的会话层必须成为 UI 唯一事实源。前端是纯观察者，只向后端发送命令，并订阅会话层产出的 UI 投影，不再直接解释低层 runtime event。

### 2.1 事实源与投影源分离

- `session_event_log` 保持 append-only 原始证据源，负责诊断、审计、回放、执行事实还原。
- `ChatMessages` 保持聊天转录物化表，负责历史分页、检索、token 统计和兼容旧 API。
- 新增或演进 `ConversationProjection`，作为 `/admin/chat` 的唯一 UI 事实源。
- 前端默认只消费 `ConversationProjection` 的快照和增量帧。原始事件只进入开发者诊断、trace report 和 Inspector，不进入普通消息流组件。

### 2.2 会话层统一归并所有可见输出

所有发送给用户的可见内容都必须先进入会话层投影：

- 用户消息。
- Agent 消息。
- 系统通知和系统错误。
- LLM/API 错误，例如 Unauthorized、rate limit、context overflow。
- Session fuse、cancelled、timeout、tool blocked 等终态。
- 子代理 spawned/running/completed/failed 状态。
- 工具调用摘要、思考摘要、上下文压缩结果、记忆/索引/后台整理等过程摘要。

会话层负责排序、去重、原子终态覆盖和唯一性。前端不再自行判断哪个事件更早、哪个事件应该覆盖哪个卡片、哪个 error 应该作为气泡还是状态行。

### 2.3 前端只消费会话层通讯

面向 Chat UI 的核心接口应收敛为：

```http
GET /api/sessions/{sessionId}/conversation?after={sequence}
GET /api/sessions/{sessionId}/conversation/stream?after={sequence}
POST /api/workspaces/{workspaceId}/agents/{agentId}/messages
POST /api/sessions/{sessionId}/commands/cancel
POST /api/sessions/{sessionId}/commands/steer
```

`POST` 是命令入口，不是 UI 状态来源。命令提交成功后，前端可以显示极短生命周期的 optimistic pending item，但它必须被会话投影覆盖或撤销。

### 2.4 统一序列与离线追赶

`ConversationProjection` 的每个增量帧必须具备：

- `sequenceNum`：会话内单调递增。
- `recordedAt`：后端记录时间。
- `projectionId`：投影项稳定 ID。
- `revision`：同一投影项的版本号。
- `kind`：投影项类型。
- `status`：发送、运行、成功、失败、取消等状态。

前端离线、刷新或重新连接后，只需要用 `after=lastSequenceNum` 追赶 projection frames。若本地 cursor 过旧，后端可以返回 snapshot + cursor。

### 2.5 子代理作为会话投影项

子代理不再由前端根据 `subagent.*` 事件自行合并和排序。会话层将子代理运行归并为：

- 独立 `kind=sub_agent_run` 投影项；或
- 某个 agent message 下的 `processItems`。

首阶段建议使用独立 projection item，因为它能清晰表达子代理生命周期，也便于后续进入子代理管理器和诊断面板。其排序、终态、输出摘要、失败原因全部以后端投影为准。

### 2.6 系统错误必须成为可见投影

所有会影响用户理解的错误，都应被投影为 `system_notification` 或失败的 `agent_message`：

- LLM provider Unauthorized。
- session fuse triggered。
- tool approval rejected。
- tool execution failed。
- context assembly failed。
- sub-agent task failed。

错误事件不得只存在于 `session_event_log` 或普通日志里。用户刷新页面后仍应看到明确失败消息和恢复建议。

---

## 3. Consequences

正向影响：

- 前端状态机显著简化，减少 `useChatState` 中事件映射、replay、子代理排序和本地队列推断。
- 离线、刷新、重连、多浏览器观察同一会话时行为一致。
- 子代理、系统错误、工具过程和 Agent 输出拥有统一排序和唯一性。
- 诊断仍保留原始事件证据，但普通 UI 不被低层事件污染。
- 后端自治更完整：agentd、消息队列和会话层可以在浏览器断开后继续工作。

代价：

- 后端需要维护稳定的 projection contract。
- 原始事件到 projection 的 reducer 需要测试覆盖，否则会把复杂度从前端搬到后端后失去可观察性。
- 短期需要兼容现有 `/messages`、`/replay`、`/agents/{agentId}/conversation`。
- 迁移期间前端可能同时存在旧事件流和新 projection 流，需要明确切换开关。

---

## 4. Rejected Options

### A. 继续让前端直接消费低层事件

不采纳。该方案会持续扩大 `useChatState` 的复杂度，重复出现事件顺序、映射、恢复、子代理卡片和错误可见性问题。

### B. 只依赖 `ChatMessages`

不采纳。`ChatMessages` 是转录视图，适合分页和检索，但无法表达运行中状态、子代理生命周期、工具过程、系统通知 revision 和投影 cursor。

### C. 只保留 `session_event_log`，前端每次重放原始事件

不采纳。原始事件是证据层，不是 UI 模型。把每次页面加载都变成事件重放会让前端继续理解后端内部协议，也不利于性能和稳定渲染。

---

## 5. Implementation Notes

### 5.1 后端新增会话投影 reducer

建议新增：

- `IConversationProjectionService`
- `IConversationProjectionReducer`
- `ConversationProjectionItem`
- `ConversationProjectionFrame`
- `ConversationProjectionStore`

Reducer 从 `session_event_log`、`ChatMessages`、`sub_agent_runs`、message delivery 状态等事实层构建 projection。首阶段可以按需实时构建，后续再物化为表以优化性能。

### 5.2 投影项类型

首批 `kind`：

- `user_message`
- `agent_message`
- `system_notification`
- `sub_agent_run`
- `process_summary`
- `status_marker`

其中 `process_summary` 可以内嵌到 message，也可以作为独立项；首阶段优先内嵌，避免消息流过碎。

### 5.3 前端迁移策略

迁移应分阶段：

1. 后端提供 `conversation` snapshot，与现有 AgentConversationProjection 并存。
2. 后端提供 projection stream/replay，帧格式不暴露 runtime event type。
3. 前端新增 `ConversationProjectionStore`，只渲染 projection item。
4. 移除前端对 `subagent.*`、`error/done/delta` 的业务解释，只保留开发者诊断入口。
5. 删除本地队列事实维护，队列状态由后端投影或专用只读 projection 提供。

### 5.4 可观测性

Projection reducer 必须记录结构化指标：

- 原始事件数量。
- 投影项数量。
- reducer 耗时。
- projection revision 数量。
- unmapped event 数量和 event type。
- duplicate/drop/merge 次数。
- 每个会话最新 cursor。

原始事件无法归并为 projection 时，不应静默丢弃；应写诊断事件，并在开发者 Inspector 可见。

---

## 6. Acceptance Criteria

1. 发送同一句消息多次，前端每次都有可见 pending，并最终被后端 projection 覆盖。
2. LLM Unauthorized、session fuse 等错误刷新后仍在消息流或系统通知中可见。
3. 前端断开 SSE 后刷新页面，可通过 conversation snapshot 看到完整最新状态。
4. 子代理卡片顺序以后端 `recordedAt/sequenceNum` 为准，刷新后不漂移、不重复。
5. 前端普通消息流不再直接 switch 低层 runtime event type。
6. `session_event_log` 仍可完整用于诊断，projection 不替代原始证据层。

