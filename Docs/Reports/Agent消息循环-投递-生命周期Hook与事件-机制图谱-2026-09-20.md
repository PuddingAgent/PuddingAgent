# PuddingAgent 消息循环 / 消息投递 / 生命周期 Hook 与事件 —— 机制图谱

> 取证日期：2026-09-20
> 取证方式：代码逐行阅读（含 `code_outline` / 定向 grep）+ 平台库只读 SQL 取证 + 运行日志
> 证据脚本：`temp/loop_probe.py`、`temp/heartbeat_probe.py`（临时探针，随 temp 清理）
> 相关文档：ADR-059（**已实施**）、ADR-056（已实施）、`Docs/deepseek-harness-pi-plugin-hook-event-architecture-2026-08-14.md`（**Proposed，未实施**）

---

## 1. 结论速览

| 问题 | 结论 |
|---|---|
消息循环的物理载体 | **不是循环，而是「投递 → 受理 → Turn → 事件」的链**；Agent 侧的"循环"是 Turn 内 LLM⇄工具的多轮 |
心跳如何进入 Agent | 合成 `MessageEnvelope`（From=`system/heartbeat`）→ 消息管道 → durable delivery → canonical Turn → Agent Run |
心跳的 Turn 落在哪 | **目标 Agent 的 `profile.MainSessionId`**（不是工作区、不是房间） |
心跳 Turn 的身份 | `clientRequestId = FabricId("fabric-turn-request", deliveryId)`、`clientMessageId = FabricId("fabric-turn-message", deliveryId)` —— **由 deliveryId 派生**，与心跳消息 ID 无关 |
心跳是否在工作 | **在工作**（库中 1165 条心跳消息；审计员每 ~91 分钟被唤醒一次并产出报告，1:1 对应） |
为何只有审计员有心跳 | 见 §8（三个机制叠加，非单一 bug） |

---

## 2. 五类协作合同与三层 Hook（实际代码）

目标架构文档（2026-08-14）确立了五类协作合同。**代码里实际存在的**如下：

### 2.1 五类合同

| 合同 | 实际接口/位置 | 说明 |
|---|---|---|
Command | `ISubmitTurnHandler.HandleAsync(SubmitTurnCommand)` | 唯一 Chat 受理入口（ADR-059） |
Function/Capability | DI 注册的 typed service（`IWorkspaceAgentQueryService`、`IRuntimeAgentDispatcher`…） | 同步强类型调用 |
Hook（**框架级**） | `IHookPublisher.PublishAsync(HookEventName, payload)` → 内部事件管道 | ⚠️ 命名是 Hook，**语义是异步事件**（目标文档 §5.1 已指出该偏离） |
Event | `IInternalEventBus` + `IPriorityEventQueue` + `conversation_events` | 进程内总线 vs 持久领域事件，**两套** |
Projection | `session_projection_cursors` + `conversation_heads` + 前端 `messageProjection.ts` | 从事件算当前状态 |

### 2.2 三层 Hook（容易混淆，必须分清）

| 层 | 接口 | 粒度 | 触发点 |
|---|---|---|---|
**框架级** | `IHookPublisher` / `IAgentHook` | 粗 | `session.compressed`、`session.compaction_failed`、`agent.loop.completed` |
**Agent Loop 级** | `IAgentLoopHook`（`Services/AgentLoop/IAgentLoopHook.cs`） | **细，12 个节点** | `OnLoopStart` / `OnRoundStart` / `OnToolCall` / `OnToolResult` / `OnRoundComplete` / `OnLoopComplete` / `OnCompleted` / `OnCancelled` / `OnWaiting` / `OnFailed` / `OnMaxRoundsReached` / `OnLoopError` |
**工具级** | `IAgentHook` | 粗 | `OnPreToolCall` / `OnPostToolCall` / `OnPreReply` / `OnPostReply` |

`IAgentLoopHook` 的停止原因枚举（`AgentLoopStopReason`）是 Loop 的真实终态词典：
`Done` / `MaxRoundsReached` / `Cancelled` / `Waiting` / `Failed` / `MaxElapsedReached` / `BudgetExhausted`。

**已注册的 Hook 实现**：`LoggingAgentLoopHook`、`SessionCompressedMemoryMaintenanceHook`、`SubconsciousConsolidationHook`、`EmbeddingGenerationHook`。

### 2.3 关键差距：缺失 `agent_settled`

目标文档 §8.7 要求区分 `agent.run.completed`（模型循环结束）与 `agent.run.settled`（无 retry / 无 compaction / 无 queued follow-up / 无待收子代理）。
**实测 `agent.run.*` 事件为 0 条**，`settled` 语义不存在。这正是心跳调度缺少"安全重新调度点"的根源（§8）。

---

## 3. 消息投递全链路（8 跳）

