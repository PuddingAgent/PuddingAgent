# ADR-060：子代理运行可观测性与会话事件投影

状态：已实施第一纵向切片（2026-07-19）

关联：ADR-016、ADR-021、ADR-057、ADR-059

## 1. 目标

当主 Agent 调用 `spawn_sub_agent` 或其 Smart 薄包装
`smart_explore / smart_research / smart_plan / smart_review /
smart_develop / smart_test / smart_deploy` 时，浏览器必须实时获知：

- 子代理稳定运行身份、来源工具、角色和父 Turn；
- Provider/Profile/Model 执行快照；
- 超时、最大轮次等运行限制；
- 上下文装配、当前轮次、LLM 调用和工具调用阶段；
- Token、工具耗时、失败和最终状态；
- 断线或进程重启后可以从 Conversation sequence 补读。

子代理是否可见不得依赖 Smart 工具同步返回，也不得依赖浏览器轮询一张易失状态表。

## 2. 根因

旧实现存在四套不一致身份和状态：

1. Smart 工具使用 `sync=true`，而旧状态登记主要覆盖异步 `SpawnAsync`。
2. `subSessionId` 同时被当作会话、运行和 UI 卡片身份，代码还从字符串格式反推父会话。
3. `AgentExecutionService` 只保存少量运行归档事件，LLM 轮次和工具内部状态不可见。
4. Chat 卡片消费易失 `subagent.spawned/completed` 帧，状态栏每五秒查询
   `session_sub_agents`；断线补读和 live SSE 不是同一事实源。

因此“工具超时”“子代理仍在运行”“终态丢失”在 UI 中表现相同。

## 3. 决策

### 3.1 稳定执行身份

所有 Runtime 和 Tool 边界透传 `RuntimeExecutionIdentity`：

```text
kind
conversationId
turnId / commandId / messageId
runId
toolCallId
parentRunId
invocationId / batchId
originToolId / role
```

主 Agent 身份由 `ExecutionRunCoordinator` 创建；工具调用只补充
`toolCallId`；`SubAgentManager` 创建唯一 `runId` 并派生子代理身份。
禁止再从 `sessionId` 字符串猜测父会话或运行 ID。

### 3.2 同步和异步共享一个生命周期

`SubAgentManager` 在实际调度前统一：

1. 校验不可变 `LlmProfile + LlmConfig`；
2. 分配 `subSessionId` 和 `runId`；
3. 写 `run.json / input.json / events.jsonl`；
4. 进入相同 workspace/template 并发门和超时门；
5. 派发带子代理 `RuntimeExecutionIdentity` 的 `RuntimeDispatchRequest`。

`ExecuteSyncAsync` 不再绕过运行登记和限制器。Smart 工具和直接
`spawn_sub_agent` 共享同一执行路径。

绝对 `ExecutionDeadlineUtc` 由调度器一次确定并传给 Runtime。
Runtime 用它区分 `timed_out` 与用户 `cancelled`，禁止解析异常字符串。

### 3.3 运行事件日志与 Conversation 投影

子代理细节的本地持久事实源仍是 run archive：

```text
data/workspaces/{workspaceId}/agents/{agentId}/runs/{runId}/
  run.json
  input.json
  events.jsonl
  tools.jsonl
  output.md
  errors.jsonl
  conversation-projection.cursor
```

`FileSubAgentRunStore.AppendEventAsync` 先追加 `events.jsonl`，再将尚未投影的
事件按顺序写入 canonical `IConversationEventStore`。游标只在 Conversation
追加成功后推进。`SubAgentConversationProjectionWorker` 周期扫描积压，恢复
“本地已提交、Conversation 尚未提交”的窗口。

投影使用 run 事件原始 `eventId` 作为 Conversation `eventId` 和
`producerEventId`；重复重放由 Event Store 幂等消除。

第一切片保留文件事件日志作为子代理详细审计主存储，数据库只保存 run 查询索引；
后续若需要跨节点高吞吐，可把 `events.jsonl + cursor` 替换为数据库 Outbox，
但不得改变上层事件和 UI 契约。

### 3.4 事件词汇

```text
subagent.run.created
subagent.run.started
subagent.run.context_assembled
subagent.round.started
subagent.round.completed
subagent.llm.started
subagent.llm.completed
subagent.llm.failed
subagent.tool.started
subagent.tool.completed
subagent.tool.failed
subagent.run.completed
subagent.run.failed
subagent.run.cancelled
subagent.run.timed_out
subagent.run.interrupted
```

