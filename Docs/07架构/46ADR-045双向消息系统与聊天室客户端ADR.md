# ADR-045：双向消息系统与聊天室客户端架构

> 状态：**Proposed**  
> 日期：2026-05-28  
> 范围：消息系统、聊天室、Agent-to-Agent、Connector、Cron、会话层、事件系统、Admin Chat UI  
> 关联：[10事件系统与事件总线](10事件系统与事件总线.md)、[16会话状态层与客户端解耦ADR](16会话状态层与客户端解耦ADR.md)、[24核心架构组件边界与执行引擎拆分ADR](24核心架构组件边界与执行引擎拆分ADR.md)、[32ADR-031聊天历史转录持久化与事件日志回放边界](32ADR-031聊天历史转录持久化与事件日志回放边界.md)、[49ADR-048Hermes型系统开发方向参考ADR](49ADR-048Hermes型系统开发方向参考ADR.md)

---

## 2026-07-18 实现边界修订

`message.deliver` 的唯一自动消费方确定为 Runtime 内的 `MessageDeliveryDispatcher`，不再由通用 `AgentEventHandler` 执行。`MessageDelivery` 持久化状态是可靠性权威，内部事件只是低延迟唤醒信号。

`MessageDeliveryDispatcher` 必须同时满足：

- 以 Hosted Service 启动并订阅 `message.deliver` / `agent.availability.changed`。
- 每轮恢复从 `IMessageInbox` 查询存在 `queued/retrying` 投递的 Agent 目标，不能只依赖进程内见过的目标。
- 通过原子 claim 进入 Runtime，成功 ack，失败 retry/dead-letter，并周期恢复过期 lease。
- `Busy` 只表示调度竞争，必须延后重试且不得触发业务失败死信阈值。
- 入站执行确认与回复发送是两个投递事务；回复目标失效不得回滚已成功的入站 delivery，批量 claim 的每条记录必须一起完成状态迁移。
- Chat 的交互队列默认只显示用户可见投递；`visibility=system` 仅供显式诊断查询，且协议 envelope 投影为上下文正文。

### Runtime 会话完整性边界

`MessageDeliveryDispatcher` 与 ADR-059 `ChatExecutionWorker` 是不同的可靠性入口：前者拥有 `MessageDelivery` 的 claim/ack/retry，后者拥有 Conversation Turn/Run 的 lease/fence。二者可以继续保留各自事实源，但只要解析到同一个 Runtime `sessionId`，就必须服从同一个会话单写者。

- `ISessionExecutionGate` 是进程内 Runtime 会话状态的唯一写入门。用户 Turn、Agent 消息、Heartbeat、直接 Runtime 调度进入 `AgentExecutionService` 时都必须先按 `sessionId` 串行化。
- `ChatExecutionWorker` 的每 Conversation 锁只是命令领取优化，不能被视为 Runtime 全局锁；Conversation Run 的数据库 lease/fence 仍是跨进程正确性权威。
- `ContextWindowManager` 的历史不是并发协调器。禁止两个执行同时修改同一个 `List<ChatMessage>`。
- Assistant `tool_calls` 与其全部 Tool results 是一个不可分割的协议轮次。只有每个 advertised `tool_call_id` 都有且只有一个结果时，才允许一次性提交到历史；取消、超时、Fuse、工具异常不得留下半轮历史。
- `LlmMessageSequenceNormalizer` 在历史水合与 LLM 调用边界修复遗留的不完整轮次，`OpenAiLlmGateway` 在协议序列化前执行最后守卫。修复必须记录 incomplete round/orphan tool 计数，不能静默把 Provider 400 当作普通网络失败重试。

---

## 1. Context

当前 Admin Chat 正在从单 Agent 对话界面演进到多 Agent 聊天室。用户期望的语义不是“用户请求、Agent 回复”的单向管线，而是类似微信的双向消息关系：

- 用户可以给 Agent 发消息。
- Agent 也可以主动给用户发消息。
- Agent 可以给另一个 Agent 发消息。
- Agent 可以向聊天室广播，或给 `@all` 群发。
- Connector、Cron、Heartbeat、P2P 节点都可能成为消息来源或消息目标。