```
① 生产者
   HeartbeatOrchestrator（PuddingHost/Services/HeartbeatService.cs，类名 HeartbeatOrchestrator）
     └ 合成 MessageEnvelope{ From=system/heartbeat, To=agent:<id>, Content="[HEARTBEAT]…" }
     └ messageSystem.SendAsync(envelope)

② 消息落库 + 投递行
   room_messages（消息事实，from_kind/from_id 区分来源）
   message_deliveries（每收件人一行：target_id, handling_mode, status, attempt_count, ack_at）
   └ 发布 InternalEvent type="message.deliver"

③ 订阅与分派
   MessageDeliveryDispatcher（IHostedService）
   StartAsync → 订阅 InternalEventBus：pattern="message.deliver" + "agent.availability.changed"
   HandleAsync → HandleMessageDeliverAsync

④ 判定
   isHeartbeat      = From.Kind==System && From.Id=="heartbeat"
   isForeground     = gateway ingress || canonical_turn || From.Kind==User
   handlingMode     = NormalizeHandlingMode(payload.HandlingMode, metadata)
                      ├ 显式 notify/execute 优先
                      └ 否则 ResolveHandlingMode：requires_response=true→execute；
                        intent∈{inform,report_result,agent_reply}→notify；其余→execute
   notify → DrainNotificationDeliveriesAsync（独立排空，不执行）并 return
   foreground → CancelActiveHeartbeat + wakeQueue.NotifyUserActivityAsync（**清除该 Agent 的 sleep**）

⑤ 准入与领取
   isHeartbeat 绕过 CanStartBackground 前置检查（:325），但在 claim 后复查（:472）
   IsTargetBusy 冷却检查（非 foreground）
   inbox.ClaimNextAsync(lease=DeliveryLeaseDuration) → MessageInboxItem
   heartbeat 前置防火墙：AgentFirewall.EvaluateAsync(IsHeartbeat=true)（AgentFirewall.cs:332）
     ├ deny → AckAsync 并丢弃（"expendable"）
     └ allow → 继续

⑥ 受理（关键一步）
   heartbeat / sub-agent 结果 / agent-to-agent / canonical_turn → AcceptCanonicalConversationTurnAsync
     ├ ResolveCanonicalTurnIdentityAsync
     │   ├ agent 无 MainSessionId → 抛异常（重试 3 次后 dead_letter，**绝不回退主会话**）
     │   ├ ConversationId = profile.MainSessionId
     │   └ ClientRequestId/ClientMessageId = FabricId("fabric-turn-{request|message}", deliveryId)
     └ AcceptMessageFabricConversationTurnAsync → ISubmitTurnHandler.HandleAsync(SubmitTurnCommand)
   ⇒ 同一 deliveryId 重放得到同一 Turn 身份（幂等）

⑦ Turn 与 Run
   ADR-059 canonical 链路：durable message + turn + command → turn.accepted
   ChatExecutionWorker 领取 → IExecutionLeaseStore 租约/fencing → IExecutionJournal
   Runtime 执行：RuntimeDispatchRequest → AgentExecutionService → Agent Loop（多轮 LLM⇄工具）

⑧ 事实与投影
   conversation_events（append-only，sequence 单调）
   conversation_heads / session_projection_cursors / ChatMessages
   SSE：GET /api/sessions/{id}/events?from={exclusiveCursor}
```

### 3.1 表与事件对照

| 层 | 表 | 关键列 |
|---|---|---|
消息事实 | `room_messages` | `message_id, from_kind, from_id, content, metadata_json` |
投递 | `message_deliveries` | `target_id, handling_mode, status, attempt_count, defer_count, available_at, lease_until, ack_at` |
Turn | `conversation_turns` | `conversation_id, turn_id, status, accepted_sequence, terminal_sequence, terminal_kind` |
事件 | `conversation_events` | `sequence, type, turn_id, message_id, payload, occurred_at, agent_id` |
会话映射 | `conversation_catalog` | `conversation_id, agent_id, parent_conversation_id, successor_conversation_id` |

⚠️ **时间列单位不一致（易踩坑）**：`conversation_turns.created_at` 是 **epoch 毫秒**，`conversation_events.occurred_at` 是 **ISO8601 字符串**。用同一数值区间同时查两表会静默返回空集（本次取证已踩过一次）。

---

## 4. 一次心跳的端到端实录【已证】

以 2026-09-20 08:10:55（+08:00）审计员那次为例：

