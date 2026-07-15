# ADR-057 前后端可靠 SSE 与 Conversation 事件流架构

## 状态

Proposed

## 日期

2026-07-15

## 决策类型

Breaking Change。本文定义全新的前后端会话事件协议，不要求兼容现有 SessionStateManager、旧 SSE 帧、旧 replay 接口或前端 useChatState 状态链。

## 范围

- 浏览器与 Agent 之间的可靠会话观察层
- Conversation、Turn、Command、Run、Message 和 Event 的领域边界
- Agent 输出持久化、增量订阅、离线恢复和历史加载
- 后端命令受理、执行调度、事件存储、投影和 SSE Gateway
- 前端 Bootstrap、Event Reducer、Connection Manager、Command Outbox 和 Conversation Store

## 关联决策

- [ADR-016 会话状态层与客户端解耦](16会话状态层与客户端解耦ADR.md)
- [ADR-031 聊天历史转录持久化与事件日志回放边界](32ADR-031聊天历史转录持久化与事件日志回放边界.md)
- [ADR-050 会话层统一投影与前端观察者模型](51ADR-050会话层统一投影与前端观察者模型ADR.md)
- [ADR-053 前端会话引用生命周期与 SSE 清理边界](54ADR-053前端会话引用生命周期与SSE清理边界ADR.md)
- [ADR-056 聊天消息受理与可靠事件流架构](57ADR-056聊天消息受理与可靠事件流架构ADR.md)

在本文范围内发生冲突时，以 ADR-057 为准。

---

## 1. 背景与问题

Pudding 的原始目标是：浏览器只是 Agent 会话的观察者。浏览器关闭、刷新或离线时，Agent 仍然可以继续执行；浏览器重新上线后，可以恢复离线期间没有加载的输出。

实现该目标不能依赖以下机制作为事实源：

- HTTP 请求生命周期
- SSE 连接
- 内存 Channel
- 浏览器 React 状态
- 定时 replay poll
- 尚未提交到持久层的实时帧

这些机制都可能随进程、网络或浏览器生命周期消失。

本文将会话层重新定义为持久 Conversation Event Log。Agent 只向持久事件日志写入输出，SSE 只负责把已提交事件投递给浏览器。内存通知仅用于降低延迟，不承担可靠性。

---

## 2. 架构目标

系统必须提供以下保证：

1. 浏览器断开不取消 Agent 执行。
2. Agent 输出必须先持久化，事务提交后才能对浏览器可见。
3. 同一 Conversation 内事件拥有严格递增的 sequence。
4. 浏览器可以从最后成功应用的 sequence 继续恢复。
5. SSE 允许重复投递，但前端应用结果必须幂等。
6. 通知丢失、重复或合并不会造成事件丢失。
7. 慢浏览器不能阻塞 Agent Runtime。
8. 每个已受理 Turn 恰好拥有一个业务终态。
9. Snapshot 加增量事件的结果必须等于从头完整重放的结果。
10. 所有跨网络和跨进程边界均使用稳定 ID，而不是文本、时间或对象引用推断身份。

系统不承诺端到端 exactly-once。网络投递采用 at-least-once，前端通过 sequence 和 eventId 实现幂等效果。外部工具和 LLM 副作用只有在目标系统支持幂等键时才能实现业务级 exactly-once。

---

## 3. 核心决策

### ADR-057-A：Conversation Event Log 是唯一事实源

Agent 输出的权威路径为：

    Agent Runtime
      -> Output Chunker
      -> Conversation Event Writer
      -> SQLite Conversation Event Log
      -> Commit
      -> Committed Head Signal
      -> SSE Delivery Gateway
      -> Browser Event Reducer

禁止以下路径：

    Agent -> Channel/SSE -> later persistence

    Agent -> Browser connection -> transcript reconstruction

    Controller -> consume internal SSE -> rewrite execution facts

### ADR-057-B：命令上行使用 HTTP，事件下行使用 SSE

- 用户发送、取消、steering 等命令使用 HTTP。
- Agent 输出、Turn 状态、工具调用和 Usage 使用 SSE 下行。
- 浏览器断开 SSE 不影响已经受理的命令。
- Agent Runtime 不感知 HTTP、SSE 或浏览器连接。

### ADR-057-C：内存 Channel 只发送 Committed Head

内存通知只携带：

    conversationId
    committedThroughSequence

