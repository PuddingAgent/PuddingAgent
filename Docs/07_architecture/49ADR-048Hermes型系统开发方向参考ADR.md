# ADR-048：Hermes 型系统开发方向参考

> 状态：**Draft**  
> 日期：2026-06-04  
> 范围：Message Fabric、Event System、Agent 身份、记忆、工具/技能、Projection、Connector 出站、开发路线  
> 关联：[10事件系统与事件总线](10事件系统与事件总线.md)、[46ADR-045双向消息系统与聊天室客户端ADR](46ADR-045双向消息系统与聊天室客户端ADR.md)、[47ADR-046事件驱动多AgentOS交互体验架构ADR](47ADR-046事件驱动多AgentOS交互体验架构ADR.md)、[48ADR-047记忆图书馆知识图谱演进ADR](48ADR-047记忆图书馆知识图谱演进ADR.md)

---

## 1. Context

Pudding 正在从单 Agent Chat 应用演进为 Hermes 型本地 Agent OS。Hermes 型系统的核心不是“更多入口”或“更多 Agent”，而是让消息、事件、记忆、工具、连接器和 UI projection 形成可追踪、可恢复、可扩展的系统闭环。

本文记录当前 1~7 开发方向的参考基线。它不是最终实施计划；后续每个方向仍需要继续拆成独立 ADR、设计文档和实现计划。

---

## 2. Guiding Principle

最重要的收敛原则：

```text
Message Fabric 和 Event System 是同一条管道的两层。

Message Fabric = 领域语义层
Event System   = 可靠推进层
```

消息是业务事实：谁发给谁、属于哪个房间、谁可见、谁需要投递、投递状态如何。

事件是推进机制：如何排队、唤醒、重试、死信、回放、诊断、关联执行结果。

因此，方向 1 和方向 2 必须作为一个开发主干推进，不能独立排期。

---

## 3. Seven Development Directions

### 3.1 Message-backed Event Pipeline

目标：建立系统中枢，让所有可执行消息先成为 `MessageDelivery`，再通过 `message.deliver` 进入事件队列。

关键能力：

- `MessageEnvelope` 是统一入站和出站消息事实。
- `IMessageSystem.SendAsync` 是唯一消息写入口。
- `IMessageRouter` 负责 endpoint、room、`@agent`、`@all`、visibility、priority。
- `MessageDelivery` 是投递、离线恢复、重试、诊断的共同事实来源。
- `IInternalEventBus` / `IPriorityEventQueue` 只处理推进机制，不理解 room 语义。

第一条验收切片：

```text
Web Chat -> IMessageSystem -> MessageDelivery -> message.deliver
  -> Event Queue -> AgentEventHandler -> Runtime
  -> Agent send_message -> IMessageSystem -> user delivery -> UI projection
```

待细化：

- `MessageDelivery` 与 `SessionEventLog`、runtime timeline 的关联模型。
- delivery ack、event ack、execution completion 三者状态如何映射。
- `traceId/correlationId/causationId` 的创建和继承规则。

### 3.2 Agent Endpoint And Inbox

目标：Agent 成为聊天室和消息系统的一等参与者，而不是被 Controller 调用的后端函数。

关键能力：

- Agent 拥有 `RoomParticipant`、`MessageEndpoint`、`MessageInbox`。
- Agent 有 presence：available、busy、sleeping、disabled、error。
- Agent 可主动收消息、主动发消息、订阅事件、被优先级消息唤醒。
- Agent-to-Agent、Agent-to-User、Agent-to-Connector 统一走 `send_message`。

待细化：

- Agent inbox 是 pull 优先、push 优先，还是两者并存。
- 子 Agent、P2P Agent、本地 Agent 是否共享 endpoint 模型。
- sleeping/busy Agent 的 delivery policy 和重试策略。

### 3.3 Auditable Memory And Cognition Layer

目标：记忆不只是存储和召回，而是可审计、可纠错、可解释的认知层。

关键能力：

- 每条长期记忆保留来源消息、会话、提取任务和 confidence。
- 记忆召回要能解释命中原因、相似度、注入位置和失败原因。
- 冲突记忆需要 merge/supersede/reject 状态，而不是简单覆盖。
- 记忆抽取、候选、确认、落库全部进入事件和诊断链路。
- 记忆图谱服务承接实体、关系、证据和版本演进。

待细化：

- Fact memory、page/chapter memory、graph memory 的边界。
- 用户手动编辑记忆后如何影响 confidence 和来源链。
- 记忆 benchmark 如何覆盖召回准确性、污染率和延迟。

### 3.4 Tool, Skill, And Permission Marketplace

目标：工具和技能形成可发现、可授权、可审计、可组合的能力层。