因此，聊天室不应被视为“前端页面”。聊天室应被视为一个客户端，只是当前用 Web 技术实现。Web UI、CLI、移动端、P2P 节点、MQTT 连接器都应该作为 Message Client 参与同一个消息系统。

当前代码已经出现了过渡性 spike：

- `ChatRoomRouteResolver` 将 `@agent/@all` 解析放到服务端，这是正确方向的一部分。
- `ChatApiController` 直接处理 secondary fan-out、转录、SSM 写入和 token 统计，这是不应长期保留的补丁式实现。
- `SessionRouter` 开始透传 `audience/target_agent_ids/fanout_index`，说明群聊语义已经渗入执行 ingress。
- `useChatState` 根据 fan-out metadata 创建多 Agent turn，说明前端也开始承担调度结果物化。

这些实现可以作为需求验证，但不能作为目标架构。否则后续 `send_message`、Connector 出站、Cron 唤醒、Agent-to-Agent 都会各自长出一套通道。

---

## 2. Decision

引入双向 Message Fabric，作为所有消息收发的统一抽象。

```text
MessageEndpoint <-> IMessageSystem <-> MessageEndpoint
```

`MessageEndpoint` 可以是：

- `user`：人类用户、浏览器会话、CLI 用户。
- `agent`：本地 Agent、子 Agent、P2P Agent。
- `room`：聊天室或工作区协作空间。
- `connector`：MQTT、HTTP、Webhook、WebSocket、邮件等外部连接器。
- `system`：Cron、Heartbeat、治理任务、内部服务。

Admin Chat UI 只是一个 `user`/`room` 客户端。它可以提交消息、观察房间、接收 Agent 主动消息，但消息系统不得依赖浏览器打开。

Agent 必须被视为聊天室的一等公民，而不是被 UI 或 Controller 调用的后端能力。用户和 Agent 都是 `RoomParticipant`，都拥有参与者身份、发件能力、收件能力、可见性边界、投递状态和在线/可用状态。区别只在能力和权限，不在消息系统地位。

Message Fabric 与 Event System 必须作为同一条消息驱动事件管道建设，而不是两个松散并列的模块。消息系统负责领域语义：端点、参与者、收件箱、可见性、投递记录、`send_message`；事件系统负责推进机制：优先级队列、持久化投递、订阅、重试、死信、唤醒。换句话说，消息是业务事实，事件是推进机制。

两者不能合并成一个抽象：事件系统不应该理解 `@all`、room、agent inbox、可见性和 connector endpoint 等消息领域规则。两者也不能分开排期：没有可靠事件层的 Message Fabric 只是同步 API，无法支撑 Agent 主动消息、离线投递、唤醒、恢复和跨端追责。因此目标不是“先做消息系统，再补事件系统”，而是交付一条最小可用的 message-backed event pipeline。

Agent 可以通过消息系统主动查询自己的收件箱，也可以通过事件系统订阅 `message.*` 事件被动接收新消息；这两种消费方式必须共享同一批 `MessageDelivery` 记录和同一条 trace/correlation/causation 链。

---

## 3. Target Architecture

```text
Web Chat / CLI / Connector / Cron / Agent Tool
        |
        v
IMessageSystem.SendAsync(MessageEnvelope)
        |
        v
IMessageRouter
  - resolve @agent / @all
  - resolve room participants
  - apply visibility and delivery policy
  - produce delivery records
        |
        +--> RoomMessageLog       public/private transcript facts
        +--> MessageDelivery      per-target delivery state
        +--> MessageInbox         pull-based endpoint inbox
        +--> IInternalEventBus    message.deliver events
                    |
                    v
              IPriorityEventQueue
                    |
                    v
        MessageDeliveryDispatcher
                    |
                    v
              Runtime / Tools / LLM
                    |
                    v
              send_message -> IMessageSystem
```

这条管道的最小闭环如下：

```text
MessageEnvelope
  -> IMessageSystem.SendAsync
  -> IMessageRouter resolves target and visibility
  -> RoomMessageLog / MessageDelivery / MessageInbox
  -> message.deliver event
  -> IInternalEventBus / IPriorityEventQueue
  -> MessageDeliveryDispatcher / ConnectorEgress / UI Projection consumer
  -> delivery ack / retry / dead-letter / trace update
```