通知不携带事件正文。通知可以重复、合并或丢失。SSE Gateway 收到通知后，始终从 SQLite 读取 sequence 大于 lastSent 的已提交事件。

因此 Channel 容量可以为 1，并只保留最新 Head。它是低延迟唤醒器，不是可靠队列。

### ADR-057-D：网络层 at-least-once，Reducer 幂等

服务端可以重复发送事件。浏览器仅在以下条件下推进 cursor：

- 事件 sequence 等于 localCursor + 1；
- Event Reducer 已成功应用事件；
- canonical Conversation Store 已完成状态提交。

sequence 小于等于 localCursor 的事件视为重复并忽略。sequence 大于 localCursor + 1 表示出现缺口，必须暂停 live 应用并执行 gap recovery。

### ADR-057-E：Conversation、Turn、Run、Message 分离

不得继续使用一个 Session 状态表达浏览器连接、Agent 执行、单轮消息和长期会话。

| 标识 | 语义 |
|---|---|
| conversationId | 用户可长期继续的会话 |
| turnId | 一次用户输入以及对应 Agent 结果 |
| commandId | 一次被系统可靠受理的执行命令 |
| runId | Worker 的一次执行尝试 |
| messageId | 一条用户或 Agent 消息 |
| eventId | 全局唯一事件 |
| sequence | Conversation 内单调递增游标 |
| clientRequestId | 浏览器提交幂等键 |
| fencingToken | Worker 租约代次 |

### ADR-057-F：历史 Projection 与原始事件日志分离

- Conversation Event Log 保存执行事实、恢复证据和审计信息。
- Conversation Projection 保存适合 UI 展示和分页的 Message、Turn 和 Tool Timeline。
- 浏览器首次加载使用 Bootstrap Snapshot。
- 浏览器只从 snapshotCursor 之后消费增量事件。
- 历史向前分页使用 messageCursor，不复用事件 sequence。

---

## 4. 总体模块

### 4.1 Conversation Domain

负责：

- Conversation、Turn、ExecutionRun 状态机
- 领域事件类型
- 状态转换和终态约束
- 写入条件和不变量

不得引用 ASP.NET、EF Core、SSE、Channel 或 React。

### 4.2 Command Ingress

负责：

- ACL 校验
- clientRequestId 归一化和并发幂等
- 创建 Turn 和 execution command
- 追加 turn.accepted
- 返回稳定 ID 和 acceptedSequence

### 4.3 Execution Scheduler

负责：

- 原子领取命令
- 租约续期
- fencing token
- 同一 Conversation 串行
- 不同 Conversation 并行
- Worker 崩溃恢复

### 4.4 Agent Runtime

Agent Runtime 接收 TurnExecutionContext，输出领域事件流，不输出 SSE Frame：

    public interface ITurnExecutor
    {
        IAsyncEnumerable<NewConversationEvent> ExecuteAsync(
            TurnExecutionContext context,
            CancellationToken ct);
    }

### 4.5 Output Chunker

单 token 不直接成为持久事件。

默认聚合策略：

- 20 至 50ms 时间窗口；
- 或累计 1 至 4KB；
- terminal 到达时立即 flush；
- pending content 和 terminal 按顺序在同一批次提交。

### 4.6 Conversation Event Store

负责：

- Conversation 内 sequence 分配
- 原子批量追加
- eventId 和 producerEventId 幂等
- 正向和反向 cursor 查询
- Turn 终态唯一性
- fencing token 校验

### 4.7 SSE Delivery Gateway

负责：

- ACL
- cursor 校验
- catch-up
- live 追赶
- gap recovery
- heartbeat
- 慢连接治理
- ConversationEvent 到 SSE 的序列化

不得负责事件生成和业务状态变更。

### 4.8 Conversation Projector

从 Event Log 按 checkpoint 重放并生成：

- Conversation Projection
- Message Projection
- Turn Projection
- Tool Timeline Projection
- Usage Projection
- Workspace Conversation Summary

Projection 失败不影响已经提交的领域事件。

---

## 5. 状态机

### 5.1 Conversation

    Open -> Archived -> Deleted

Conversation 状态不表示 Agent 当前是否 thinking、等待工具或已经完成某一轮。

### 5.2 Turn

    Accepted
      -> Running
      -> WaitingForTool
      -> Running
      -> Completed | Failed | Cancelled

每个 Turn 恰好一个终态。