`llm.completed` 只记录用量和时长；工具事件只记录工具名、参数哈希、时长、
输出长度和错误摘要。不得写 API Key、完整 Prompt、完整工具参数或敏感输出。

### 3.5 终态唯一性

`ISubAgentRunStore.CompleteRunAsync` 是终态仲裁边界：

- Runtime 正常/异常边界和 Manager 调度异常边界都可以尝试提交；
- Store 只接受第一次有效终态；
- Store 先写带稳定 ID `{runId}:{terminalEventType}` 的终态事件，再推进
  `run.json` 和数据库索引；
- 进程在两步之间退出时，重试只会幂等补齐同一个 Conversation 事件；
- `completed/failed/cancelled/timed_out/interrupted` 均为终态。

这样不会出现归档已经终态但 UI 永久 Running，也不会由晚到的失败覆盖已完成运行。

子代理是进程内执行，不具备跨进程续跑能力。`SubAgentConversationProjectionWorker`
在服务启动时以进程启动时间为 fence，扫描此前创建但仍为非终态的 run，并通过同一个
`CompleteRunAsync` 仲裁边界提交 `subagent.run.interrupted`。恢复过程从持久事件
计算已完成轮次和工具统计，不直接篡改 UI，也不把旧 run 猜测为仍在执行。

### 3.6 前端状态权威

`subAgentReducer.ts` 是子代理 UI 投影的唯一纯函数。它同时接入：

- canonical `conversationReducer`，用于 ADR-057 Store；
- 尚未完全退出的 `useChatState` 兼容桥，保证当前 Chat 主链立即可用。

`SubAgentActivityDock` 只消费该投影，不再调用
`GET /api/sessions/{id}/sub-agents`，也不再启动网络轮询；消息列表不消费子代理投影。
运行坞自己的秒级计时器只更新本地耗时和成功态退场显示，不产生请求。

旧 `subagent.spawned/delta/completed` 不进入新 UI 投影。它们没有稳定 `runId`
和可靠终态，历史重放会把孤儿事件错误恢复为永久 Running。开发阶段不为这套易失
协议增加兼容层；只有本 ADR 定义的 canonical 事件可以创建或推进子代理卡片。

### 3.7 断线补读与刷新恢复

Conversation Event 的 `RunId` 是事件信封字段，不得只依赖业务 payload：

- live SSE、gap replay 和 bootstrap 序列化都必须输出 `runId`；
- 新写入的 run archive payload 同时包含 `run_id`，使 `events.jsonl` 自描述；
- bootstrap 通过 Event Store 的类型前缀查询单独加载最近 5000 条
  `subagent.*` 事件，不得让普通消息的分页上限挤掉子代理运行事实；
- 前端在建立 SSE 前用同一个 `subAgentReducer` 折叠 bootstrap 事件，随后再按
  Conversation sequence 接续 live/replay 事件；
- reducer 按 Conversation `eventId` 记录每个 run 已应用事件，bootstrap、gap
  replay 和 live SSE 窗口重叠时不得重复累计 Token、耗时或工具调用；
- bootstrap 与已到达的 live 状态发生竞态时，以每个 run 的
  `lastActivityAt` 较新者为准，禁止累加 Token 或工具次数。

5000 条是当前开发阶段的有界恢复窗口，不是新的事实源。超过窗口的完整审计仍在
run archive；后续若需要任意历史区间浏览，应增加按 run 查询的诊断接口，而不是
恢复旧 `/sub-agents` 轮询。

### 3.8 前端两层呈现

子代理状态按用途拆成两个展示层，禁止把子代理状态塞回消息虚拟列表：

1. `SubAgentActivityDock` 位于 `ChatMain`、`MessageList` 之外，以聊天容器内的
   绝对定位覆盖层固定在右上角，不参与 Flex 布局、不挤占消息宽度，也不随消息滚动；
   每个活动 run 使用独立悬浮图标，悬停显示安全的运行摘要，点击打开详情检查器。