所有可唤醒执行的消息都必须先成为 `MessageDelivery`，再由 `message.deliver` 进入事件机制。禁止 Connector、Chat API、Cron 或 Agent tool 绕过 delivery 直接唤醒 Runtime；否则离线恢复、重试、审计和 UI projection 会失去共同事实来源。

职责边界：

| 组件 | 职责 | 不应承担 |
|------|------|----------|
| `IMessageSystem` | 统一发送入口、幂等、审计、投递记录创建 | LLM 执行、UI 渲染 |
| `IMessageRouter` | 解析地址、`@`、房间成员、可见性、优先级 | 写 SSE、调用 Runtime |
| `IMessageInbox` | 查询、领取、确认端点收件箱 | 决定消息目标、调用 LLM |
| `IInternalEventBus` | 纯事件管道、优先级队列、重试、死信 | 理解 Chat UI 或房间语义 |
| `IPriorityEventQueue` | 按事件优先级持久化推进、ack、retry、dead-letter | 持有消息领域状态、解析 room/endpoint |
| `MessageDeliveryDispatcher` | 发现/领取持久化 Agent 投递，检查可用性，构造 Runtime 执行并 ack/retry/dead-letter | 解析 `@all`、决定群发目标、依赖浏览器在线 |
| `AgentEventHandler` | 消费非消息类通用内部事件 | 消费 `message.deliver`、重复触发消息执行 |
| `SessionStateManager` | 会话事件日志、实时观察、回放 | 消息路由和投递策略 |
| `ChatApiController` | 接收 Web 客户端消息意图 | secondary fan-out、Agent 调度 |
| Admin Chat UI | 聊天室客户端、观察窗口、输入器 | 消息路由权威、执行依赖 |

---

## 3.1 Gateway / Connector 分层修订

用户提出的粗略链路是：

```text
聊天室 -> 连接器 -> 网关 -> 消息系统 -> 事件系统 -> 执行引擎
执行引擎 -> send_message -> 消息系统 -> 路由到聊天室或内部 Agent
```

这个方向抓住了两个关键点：聊天室不应绕过消息系统，Agent 回复也必须反向走 `send_message`。但更好的分层不是让“聊天室”直接成为连接器，而是让聊天室成为一个 Message Client，让 WebSocket/HTTP/SSE/CLI/MQTT/Email 等协议实现成为 Connector。原因是聊天室是产品语义，连接器是传输协议适配；二者耦合会让 Web Chat 的实现细节污染 CLI、MQTT、P2P 和移动端。

修订后的权威链路：

```text
Human / External System / Agent Tool
        |
        v
Message Client
  - Web Chat
  - CLI
  - MQTT device
  - HTTP webhook
  - Email
        |
        v
Connector
  - HTTP / WebSocket / MQTT / Webhook / Email protocol adapter
        |
        v
Gateway Boundary
  - auth
  - workspace / tenant binding
  - protocol normalization
  - rate limit / backpressure
  - ingress / egress correlation
        |
        v
IMessageSystem
  - endpoint semantics
  - room participant resolution
  - route decision
  - transcript and delivery records
        |
        v
IInternalEventBus / IPriorityEventQueue
  - priority
  - subscription
  - retry
  - dead-letter
  - wakeup
        |
        v
Execution Engine / Agent Runtime
        |
        v
send_message -> IMessageSystem
```

反向链路：

```text
Agent Runtime
  -> send_message
  -> IMessageSystem
  -> IMessageRouter
       - target=agent      => message.deliver wakeup for MessageDeliveryDispatcher
       - target=user       => user/client delivery + notification projection
       - target=room       => room transcript + participant deliveries
       - target=connector  => connector egress delivery
       - target=system     => internal service delivery
  -> Gateway Egress Dispatcher
  -> Connector.SendAsync / OperateAsync
  -> external client or device
```

关键边界：

| 层 | 应该做 | 不应该做 |
|----|--------|----------|
| Chatroom Client | 输入、观察、展示、断线恢复、mention 提示 | 决定最终路由、直接唤醒 Agent |
| Connector | 协议收发、连接生命周期、协议字段映射 | 解析 `@all`、选择 Agent、写执行日志 |
| Gateway Boundary | 认证、租户绑定、限流、归一化、相关性 ID | 执行业务路由、理解 Agent 能力 |
| Message System | 端点、房间、收件箱、可见性、delivery、路由 | 协议细节、Web UI 状态、LLM 执行 |
| Event System | 优先级队列、订阅、重试、死信、唤醒 | 聊天室语义、`@` 解析、房间成员管理 |
| Execution Engine | 消费投递、运行 Agent、调用工具 | 直接写外部协议、绕过 `send_message` 回复 |