| 时刻 | 事实 | 证据 |
|---|---|---|
08:10:55.279 | 心跳消息落库 | `room_messages.message_id=efc9172a481c415b8d043ee0d4425098`, `from_id=heartbeat`, `metadata={agent_id: default.audit-agent.001, source: heartbeat, min_idle_seconds: 5400, max_idle_seconds: 7200}` |
08:10:55.302 | 投递行创建 | `message_deliveries`: target=`default.audit-agent.001`, **handling_mode=execute**, attempt_count=1 |
08:10:55.410 | Turn 受理 | `conversation_events` seq=20852 `turn.accepted`, conv=`42acf68a34a9…`, `userMessageId=fabric-turn-message:9d842bf5…`, `clientRequestId=fabric-turn-request:9…` |
08:10:55.4xx | 开始执行 | seq=20853 `turn.started`；seq=20854 `context`；seq=20855+ `message.thinking_summary.appended` |
08:10:55.555 | 投递确认 | `ack_at=1789863055555`（落库后 253 ms，**执行前即 ACK**） |
08:12:00 | Agent 产出报告 | 审计员发给我 `f817b8161fdb47da83c67724a1e7724c`（handling_mode=notify） |
08:12:09 | Turn 完成 | `conversation_turns`: status=**completed**, terminal_sequence=**21281**（accepted 20852 → 单次心跳产生 **429 条事件**） |

**投递模式分布（全库 5,687 条）**：`execute/delivered` 4,847 · `notify/delivered` 830 · `notify/queued` 7 · `notify/dead_letter` 2 · `execute/queued` 1。
**投递端点**：一个 Agent 可有多个端点 —— `default.<agentId>`、`feishu:feishu-default.<agentId>`、`feishu:<hash>`。

---

## 5. 状态机与不变量

### 5.1 实际状态机（代码强制）

```
Message:   Accepted → Persisted
Delivery:  Queued → Claimed → Delivered(ACK)         ← 心跳：执行前 ACK
                        └→ Retrying(30s 退避, ≥3 次) → DeadLetter
Turn:      accepted → running → completed | failed | cancelled
（目标文档的 run.settled / heartbeat.* 均不存在）
```

### 5.2 不变量（修复时不得破坏）

1. **投递先 ACK、执行后置**：heartbeat 的 delivery 在执行前就被 ACK，Turn 归属由 deliveryId 派生的稳定身份保证，因此**重启不会重复执行同一次心跳**。
2. **心跳可丢弃**：busy / frozen / 前台准入待办 / 防火墙拒绝 → drop + ACK，**不是错误**。
3. **心跳不进上下文**：`ContextPipelineLayers.IsHeartbeatContent` 按 `[HEARTBEAT]` 前缀把心跳排除出历史注入，所以心跳不会污染后续模型输入。
4. **心跳不参与批处理**（`MessageDeliveryDispatcher.cs:541`），避免插队或打断真实消息。
5. **用户消息优先**：foreground ingress 会 `CancelActiveHeartbeat` + `NotifyUserActivityAsync`（清掉该 Agent 的 sleep 登记）。
6. **无 MainSessionId 的 Agent 无法受理 canonical Turn** → 抛异常 → 重试 3 次 → dead_letter。这是"心跳只发给有主会话的 Agent"的**硬约束**。

---

## 6. 事件目录：实测词表 vs 目标词表

### 6.1 实测（`conversation_events`，总计 2,265,095 条）

| 事件类型 | 条数 |
|---|---|
`message.thinking_summary.appended` | 1,535,732 |
`message.content.appended` | 220,802 |
`subagent.tool.started` / `.completed` | 76,843 / 70,726 |
`subagent.round.*` / `subagent.llm.*` | ~61.5K each |
`tool.call.requested` / `.completed` | 33,087 / 33,064 |
`usage.recorded` | 25,959 |
`turn.started` / `turn.accepted` / `turn.completed` | 2,411 / 2,332 / 2,222 |
`context` | 2,401 |
`subagent.run.*`（created/started/completed/failed） | 1,271 / 1,259 / 895 / 253 |
`terminal` | 205 |
`context.compaction.completed` / `.started` | 131 / 120 |

### 6.2 目标架构文档规定但**完全未实现**的事件族（实测 0 条）

`heartbeat.*` · `agent.run.*` · `message.delivery.*` · `llm.request.*` · `session.*` · `tool.call.approval*`

⇒ **`Docs/deepseek-harness-pi-plugin-hook-event-architecture-2026-08-14.md` 是 Proposed 状态，不可当作现状依据**。判断现状请用 ADR-059 / ADR-056（已实施）或直接查库。

---

## 7. 由此定位的缺陷：为什么只有审计员有心跳

三个机制叠加，**每一层单独看都"合理"，合起来造成单点**：

### 7.1 启动只恢复一个 Agent

`HeartbeatOrchestrator.StartAsync`
→ `ResolveDefaultAgentIdAsync()`：优先配置 `Agent:DefaultId`（`appsettings.json` **无此键**）→ 回落到「字典序第一个有 MainSessionId 的启用 Agent」。
排序结果：`default.audit-agent.001` < `default.global_general-assistant.0e0` < `...258`（已禁用）< `...6a8` ⇒ **选中审计员**。
→ 只对该 Agent 调 `RestoreHeartbeatPreferenceAsync`（读其 `heartbeat.json` 并 `EnqueueAsync`）。