### 5.3 ExecutionRun

    Leased
      -> Running
      -> Succeeded | Failed | Cancelled | LeaseLost

LeaseLost 的旧 Worker 即使恢复运行，也不能继续写入事件。

### 5.4 Browser Connection

    Idle
      -> Bootstrapping
      -> CatchingUp
      -> Live
      -> Recovering
      -> Offline
      -> Terminal

Connection 状态只存在于前端运行时，不改变 Conversation 或 Turn 状态。

---

## 6. 统一事件 Envelope

所有持久事件使用统一 Envelope：

    public sealed record ConversationEvent
    {
        public required string EventId { get; init; }
        public required string ConversationId { get; init; }
        public required long Sequence { get; init; }

        public required string WorkspaceId { get; init; }
        public required string TurnId { get; init; }
        public string? CommandId { get; init; }
        public string? RunId { get; init; }
        public string? MessageId { get; init; }

        public required string Type { get; init; }
        public required int SchemaVersion { get; init; }

        public required DateTimeOffset OccurredAt { get; init; }
        public required DateTimeOffset CommittedAt { get; init; }

        public string? CorrelationId { get; init; }
        public string? CausationId { get; init; }
        public string? ProducerEventId { get; init; }

        public required JsonElement Payload { get; init; }
    }

sequence 是 Envelope 字段，不得注入 Payload JSON。SSE id 直接取 Envelope.Sequence。

### 6.1 推荐事件

    turn.accepted
    turn.started
    turn.waiting_for_tool
    turn.completed
    turn.failed
    turn.cancelled

    message.started
    message.content.appended
    message.thinking_summary.appended
    message.completed

    tool.call.requested
    tool.call.completed
    tool.call.failed

    usage.recorded
    run.lease_lost
    conversation.archived

不得向浏览器发送或持久化原始 Chain of Thought。只保存可以安全展示的 thinking summary。

---

## 7. 后端核心接口

### 7.1 Command Service

    public interface IConversationCommandService
    {
        Task<AcceptTurnResult> AcceptAsync(
            AcceptTurnCommand command,
            CancellationToken ct);
    }

### 7.2 Event Store

    public interface IConversationEventStore
    {
        Task<AppendResult> AppendAsync(
            string conversationId,
            long expectedVersion,
            IReadOnlyList<NewConversationEvent> events,
            EventWriteCondition condition,
            CancellationToken ct);

        Task<EventPage> ReadForwardAsync(
            string conversationId,
            long afterExclusive,
            long? throughInclusive,
            int limit,
            CancellationToken ct);

        Task<EventPage> ReadBackwardAsync(
            string conversationId,
            long beforeExclusive,
            int limit,
            CancellationToken ct);

        Task<EventBounds> GetBoundsAsync(
            string conversationId,
            CancellationToken ct);
    }

EventWriteCondition 必须支持：

- runId
- fencingToken
- producerEventId
- Turn 尚未终结
- expected Conversation version

### 7.3 Execution Lease

    public interface IExecutionLeaseStore
    {
        Task<ExecutionLease?> TryAcquireAsync(
            string workerId,
            TimeSpan duration,
            CancellationToken ct);

        Task<bool> RenewAsync(
            string commandId,
            string workerId,
            long fencingToken,
            TimeSpan duration,
            CancellationToken ct);

        Task CompleteAsync(
            string commandId,
            long fencingToken,
            CommandTerminalStatus status,
            CancellationToken ct);
    }

### 7.4 Committed Event Signal

    public interface ICommittedEventSignal
    {
        ValueTask WaitForChangeAsync(
            string conversationId,
            long knownHead,
            CancellationToken ct);
    }

该接口只表示数据库可能存在新事件。调用方必须读取 Event Store 确认实际 Head。

---

## 8. SQLite 数据模型

Pudding 保持单进程、单节点 SQLite 部署模型。开启 WAL，写入事务保持短小。

核心表：

    conversations
    conversation_heads
    conversation_events
    turns
    execution_commands
    execution_runs
    conversation_projections
    projection_checkpoints
    conversation_snapshots

关键约束：

    PRIMARY KEY(conversation_id, sequence)
    UNIQUE(event_id)
    UNIQUE(workspace_id, client_request_id)
    UNIQUE(turn_id, terminal_category)
    UNIQUE(run_id, producer_event_id)

sequence 分配必须在 SQLite 事务中更新 conversation_heads。可使用 BEGIN IMMEDIATE 或原子 UPDATE RETURNING 分配连续区间。进程内计数器只能作为优化，不能成为最终一致性依据。