因此，当前 Admin Chat 的 HTTP API 应被视为 WebChat Connector/Gateway 的一个临时入口，而不是长期的消息调度器。长期结构应把 Web Chat、WebSocket Connector、MQTT Connector、HTTP Webhook Connector 都统一到 `GatewayIngress -> IMessageSystem`，并把所有出站消息统一到 `IMessageSystem -> GatewayEgress -> Connector.SendAsync`。

### 3.2 Ingress 归一化模型

Connector 入站不应直接投递 `InternalEvent` 给执行引擎。它应先变成消息系统可理解的 `MessageEnvelope`：

```text
PuddingIngressEnvelope
  -> GatewayIngressNormalizer
  -> MessageEnvelope
  -> IMessageSystem.SendAsync
```

归一化规则：

- `channelType=webchat/websocket/http/mqtt/email` 映射为 `from.kind=user|connector|system`。
- `channelId` 映射为 connector endpoint 或 client connection。
- `userExternalId` 映射为 user endpoint，并绑定 workspace。
- `messageText` 映射为 `MessageEnvelope.Content`。
- `messageType` 映射为 `ContentType` 或 metadata。
- `traceId/correlationId/causationId` 必须贯穿到 `MessageEnvelope` 和后续 `InternalEvent`。
- `@agent/@all` 只能作为 message content 或 route hint 传入，最终解析由 `IMessageRouter` 完成。

这样 Web Chat 和 MQTT 的区别只存在于 Gateway/Connector 层；进入 Message Fabric 后，二者都是端点之间的消息。

### 3.3 Egress 分发模型

`IMessageSystem` 创建的每条 `MessageDelivery` 都有明确 `TargetKind`：

- `agent`：发布 `message.deliver`，由事件系统唤醒 Agent。
- `user`：写入用户收件箱，并通知在线客户端；离线时保留 delivery。
- `room`：写入房间 transcript，再展开到可接收参与者。
- `connector`：交给 Gateway Egress Dispatcher，调用目标连接器的 `SendAsync` 或 `OperateAsync`。
- `system`：交给内部系统 handler。

Gateway Egress Dispatcher 不决定业务目标，只根据 delivery 的目标类型和 connector binding 找到出站协议。比如：

```text
MessageDelivery(target=connector:mqtt.living-room, content="turn_on:ac")
  -> GatewayEgressDispatcher
  -> MqttConnector.SendAsync(topic="living-room/ac", payload="turn_on:ac")
```

```text
MessageDelivery(target=user:owner, roomId=default)
  -> GatewayEgressDispatcher
  -> WebSocketConnector.SendAsync(connectionId=...)
  -> Admin Chat timeline receives message
```

如果用户客户端离线，delivery 仍然存在；下次 Web Chat 打开时从 `MessageInbox` / room transcript 恢复，而不是要求执行引擎重新发一次。

---

## 4. Domain Model

### 4.1 RoomParticipant

```csharp
public sealed record RoomParticipant
{
    public required string ParticipantId { get; init; }
    public required string RoomId { get; init; }
    public required string Kind { get; init; } // user | agent | connector | system
    public required string EndpointId { get; init; }
    public string? DisplayName { get; init; }
    public string? AvatarUrl { get; init; }
    public bool CanSend { get; init; } = true;
    public bool CanReceive { get; init; } = true;
    public string Status { get; init; } = "available"; // available | busy | sleeping | disabled
}
```

`RoomParticipant` 是聊天室成员的权威模型。用户和 Agent 都通过该模型进入房间；UI 展示成员列表、`@` 自动补全、`@all` 目标集合、Agent 可用状态都应从该模型派生。

### 4.2 MessageAddress

```csharp
public sealed record MessageAddress
{
    public required string Kind { get; init; } // user | agent | room | connector | system
    public required string Id { get; init; }
    public string? WorkspaceId { get; init; }
    public string? DisplayName { get; init; }
}
```