**实测 Agent 清单**：`audit-agent.001`（enabled，mainSession=`42acf68a…`）、`...6a8`（enabled，mainSession=`206a9b48…`）、`...0e0`（enabled，无 mainSession）、`...258`（**disabled**）。

### 7.2 唤醒队列是内存态，重启即空

`AgentWakeQueue` 用 `PriorityQueue` + `SemaphoreSlim`，**无持久化**。重启后队列为空，只有 §7.1 那一个 Agent 被重新登记。

### 7.3 "队列为空才补全"几乎不可达

`OnIdleTickAsync` 的补全分支条件是 `CountAsync() == 0`；而每次心跳发送成功后都会 `EnqueueAsync(request.AgentId, skipDelay, request.MaxIdle)` **自我重入队** ⇒ 队列恒 ≥1 ⇒ 补全分支永不执行 ⇒ **其他 Agent 永远不会被登记**。

### 7.4 实证

- 库中 `from_id='heartbeat'` 共 **1165** 条；最近 6 条全部发给审计员，间隔 90.9–92.9 分钟（= 其 `heartbeat.json` 5400s）。
- 发给 `...6a8`（我）的最后一条是 **09-19 13:51:06**，此后**零条**；此前我有心跳是因为**自己调了 `sleep`**（`AgentSleepTool` → `EnqueueAsync`）。
- 修复轮次前我调 `sleep(3600,7200)` 后 `agent_status` 仍显示 `in_queue=false` —— 因为**用户随后发消息**触发 `NotifyUserActivityAsync` 清除了我的登记（不变量 5），而没有任何机制会把我重新加回去。

### 7.5 次生缺陷（同一根因）

- `TryDequeueAsync` **只 `TryPeek` 队首**并只按队首判断 `EarliestWakeAt`，而优先级键是 `LatestWakeAt` —— 键不一致会导致**队首未到期就阻塞后面已到期的条目**（head-of-line blocking，静态可证，尚未实测触发）。
- `EnsureDefaultAsync` 的"队列非空则 no-op"与 §7.3 叠加，使默认心跳无法为新 Agent 生效。

---

## 8. 修复必须遵守的约束（供后续实施）

1. **不得绕过状态机**：任何补全只能在 `EnqueueAsync` / `EnsureDefaultAsync` 层做，不得直接投递消息。
2. **保持 §5.2 六条不变量**。
3. **无 MainSessionId 的 Agent 不可登记心跳**（否则必然 dead_letter）；登记前需校验。
4. **不得高频重试**（目标文档 §8.12：busy 时记录 skipped/rescheduled，不制造"投递中"假象）。
5. **需定义 `settled` 语义或等价安全点**再谈"心跳完成后再调度"；当前实现是"发送成功即重入队"，这是 §7.3 的直接成因。
6. **队列目前是内存态**；若要求重启后恢复全部 Agent，则要么启动时全量恢复（改动最小），要么队列持久化（改动大）。
7. **不得同时存在两个 orchestrator**：`PuddingHost/Services/HeartbeatService.cs:29` 的 `PuddingHost.Services.HeartbeatOrchestrator` 是唯一被实例化的实现（`PuddingServiceCollectionExtensions.Runtime.cs:326-327` 注册）；
   **产品组合根不调用 `AddPuddingRuntime`**（同文件 :248/:354），故 `PuddingRuntime/DependencyInjection.cs:65` 的 `AddHostedService<HeartbeatOrchestrator>()` **永不执行**——该文件（`PuddingRuntime/Services/HeartbeatOrchestrator.cs`）是死代码，应删除以免将来真调用时出现双 orchestrator 争抢同一队列。
8. **修复必须实测验证**：`agent_status.in_queue` 由 false→true；日志出现该 Agent 的 `EnqueueAsync`/`Dequeued`；库中新增 `from_id=heartbeat` 且 `metadata.agent_id` 为目标的记录。

---

## 9. 未验证 / 待确认

| 项 | 状态 |
|---|---|
head-of-line blocking（§7.5） | **静态可证，未实测触发** |
心跳在会话中如何渲染 | `ChatMessages` 存的是 `role='user'` + 原始 `pudding-message` JSON；前端 `isHeartbeatMessage` 判 `role==='system' && sourceKind==='system' && sourceId==='heartbeat'`，**两者对不上**，需有心跳后实看 |
`GoalMode.Enabled=true`（上一轮基于错误前提所改） | 待重新裁决保留或回退 |
`260fe2a1…` / `8fd0f96f…` 两个 mainSession 归属的 Agent | 对应已禁用 Agent，未逐一确认 |