---

## 9. 事务边界

### 9.1 Turn 受理事务

同一事务完成：

1. 检查 workspaceId 和 clientRequestId 唯一性。
2. 创建 Turn。
3. 创建 execution command。
4. 追加 turn.accepted。
5. 推进 Conversation Head。
6. 提交。

返回 cursor 直接使用 turn.accepted 的 sequence。

### 9.2 输出批次事务

同一事务完成：

1. 验证 command、run 和 fencing token。
2. 验证 Turn 尚未终结。
3. 为事件批次分配连续 sequence。
4. 插入事件。
5. 推进 Conversation Head。
6. 提交。
7. 提交后发送 Head 通知。

### 9.3 Turn 终态事务

同一事务完成：

1. flush 所有 pending content。
2. 追加 turn.completed、turn.failed 或 turn.cancelled。
3. 更新 Turn 状态。
4. 更新 command 状态。
5. 更新 execution run。
6. 推进 Conversation Head。
7. 提交。

---

## 10. HTTP 与 SSE 协议

### 10.1 创建 Turn

    POST /api/v1/conversations/{conversationId}/turns
    Idempotency-Key: {clientRequestId}

请求：

    {
      "text": "用户消息",
      "agentId": "agent-1"
    }

响应：

    HTTP/1.1 202 Accepted

    {
      "conversationId": "conv-1",
      "commandId": "cmd-1",
      "turnId": "turn-1",
      "messageId": "msg-user-1",
      "acceptedSequence": 102
    }

### 10.2 Bootstrap

    GET /api/v1/conversations/{conversationId}/bootstrap

响应：

    {
      "conversationId": "conv-1",
      "snapshotCursor": 100,
      "messages": [],
      "activeTurns": [],
      "hasMoreBefore": true,
      "oldestMessageCursor": "..."
    }

snapshotCursor 表示 Projection 实际应用到的位置，不是 Event Log 当前 Head。

### 10.3 历史消息

    GET /api/v1/conversations/{conversationId}/messages?before={messageCursor}&limit=50

历史分页只使用 messageCursor。

### 10.4 事件补洞

    GET /api/v1/conversations/{conversationId}/events?after=102&through=150&limit=500

after 永远是 exclusive 语义：

    event.sequence > after

### 10.5 SSE

    GET /api/v1/conversations/{conversationId}/stream
    Last-Event-ID: 102
    Accept: text/event-stream

事件帧：

    id: 103
    event: conversation.event
    data: {"eventId":"evt-103","sequence":103,"type":"message.content.appended"}

固定使用 conversation.event，领域类型放在 Envelope.Type 中。

心跳：

    : heartbeat

心跳不推进 cursor。

### 10.6 控制命令

    POST /api/v1/conversations/{conversationId}/turns/{turnId}/cancel
    POST /api/v1/conversations/{conversationId}/turns/{turnId}/steer

---

## 11. 无竞态 SSE 算法

服务端按以下顺序建立流：

1. 校验认证和 Conversation ACL。
2. 解析 Last-Event-ID。
3. 先创建 Committed Head 订阅。
4. 再查询当前已提交 Head，记为 H。
5. 分页读取并发送 (cursor, H]。
6. 消费 Head 通知。
7. 收到 N 后读取 (lastSent, N]。
8. 定期主动查询 Head，防止通知永久丢失。
9. sequence 小于等于 lastSent 的事件直接忽略。
10. 慢连接超过写入超时后主动关闭，让客户端重连。

必须先订阅再读取 Head。反向顺序会在读取 Head 和建立订阅之间产生事件窗口。

如果客户端 cursor 已低于最小可恢复 sequence，在写 SSE Header 前返回：

    HTTP/1.1 410 Gone

    {
      "code": "snapshot_required",
      "minimumAvailableSequence": 12000,
      "snapshotUrl": "/api/v1/conversations/conv-1/bootstrap"
    }

浏览器收到后重新 Bootstrap。

---

## 12. 前端架构

现有 useChatState 不再承担 SSE、cursor、replay、消息归并和命令队列职责。目标目录：

    pages/chat/
      domain/
        contracts.ts
        conversationReducer.ts
        selectors.ts

      transport/
        conversationApi.ts
        conversationSseClient.ts
        sseParser.ts

      runtime/
        conversationConnectionManager.ts
        commandOutbox.ts
        gapRecoveryEngine.ts

      state/
        conversationStore.ts
        localPersistence.ts

      react/
        useConversation.ts
        useConversationCommands.ts