### 4.3 MessageEnvelope

```csharp
public sealed record MessageEnvelope
{
    public required string MessageId { get; init; }
    public required MessageAddress From { get; init; }
    public required IReadOnlyList<MessageAddress> To { get; init; }
    public string? RoomId { get; init; }
    public string? ConversationId { get; init; }
    public string? ReplyToMessageId { get; init; }
    public string? CorrelationId { get; init; }
    public string? CausationId { get; init; }
    public required string Audience { get; init; }   // direct | room | broadcast
    public required string Visibility { get; init; } // public | private | system
    public required string ContentType { get; init; } // text | command | event | artifact
    public required string Content { get; init; }
    public int Priority { get; init; }
    public long CreatedAt { get; init; }
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
}
```

### 4.4 RoomMessage

`RoomMessage` 是房间转录事实。它记录谁在房间里说了什么、对谁可见。它不等价于 Runtime 执行日志。

关键字段：

- `RoomMessageId`
- `RoomId`
- `MessageId`
- `FromKind`
- `FromId`
- `Audience`
- `Visibility`
- `Content`
- `CreatedAt`

### 4.5 MessageDelivery

`MessageDelivery` 是每个目标端点的一条投递事实。

关键字段：

- `DeliveryId`
- `MessageId`
- `TargetKind`
- `TargetId`
- `Status`：`queued | delivering | delivered | failed | cancelled | expired`
- `Priority`
- `AttemptCount`
- `LastError`
- `CreatedAt`
- `UpdatedAt`

`@all` 不应复制 N 条用户消息；它应产生一条 `RoomMessage` 和 N 条 `MessageDelivery`。

### 4.6 MessageInbox

`MessageInbox` 不是独立事实源，而是 `MessageDelivery` 面向某个 endpoint 的查询投影。Agent 主动询问是否有新消息时读取它；事件订阅触发时也应引用同一条 delivery。

关键字段：

- `EndpointKind`
- `EndpointId`
- `WorkspaceId`
- `RoomId`
- `DeliveryId`
- `MessageId`
- `FromKind`
- `FromId`
- `Content`
- `Status`
- `Priority`
- `CreatedAt`
- `ReadAt`
- `AckAt`

---

## 5. Message Flow

### 5.1 用户在聊天室发给单个 Agent

```text
Web Chat Client
  -> IMessageSystem.SendAsync(from=user, to=agent)
  -> MessageRouter resolves one delivery
  -> RoomMessageLog appends public/direct transcript
  -> MessageDelivery queued for target agent
  -> IInternalEventBus publishes message.deliver
  -> MessageDeliveryDispatcher atomically claims delivery and invokes Runtime
  -> Agent may respond via send_message(from=agent, to=user or room)
```

### 5.2 用户 `@all`

```text
Web Chat Client
  -> IMessageSystem.SendAsync(from=user, to=room, audience=broadcast)
  -> MessageRouter expands room participants
  -> one RoomMessage
  -> N MessageDelivery rows
  -> N message.deliver events, each targeting one agent
```

### 5.3 Agent 主动给用户发消息

```text
Runtime tool call: send_message(to=user, content=...)
  -> IMessageSystem.SendAsync(from=agent, to=user)
  -> MessageDelivery target=user
  -> SessionStateManager/notification stream updates observing clients
```

这条链路不要求浏览器处于打开状态。浏览器只是后续观察和接收通知的客户端。

### 5.4 Agent 给 Connector 发消息

```text
Runtime tool call: send_message(to=connector:mqtt.living-room, content=...)
  -> IMessageSystem
  -> MessageDelivery target=connector
  -> connector outbound handler publishes MQTT/HTTP/Webhook message
```

### 5.5 Agent 接收消息

Agent 接收消息有两种等价入口，二者都基于同一条 `MessageDelivery`：

```text
主动拉取:
Runtime tool call: receive_messages(endpoint=agent:self)
  -> IMessageInbox.ListAsync(agent endpoint)
  -> return queued/unread deliveries
  -> Agent processes messages
  -> IMessageInbox.AckAsync(deliveryId)
```

```text
事件订阅:
Agent subscribes message.*
  -> IInternalEventBus publishes message.deliver
  -> MessageDeliveryDispatcher receives wakeup or discovers durable pending target
  -> Runtime is invoked or resumed
  -> same delivery is marked delivered/failed
```