2. 详情检查器显示 reducer 中有界保存的 canonical 活动时间线，包含阶段、轮次、
   模型消息预览、工具名、脱敏后的工具输入/输出预览、用量、耗时和错误摘要。所有
   时间线预览必须经过 KeyVault 脱敏并限制长度；不得展示隐藏原始思维链、完整 Prompt、
   密钥或完整工具输出。检查器必须显示可复制的 `subSessionId` 与 `runId`。选中终态
   run 时，检查器可以按 `runId` 一次性读取 run archive 的 `output.md`，在独立结果
   分区显示返回主 Agent 的完整子代理输出；Conversation Event 的 `result_summary`
   只能作为摘要，禁止冒充完整结果。该只读加载不得修改 reducer 状态，也不得轮询。
3. 消息流只投影用户消息、主 Agent 消息和系统消息，不渲染子代理卡片或因果锚点。
   子代理与父执行的关系由 `parentTurnId / parentRunId / parentToolCallId` 保存在
   canonical 事件和检查器中；不得为了展示关系而给每个 run 永久增加消息虚拟项。

成功 run 在运行坞保留短暂确认时间后自动退场；失败、超时和中断必须保留到用户查看
或显式移除。完整历史仍由检查器和诊断页面访问，不能重新引入第二份网络状态。

首版实时粒度定义为 canonical 事件边界：LLM 调用完成、工具开始/完成、轮次切换和
终态提交后立即经现有 Conversation SSE 到达前端。它不是逐 Token 文本流。后续若
增加 `subagent.message.delta`，也只能承载可公开的模型消息增量，不能承载隐藏原始
思维链，并且仍必须进入同一 Event Store、sequence 和 reducer。

### 3.9 Smart 执行预算与有界委派图（2026-07-19 演进）

七个 Smart 工作流工具的默认 `timeout_seconds` 统一为 1800 秒。调用方仍可显式缩短
预算，但不得超过平台 `MaxSubAgentTimeoutSeconds` 总护栏。超时预算由 Smart 工具
写入 `spawn_sub_agent` 请求，`SubAgentManager` 只负责建立绝对
`ExecutionDeadlineUtc`；Runtime 不得再从异常文本猜测超时。

Smart 工具之间不采用任意递归，而采用静态有向无环调用图：

```text
Main Agent
  └─ smart_plan
       └─ smart_explore（最多一层，用于补齐规划所缺的关键代码事实）
```

- `smart_plan` 对主 Agent 暴露为 `MainAgentOnly`，因此 Planner 子代理不能再次调用
  `smart_plan`；
- `smart_explore` 暴露为 `DelegatedSubAgent`，只有父执行显式设置
  `AllowSubDelegation=true` 且 `DelegationDepth < MaxDelegationDepth` 时才可见；
- Planner 的 capability whitelist 只增加 `smart_explore`，不包含其他 Smart 工具
  和通用 `spawn_sub_agent`；
- Explorer 派生子代理时写入 `AllowSubDelegation=false`，终止下一层委派；
- `PuddingToolRegistry` 在执行边界再次检查 exposure/depth。即使模型伪造工具名，
  也不能绕过 schema 可见性或 capability 策略形成循环。

嵌套执行必须区分两个身份：

- `AgentInstanceId`：当前临时子代理执行身份；
- `ConfigurationAgentInstanceId`：持久 Agent 配置所有者，用于
  `AgentProfileProvider` 读取角色模型。

Planner 调用 Explorer 时，Explorer 模型仍从根 Agent manifest 的
`explorerModel` 读取，禁止用临时子代理 ID 查找并不存在的实例配置。

取消与超时共享同一终态提交路径：

1. `LlmInvocationService` 对调用方取消重新抛出 `OperationCanceledException`；
2. Runtime 使用绝对 deadline 将其分类为 `timed_out`，用户操作分类为
   `cancelled`；
3. 同步与 SSE 路径都只提交一个 terminal，并从 journal/事件累计真实轮次和工具数；
4. 取消或超时后不再执行记忆写回、自动压缩或潜意识回退任务。

## 4. 组件边界