关键能力：

- 工具声明包含 schema、readOnly、concurrency、risk、permission hints。
- Agent 模板绑定工具/技能时能得到稳定能力视图。
- 工具审批、allowlist、denylist、AI reviewer 和人工确认统一进入审计链。
- 每次 tool call 记录输入、审批、输出、错误、耗时、trace。
- Skill、MCP、内置工具、连接器操作在能力发现层有一致表达。

待细化：

- 工具市场是工作区级、Agent 级，还是系统级。
- capability discovery 如何进入 prompt/context pipeline。
- 高风险工具的审批 ticket 如何跨会话继续。

### 3.5 Projection-first UI And Diagnostics

目标：UI 只消费面向交互的 projection，不直接绑定内部事件细节。

关键能力：

- `RoomTimelineProjection` 表达用户、Agent、connector、system note。
- `ParticipantPresenceProjection` 表达参与者状态。
- `MessageDeliveryProjection` 表达 sent、queued、delivered、read、failed、dead-letter。
- `ExecutionTraceProjection` 表达 LLM、tool、memory、subagent、connector 的执行链。
- Inspector 可钻取原始事件，但普通 timeline 不逐事件渲染。

待细化：

- Projection 存储是实时聚合、持久化读模型，还是混合。
- 高频 delta、thinking、tool status 如何归并以避免 UI 抖动。
- Room timeline 与 Session transcript 的恢复边界。

### 3.6 Connector Ingress And Egress Symmetry

目标：连接器不只是接收外部消息，也负责把系统消息可靠送回外部世界。

关键能力：

- Ingress：Connector -> GatewayNormalizer -> MessageEnvelope -> IMessageSystem。
- Egress：MessageDelivery(target=connector) -> GatewayEgress -> Connector.SendAsync。
- 所有 connector 消息都有 workspace binding、auth、rate limit、correlation。
- Web Chat、CLI、MQTT、Webhook、Email 使用同一 endpoint/delivery 模型。
- Connector 离线时 delivery 保留，恢复后可重试或人工处理。

待细化：

- Connector 出站失败如何进入 dead-letter 和人工修复队列。
- 外部平台 message id 与 Pudding delivery id 的幂等关系。
- P2P 节点是 connector、endpoint，还是独立 transport 层。

### 3.7 Vertical Slice And Governance Loop

目标：用可验证的端到端切片推进，而不是横向铺开所有抽象。

推荐第一阶段切片：

```text
Web Chat text message
  -> Gateway ingress normalization
  -> IMessageSystem
  -> one user-to-agent delivery
  -> message.deliver event
  -> priority event queue
  -> Agent runtime execution
  -> send_message reply
  -> user delivery
  -> room timeline projection
  -> diagnostics trace view
```

治理要求：

- 每个方向都要有最小可验收切片。
- 每个切片必须有回放、诊断和失败证据。
- 不允许新增旁路通道绕过 `IMessageSystem` 或 `MessageDelivery`。
- 行为变更优先补测试；难以自动化的链路至少要有诊断脚本或手动验收步骤。

待细化：

- 第一阶段是否包含 connector egress，还是只覆盖 Web Chat。
- 诊断页面先做消息链路，还是复用 Runtime Timeline。
- 如何从现有 Chat API fan-out 迁移，避免大爆炸重写。

---

## 4. Priority

建议优先级：

1. 先做 `Message-backed Event Pipeline` 最小闭环。
2. 再做 `Agent Endpoint And Inbox`，让 Agent 真正成为参与者。
3. 同步补 `Projection-first UI And Diagnostics`，否则链路不可观察。
4. 之后推进 `Connector Ingress And Egress Symmetry`。
5. 记忆、工具/技能/权限继续沿现有 ADR 和实现迭代，但要接入同一 trace 和 diagnostics 基线。

这个顺序的原因是：消息和事件中枢不稳定时，Agent、连接器、记忆和工具都会各自长出旁路；一旦旁路形成，后续很难收敛。

---

## 5. Open Questions

1. `MessageDelivery`、`SessionEventLog`、`RuntimeTimeline` 三者是否需要统一 trace 查询 API。
2. 第一阶段 `IPriorityEventQueue` 是否必须持久化，还是允许内存实现加接口约束。
3. Agent inbox 的消费确认由 Runtime 自动 ack，还是由 Agent 工具显式 ack。
4. Connector egress 的失败是否默认重试，还是按 connector policy 决定。
5. 记忆抽取和工具审批是否都应建模为系统消息，还是只进入事件/诊断链路。
6. UI projection 是否需要独立数据库表，还是从 message/delivery/session log 查询聚合。
7. P2P Agent 的 endpoint 权威在本地节点、远端节点，还是双方双写。