主动拉取适合 Agent 周期性思考、恢复后补消息、低频任务；事件订阅适合实时唤醒、高优先级消息和连接器触发。

---

## 6. Runtime Message Tools

`send_message` 是 Agent 对 Message Fabric 的标准写入口，不是 Chat UI 的快捷函数。

建议参数：

```json
{
  "to": ["agent:planner", "user:owner", "room:default"],
  "content": "需要你确认空调是否打开。",
  "audience": "direct",
  "visibility": "private",
  "priority": 5,
  "roomId": "default",
  "replyToMessageId": "optional",
  "metadata": {}
}
```

语义：

- `to` 支持 user、agent、room、connector、system。
- `audience=room` 表示面向房间。
- `audience=broadcast` 表示扩展成多个 delivery。
- `visibility=private` 表示不进入公开房间时间线，只记录投递与可授权查看的私有 transcript。
- `priority` 映射到事件系统优先级。

`receive_messages` 是 Agent 主动查询收件箱的标准读入口。

建议参数：

```json
{
  "endpoint": "agent:self",
  "roomId": "default",
  "limit": 20,
  "includeDelivered": false
}
```

语义：

- 默认读取当前 Agent endpoint 的 queued/unread delivery。
- 返回消息内容、来源、room、priority、deliveryId、messageId。
- Agent 处理完成后通过 ack 标记 delivery，避免重复处理。
- 实时场景仍优先使用事件系统订阅 `message.*`。

---

## 7. Frontend Design Implications

Admin Chat 应从“单 Agent 对话页”转为“聊天室客户端”：

- 左侧是 room/conversation 列表，不是单 Agent session 列表。
- 顶部展示房间名称、在线/启用 Agent、队列状态。
- 中间是 Room Timeline，只展示当前用户有权限看到的消息。
- 右侧是参与者、delivery 状态、Agent 活跃状态和诊断。
- 输入框支持 mention autocomplete，但服务端路由是权威。
- 前端可以做乐观显示，但不得决定最终目标集合。
- Timeline 必须支持虚拟列表、批量事件应用和断线重放，避免多 Agent 流式输出造成卡顿。

聊天室客户端是 Message Fabric 的观察者和参与者，不是系统运行的必要条件。

---

## 8. Migration Plan

### Phase 0：冻结补丁式 fan-out

- 不继续扩展 `ChatApiController` 中的 secondary fan-out。
- 不继续扩展前端基于 `fanout_index` 创建 turn 的逻辑。
- 保留 `ChatRoomRouteResolver` 的服务端解析思想，但后续移动到 `IMessageRouter`。

### Phase 1：Core 合同与管道不变量

在 `PuddingCore` 增加：

- `MessageAddress`
- `MessageEnvelope`
- `MessageDelivery`
- `RoomMessage`
- `MessageInboxItem`
- `IMessageSystem`
- `IMessageRouter`
- `IMessageInbox`

同时明确并测试三条不变量：

- `traceId`、`correlationId`、`causationId` 从 ingress envelope 贯穿到 message、delivery、event、execution trace。
- `IMessageSystem.SendAsync` 是所有入站和出站消息的唯一写入口。
- 可执行目标必须先产生 `MessageDelivery`，再产生 `message.deliver` 事件。

### Phase 2：Platform 持久化和路由

- 新增 room message 和 delivery 存储。
- 实现 `MessageRouter`。
- Chat API 改为提交 `MessageEnvelope`，不再直接 fan-out。

### Phase 3：事件桥接和可靠推进

- `IMessageSystem` 发布 `message.deliver`。
- `MessageDeliveryDispatcher` 以 Hosted Service 运行，订阅 `message.deliver` 并从 payload 唤醒目标。
- Dispatcher 每轮从持久化 Inbox 发现 `queued/retrying` 目标，确保丢失事件或进程重启后仍可恢复。
- `AgentEventHandler` 明确跳过 `message.deliver`，避免双消费者重复执行。
- `MessageDelivery` 随执行状态更新。
- `IPriorityEventQueue` 对 `message.deliver` 提供 ack、retry、dead-letter 和重放入口。
- delivery 状态与事件状态要可互相定位：从消息能查到事件，从事件能查到消息和最终执行结果。