| 组件 | 负责 | 不负责 |
|---|---|---|
| `SmartWorkflowToolBase` | 从配置所有者选择角色模型、声明 1800 秒默认预算和有界委派元数据；校验 Smart 子代理返回的 canonical 详细工作报告 | 创建 run、持久状态、任意递归或发明第二种结果格式 |
| `SubAgentTool` | 参数/权限/路由快照映射 | 重新解析 Provider、写 UI 状态 |
| `SubAgentInvocationService` | 生成 invocation/batch 身份、映射请求 | 执行或归档 |
| `SubAgentManager` | 并发、超时、run 创建、Runtime 派发 | 解析会话字符串、维护第二套终态 |
| `AgentExecutionService` | 发出真实轮次/LLM/工具执行事实 | 直接写 Conversation SSE |
| `PuddingToolRegistry` | 在工具执行边界强制 exposure、委派开关和深度限制 | 根据提示词推断调用是否合法 |
| `ISubAgentRunStore` | 运行审计、终态仲裁、可靠投影 | Agent 编排 |
| `SubAgentConversationProjectionWorker` | 启动时仲裁旧非终态 run 为 interrupted，并补投积压事件 | 创建或执行 run |
| `Conversation Event Store/SSE` | sequence、补读、实时分发 | 子代理业务状态推断 |
| `subAgentReducer` | 幂等 UI 投影 | 网络请求 |
| `SubAgentActivityDock` | 固定运行态、详情检查器、本地退场策略；终态 run 按 ID 一次性读取不可变归档输出 | 轮询运行状态、用归档输出修正 reducer、进入虚拟消息流 |

## 5. 验收

1. 七个 Smart 工具和直接 `spawn_sub_agent` 都产生稳定 `runId`。
2. `run.created` 在第一次 LLM 调用前包含模型、角色、超时和最大轮次。
3. 每次 LLM 与工具调用产生 started + completed/failed 配对事件。
4. 同步调用也实时显示轮次和工具，而不是等待工具返回后一次出现。
5. 超时显示 `timed_out`，用户取消显示 `cancelled`。
6. 刷新或 SSE 重连后按 Conversation sequence 恢复相同运行状态，Token、
   工具次数、模型、轮次和终态不归零。
7. 终态重复提交不会产生第二个可见终态。
8. Chat 状态面板不再请求旧 `/sub-agents` 轮询接口。
9. run archive 不包含密钥、完整 Prompt、隐藏原始思维链或完整工具输出；模型消息、
   工具输入和工具输出只能保存经过 KeyVault 脱敏的有界预览及截断标记。
10. 服务重启后，上一进程遗留的非终态 run 显示 `interrupted`，不得永久 Running。
11. 同一 `eventId` 经 bootstrap/replay/live 重复到达时，Token 和工具次数只计算一次。
12. 用户滚动到任意历史位置时，活动子代理仍在固定运行坞中可见。
13. 消息流不渲染子代理卡片或锚点；无论启动多少 run，虚拟消息项数量都不因
    子代理状态变化而增加。运行入口、历史和详情统一由固定运行坞与检查器提供。
14. 成功 run 自动退出运行坞；失败、超时和中断在用户确认前保持可见。
15. 检查器显示可复制的子代理 Session ID、Run ID，并通过同一
    bootstrap/replay/live reducer 实时追加模型消息和工具调用时间线。
16. 检查器的时间线与完整结果是两个独立滚动分区；结果区在常规桌面高度下不得被
    时间线压缩为不可读条带，也不得产生整页横向滚动。
17. 终态检查器从 run archive 读取 `output.md` 并标记为“返回主 Agent 的完整结果”；
    UI 不得把 `result_summary` 或模型消息预览标记为完整结果。
18. 七个 Smart 工具未显式传入预算时均使用 1800 秒，run archive 中的 deadline 与
    该预算一致。
19. Planner 子代理只能看到并调用 `smart_explore`；Explorer 不能继续派生 Smart
    子代理，伪造 `smart_plan` 或越过最大深度的调用在 Runtime 边界被拒绝。
20. deadline 到达显示 `timed_out`，用户取消显示 `cancelled`；两者的 terminal
    `rounds/tools/tokens` 与此前已写入的 run 事件一致，不得归零。

## 6. 后续清理

完成当前 Chat 到 ADR-057 canonical Store 的整体迁移后：

1. 删除 `useChatState` 的子代理兼容桥；
2. 删除旧 `SessionStateManager` 子代理写路径和 `/sub-agents` UI 查询；
3. 让诊断详情 API 直接读取 `ISubAgentRunStore`；
4. 增加运行树、批量子代理聚合、Token 速率和停滞告警；
5. 评估把文件投影游标升级为数据库 Outbox，但保持本 ADR 事件契约不变。