### 12.1 类型分层

只保留三层类型：

1. Transport Event：与后端 Envelope 一一对应。
2. Domain State：Reducer 使用的 canonical state。
3. ViewModel：React 组件展示类型。

不得在 hook、实验 store 和 projection client 中重复声明 ChatTurn。

### 12.2 Conversation Reducer

Reducer 必须是纯函数：

    reduce(
      state: ConversationState,
      event: ConversationEventEnvelope,
    ): ReduceResult

规则：

- sequence 小于等于 cursor：忽略重复事件。
- sequence 等于 cursor + 1：正常应用。
- sequence 大于 cursor + 1：返回 gap，暂停 live 应用。
- 按 turnId、messageId、toolCallId 合并状态。
- Reducer 成功提交后才推进 cursor。
- 禁止按消息文本和时间匹配 pending 消息。

### 12.3 Connection Manager

负责：

- Bootstrap
- fetch-based SSE
- Last-Event-ID
- AbortController
- 心跳超时
- online/offline
- 指数退避和随机抖动
- gap recovery
- Conversation 切换 generation token

不负责 React setState 和领域事件处理。

推荐使用 fetch-based SSE，而不是原生 EventSource，以便显式设置 Authorization、Last-Event-ID、AbortSignal 和重试策略。

### 12.4 Conversation Store

使用 useSyncExternalStore 暴露 canonical state：

    {
      entities,
      orderedMessageIds,
      turns,
      runs,
      cursor,
      connectionState,
      outbox
    }

React 组件只调用 selectors。

### 12.5 Bootstrap 流程

1. 取消旧 Conversation 连接。
2. 分配新的 connection generation。
3. 获取 Bootstrap Snapshot。
4. 用 Snapshot 替换 canonical state。
5. 使用 snapshotCursor 建立 SSE。
6. SSE 从 Event Store 重放 Snapshot 之后的事件。
7. 旧 generation 的异步结果全部丢弃。

### 12.6 Command Outbox

    type OutboxItem = {
      clientRequestId: string;
      localTurnId: string;
      text: string;
      status:
        | "queued-local"
        | "submitting"
        | "accepted"
        | "rejected";
      commandId?: string;
      turnId?: string;
      messageId?: string;
    };

流程：

1. 用户发送后立即创建 optimistic Turn。
2. 使用稳定 clientRequestId 提交。
3. 202 响应绑定服务器 ID。
4. 收到 turn.accepted 后消解 Outbox。
5. 网络失败使用相同 clientRequestId 重试。
6. 只有服务端明确拒绝才标记 rejected。

IndexedDB 可以持久化 Snapshot 和 Outbox，但 cursor 必须与对应 canonical state 一起提交。不能只保存 cursor 而不保存它所代表的状态。

### 12.7 UI 批处理

- canonical Reducer 严格按事件推进 cursor。
- React 展示可以每 16 至 50ms 合并刷新。
- UI 刷新节奏不得决定可靠 cursor。

---

## 13. Workspace 级 SSE

浏览器最多维持两条 SSE：

1. 当前 Conversation 的详细事件流。
2. 当前 Workspace 的低频摘要流。

Workspace 流只发送：

    conversation.created
    conversation.title.changed
    conversation.unread.changed
    conversation.turn.status.changed
    agent.status.changed

Workspace 流不得发送 token delta、thinking chunk 或完整工具输出。

---

## 14. 背压与资源治理

- Event Store 写失败时暂停或失败 Turn，不得继续产生不可恢复输出。
- SSE 写入阻塞时断开浏览器，不阻塞 Agent。
- Catch-up 每页 200 至 500 条。
- Head Signal 只保留最新 Head。
- delta 在持久化前聚合。
- 单事件和单批次设置 Payload 上限。
- 单用户、Conversation 和 IP 设置 SSE 连接数限制。
- 浏览器落后超过阈值时要求重新 Bootstrap。
- Projection 使用 checkpoint 重放。

---

## 15. 鉴权与数据安全