### Phase 4：Runtime 工具

- 新增 `SendMessageTool`，实现 `IAgentSkill`。
- 新增 `ReceiveMessagesTool`，实现 `IAgentSkill`。
- 工具调用 `IMessageSystem.SendAsync`。
- Agent 主动拉取消息时调用 `IMessageInbox`。
- Agent 实时收消息时通过事件系统订阅 `message.*`。
- Agent-to-Agent、Agent-to-User、Agent-to-Connector 统一走同一通道。

### Phase 5：Chat UI 重构

- 将 Admin Chat 改成 room client。
- 从 RoomMessage/SessionEventLog 恢复 timeline。
- 展示 delivery 状态和多 Agent 输出。
- 删除前端路由权威逻辑和 fan-out turn 特例。

---

## 9. Acceptance Criteria

1. 用户和 Agent 都以 `RoomParticipant` 身份加入聊天室，并通过同一套 participant/endpoint 模型发消息和收消息。
2. 用户 `@all` 只写入一条 room message，但产生多条 delivery。
3. Agent 调用 `send_message` 给用户时，浏览器关闭也能完成投递并可在之后观察到。
4. Agent 调用 `send_message` 给另一个 Agent 时，不依赖 Chat API。
5. Connector 入站消息和 Web Chat 消息进入同一 `IMessageSystem`。
6. Connector 出站消息也通过 delivery 记录驱动。
7. Agent 可以通过 `receive_messages` 主动查询自己的收件箱。
8. Agent 可以通过事件订阅接收 `message.*` 并被唤醒。
9. 事件系统只看到 `message.deliver` 等事件，不知道 Web Chat 的存在。
10. 前端刷新后可以从 room transcript 和 session event log 恢复状态。
11. 私有消息不会进入公开 room timeline。
12. 优先级消息能映射到 `IPriorityEventQueue`。
13. 删除或禁用补丁式 fan-out 后，群聊能力仍由消息系统提供。
14. 每个可执行 `MessageDelivery` 都能追踪到对应 `message.deliver` 事件、队列状态、执行 trace 和最终 ack/retry/dead-letter 结果。
15. Web Chat 发送给 Agent，Agent 再通过 `send_message` 主动回复用户，必须走同一条 `IMessageSystem -> MessageDelivery -> message.deliver/event/projection` 管道。
16. 未收到 `message.deliver` 或 Runtime 重启后，`queued/retrying` 投递仍能被持久化扫描发现并进入同一原子 claim 路径。
17. Chat 交互队列默认不显示 `visibility=system` 的内部投递；显式诊断查询返回正文投影而不是原始 envelope JSON。

---

## 10. Rejected Options

### A. 在 ChatApiController 中继续扩展 fan-out

不采纳。它会让 HTTP Controller 同时承担消息路由、Agent 调度、转录、SSM、token 统计和错误处理，违背低耦合高内聚。

### B. 让前端解析并决定所有目标 Agent

不采纳。前端是客户端，不是消息路由权威。CLI、Connector、Cron、Agent tool 都不应依赖浏览器逻辑。

### C. 复用 SwarmMessage 作为聊天室消息

不采纳。`SwarmMessage` 更接近 P2P/节点传输模型，缺少 room transcript、visibility、delivery、user endpoint、connector endpoint 等客户端消息语义。它可以成为 P2P 传输适配层，但不应成为上层消息领域模型。

### D. 把消息系统直接做成 SessionEventLog

不采纳。SessionEventLog 是执行过程事实日志；Message Fabric 是客户端之间的双向通信系统。两者有关联，但不应合并。

---

## 11. Open Questions

1. `RoomId` 与现有 `WorkspaceId/SessionId` 的关系需要进一步定型：建议 room 属于 workspace，session 属于一次或一组执行。
2. 私有消息是否允许被房间管理员审计，需要安全策略定义。
3. P2P 跨节点 delivery 的存储权威需要单独 ADR：本地节点权威、目标节点权威，或双写同步。
4. `@all` 是否包含用户客户端、只包含 Agent，还是可配置，需要产品策略决定。
5. Heartbeat 的“休眠/不接收 Cron”应作为 endpoint availability 还是 delivery policy，需要和 Cron ADR 联动。