- 所有 Command、Bootstrap、History、Events、Stream、Cancel、Steer 使用相同 ACL。
- cursor 不是授权凭证。
- 不存在和无权限统一返回 404，避免枚举 Conversation。
- ACL 被撤销后，现有 SSE 必须在限定时间内关闭。
- SSE Token 应短期有效并限制到指定用户和 Workspace。
- Provider Secret、内部系统提示词、原始 Chain of Thought 不得进入浏览器事件。
- 对 Payload 大小、重连频率和连接数实施限流。

---

## 16. 保留与压缩

默认策略：

- Conversation Projection 和最终消息长期保留。
- 原始事件按配置保留，默认 30 天。
- 完成 Turn 可以生成 Snapshot。
- 高频 content chunk 可以归档压缩，但不能破坏仍有效 cursor。
- 清理旧事件时同步推进 minimumAvailableSequence。
- 过期 cursor 使用 snapshot_required 恢复。

---

## 17. 实施路线

### Phase 1：协议冻结

- 冻结 ID 体系。
- 冻结 Envelope。
- 冻结事件类型和状态机。
- 冻结 HTTP/SSE API。
- 冻结 cursor、retention 和错误语义。

### Phase 2：Event Store

- conversation_heads
- 原子批量 append
- ReadForward / ReadBackward
- eventId 幂等
- Turn 唯一终态
- fencing token
- Fault Injection 测试

### Phase 3：Command 与 Runtime

- 原子 Accept
- Worker lease / renew / fencing
- ITurnExecutor
- Output Chunker
- 原子 terminal
- 删除内部 HTTP/SSE 回环

### Phase 4：SSE Gateway

- Committed Head Signal
- subscribe-first 算法
- catch-up
- gap recovery
- heartbeat
- slow client handling

### Phase 5：前端核心

- Transport Envelope
- Pure Conversation Reducer
- Conversation Store
- Bootstrap
- Connection Manager
- Gap Recovery
- Command Outbox

### Phase 6：UI 接入

- UI 全部切换为 selector 驱动。
- 删除 useChatState 内 SSE、cursor、replay 和 reducer 逻辑。
- 删除重复 Store 和重复 ChatTurn 类型。
- 删除 replay poll。
- 删除文本和时间去重。

### Phase 7：Projection 与 Retention

- 历史分页切换到 Conversation Projection。
- 建立 Projection checkpoint。
- 建立 Snapshot。
- 建立事件保留和 cursor 过期恢复。

本次允许 Breaking Change，因此禁止为旧协议建立长期双写路径。开发阶段可以用一次性数据迁移或清空开发数据库完成切换。

---

## 18. 验收矩阵

### 18.1 持久化

- commit 前事件对 SSE 不可见。
- SSE 可见事件立即能从 Events API 查询。
- commit 前崩溃不会产生幽灵事件。
- commit 后、notify 前崩溃，重连仍能恢复。

### 18.2 重连

- 浏览器离线期间产生 1、200、10000 条事件，重连全部恢复。
- replay 期间继续产生新事件，无缺失。
- 相同 cursor 重连多次，UI 不重复。
- 通知丢失、重复、合并不影响结果。
- 慢客户端断开后可以从 cursor 继续。

### 18.3 Worker

- 多 Worker 同时领取时只有一个成功。
- lease 到期后旧 Worker 写入被 fencing 拒绝。
- 同一 Conversation 的普通 Turn 不并发。
- 不同 Conversation 可受配额控制并行。
- 每个 accepted Turn 恰好一个终态。

### 18.4 幂等

- 并发提交相同 clientRequestId 只创建一个 Turn。
- 重复 producerEventId 不重复追加。
- 工具副作用使用稳定 operationId。

### 18.5 浏览器

- Reducer 对重复事件幂等。
- 发现 gap 后暂停并自动补洞。
- cursor 只在 canonical state 提交后推进。
- Snapshot 加增量事件等于完整重放。
- 切换 Conversation 后旧连接不能污染当前 UI。
- optimistic Turn 只按 clientRequestId 和 turnId 对账。

### 18.6 鉴权

- 用户不能访问其他 Workspace 的 Bootstrap、History、Events、Stream、Cancel 或 Steer。
- ACL 撤销后现有流会关闭。
- cursor 篡改不能越权。

---

## 19. 结论

Pudding 的会话层不是浏览器连接管理器，而是持久 Conversation Event Log。

最终不变量是：

> Agent 写持久事件库，SSE 从持久事件库追赶；内存通知永远不承载可靠数据。

只要该不变量成立，浏览器离线、刷新、慢消费、通知丢失和进程重启都不会破坏会话事实。

