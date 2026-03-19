# PuddingController 与 Gateway

## 定位

PuddingController 是整个协作网络的控制面宿主。PuddingGateway 是它内部的边界接入模块，而不是长期独立存在的平级产品。

这套设计不是为了做一个“会聊天的网关”，而是为了解决几个真实工程问题：

- 工作流与多智能体任务不能只靠 LLM 临场发挥。
- 多个工作交叉时，必须保证 Workspace、事件域和记忆域隔离。
- 世界的变化应以事件进入系统，而不是让 Agent 靠心跳轮询等待。
- 企业系统接入应优先依赖协议适配层，而不是浏览器自动化。

比如：
路由
授权
审批
审计
调度
Adapter 治理
Runtime 节点管理
Session 控制链路

LLM API 路由，LLM  API  KEY存储在数据库，由PuddingController负责管理，当Runtime思考的时候，API 路由到PuddingController，PuddingController根据策略路由到对应的服务商API。也就说Runtime不接触LLM API Key。

更抽象地说，Controller 负责回答的问题是：

- 这条输入该进入哪个 Workspace？
- 这个主体有没有权限订阅、触发、执行？
- 这个任务该走哪个 Workflow / AgentTemplate / Runtime？
- 这个动作是否需要审批、审计、冻结或限流？
- 这个外部协议事件能否进入内部事件世界？

PuddingController 也负责管理和度量不同的agent、agent模板的tokens使用情况，负责限额管理。

PuddingController需要被设计为无状态的，以便后期的横向扩展。

                ┌──────────────┐
                │  Controller   │
                └──────┬───────┘
                       │
        ┌──────────────┼──────────────┐
        │                             │
 ┌──────────────┐              ┌──────────────┐
 │   PostgreSQL │              │    Redis     │
 │              │              │              │
 │ Task state   │              │ queue        │
 │ Agent meta   │              │ locks        │
 │ Event log    │              │ cache        │
 │ Memory index │              │ stream       │
 └──────────────┘              └──────────────┘

Runtime、PuddingController、Gateway Adapter 以及 Agent 都需要支持事件订阅。内部事件总线建议基于 RabbitMQ，它负责承载控制面和执行面的统一事件流。

事件订阅和事件发布分为全局域与 Workspace 域。默认情况下，业务事件优先在 Workspace 域内传播；只有平台级治理、基础设施状态或跨域协同时，才进入全局域。

同样重要的是：事件发布权也必须分域治理。默认情况下，Runtime、普通 Agent、Workflow Worker 只应发布 `workspace` 域事件；`global` 事件必须由 Controller、用户显式动作或具备明确特权的治理模块发出。

这里的核心架构原则是：万物皆事件。外部输入是事件，内部状态变化也是事件。任务完成、某个 Agent 完成任务、心跳、审批结果、工具返回、记忆候选写回、设备告警，都应被统一抽象为可路由、可订阅、可审计的事件。

换句话说，Pudding 不是要让 Agent 持续盯着世界看有没有变化，而是要让世界在发生变化时主动敲门。



Gateway 的核心设计不应是为每一种外部系统硬编码接入逻辑，而应采用 Adapter Plugin 模式，把协议转换、事件订阅、身份标准化和出站回写封装为可热插拔的适配器插件。

## Controller 负责的能力

- Runtime 节点注册、发现、健康检查与容量感知。
- Runtime 节点标签、资源画像、用途画像、能力画像与负载画像管理。
- 渠道路由、Session 建立、用户与租户边界隔离。
- 认证、鉴权、审批、审计、治理与冻结控制。
- Workflow 调度入口、Swarm 协调与控制面事件输出。
- 统一事件路由、事件域隔离、事件权限判定与订阅治理。
- 插件注册、安装、启停、版本与装配策略。
- 全局 Skill Registry 与 MCP Registry 管理。
- AgentTemplate 存储、查询、版本化与运行画像解释。
- Runtime 放置决策：根据显式指定、标签偏好、隔离级别、Workspace 亲和性与节点负载选择 Runtime。
- 对 CLI、Web、Avalonia 以及其他客户端暴露控制接口。
- 与LLM API服务商的链接，Runtime与Controller沟通

## Controller 必须完成的功能

从系统最小闭环来看，`PuddingController` 至少必须完成以下能力：

1. **统一入口与治理判定**
       - 接收 Gateway 或客户端上送的标准化输入
       - 命中 Workspace、Channel、AgentTemplate、Session 路由
       - 执行身份、权限、审批与策略判定

2. **事件治理与路由**
       - 管理 `global` / `workspace` 两级事件域
       - 判定谁可以发布、订阅、重放、升级事件
       - 把输入转换为可治理事件流，而不是让各模块私下互调
       - 对冻结、恢复、全局 stop 这类治理动作，优先转换为全局治理事件并广播给 Runtime

建议默认原则：

- 大多数事件默认进入 `workspace` 域
- `global` 域仅用于治理、基础设施、跨域协调和显式授权的全局广播
- 只有 Controller、用户动作或具备明确特权的 Agent / 系统模块，才允许发布 `global` 事件

3. **Workflow 与协同控制**
       - 维护流程控制权，而不是把流程完全交给 LLM 自由发挥
       - 负责委派、收敛、审计、冻结、重试等控制链能力

4. **Runtime 节点管理**
       - 注册、发现、健康检查、容量感知、冻结与隔离
       - 决定把任务或事件交给哪个 Runtime 执行

5. **平台级外部资源治理**
       - 统一托管 LLM API Key、预算、配额与供应商路由策略
       - 统一托管 Adapter 插件、来源校验与装配策略

6. **全局能力注册与分发治理**
       - 统一托管 Skill 与 MCP 的注册信息、版本、可见性、风险等级与适用环境
       - 让 AgentTemplate 只引用能力，而不是复制能力定义

7. **Runtime 选址与亲和性调度**
       - 支持显式指定 Runtime
       - 支持根据必需标签、偏好标签和排斥标签选择 Runtime
       - 在缺省情况下优先保持同一 Workspace 的 Agent 落在同一 Runtime

## Controller 的最小阶段目标

第一阶段至少应做到：

- 能把外部输入命中到正确的 Workspace / Session / AgentTemplate
- 能把治理后的请求投递到 Runtime 并收回结果
- 能记录首批审计事件与拒绝原因
- 能管理最小事件总线与订阅判定
- 能管理 Runtime 节点与最小 LLM 路由
- 能维护最小 Skill / MCP 全局注册表
- 能基于标签与负载做最小 Runtime 选择

## 全局 Skill / MCP Registry

`PuddingController` 应维护一套全局 `Skill Registry` 与 `Mcp Registry`，作为平台能力定义的权威入口。

建议由 Controller 负责：

- 注册 Skill / MCP 元数据
- 维护版本、来源、信任等级与风险等级
- 维护适用的 Runtime 标签、宿主要求与资源要求
- 控制哪些 Workspace / Template 可以引用这些能力
- 审计能力的启用、冻结、升级与弃用

第一阶段建议：

- 元数据、可见性、版本和审计信息存储在 PostgreSQL
- Runtime 不直接拥有全局定义权，只消费 Controller 批准后的引用与配置

这也回答了“SKILL 在哪里”这个问题：

- **定义和注册，在 Controller**
- **配置和管理，在 Platform**
- **装配和执行，在 Runtime**

## Runtime 节点画像与心跳

Controller 不应该只知道“有几个 Runtime 活着”，还应该知道它们分别适合做什么。

因此每个 Runtime 节点至少应持续上报：

- 操作系统与架构
- CPU / 内存 / 磁盘 / GPU 基础能力
- 用途标签，例如 `coding`、`drawing`、`test-runner`
- 环境标签，例如 `windows`、`linux`、`high-memory`
- 支持的沙箱提供者，例如 `docker`、`wasm`、`gvisor`
- 当前负载、压力、活跃 Agent 数和活跃 Workspace 数

这些信息不只是用来做监控，更是 Runtime 选址与降级路由的输入。

## Runtime 选址与放置策略

当用户通过 `PuddingPlatform` 创建 Agent，或者编排 Agent 派生新的 Agent 时，Controller 应支持以下几类放置方式：

1. **显式指定 Runtime**
       - 用户或上层流程直接指定 `runtimeId`

2. **按标签与偏好选择**
       - 模板或请求声明 `requiredRuntimeTags`
       - 声明 `preferredRuntimeTags`
       - 声明 `excludedRuntimeTags`

3. **缺省规则选择**
       - 如果没有显式指定，则由 Controller 按内置规则选址

建议默认原则：

- **同一 Workspace 的 Agent 优先放在同一个 Runtime**
- 只有在模板偏好、隔离要求、OS 要求或资源要求冲突时，才拆到不同 Runtime
- Dedicated 隔离会影响 sandbox 选择，但不一定要求更换 Runtime 节点；除非节点能力不满足

建议最小决策输入：

- Workspace 亲和性
- 当前 Runtime 负载
- 模板运行画像
- 沙箱提供者可用性
- 风险等级与审批状态

这部分本质上是 Pudding 的“放置与编排权”，应归 Controller，而不是 Runtime 自己私下找地方落脚。



## Gateway 负责的能力

- 作为 Adapter Plugin 宿主，承载外部系统接入与生命周期管理。
- HTTP、WebSocket、MQTT、Webhook、本地总线等接入协议承载。
- 统一入口与统一出站回复。
- 渠道消息、宿主事件、设备事件的标准化、身份解析和边界协议转换。
- 把不同外部系统产生的原始事件转换为统一的 `PuddingIngressEnvelope` 或 `PuddingMessage`。
- 把外部协议世界翻译成平台事件世界，使 Runtime 和 Agent 永远消费统一事件，而不是外部协议细节。

## Gateway 不负责的事

- 不决定最终进入哪个 Workspace 的治理结果。
- 不决定某个 Agent 是否有权订阅或执行。
- 不承载 Workflow 编排与多智能体协同决策。
- 不持有长期执行态与会话权威。

Gateway 的职责是把边界处理干净，而不是替 Controller 或 Runtime 抢戏。

## Gateway 设计原则

- Gateway 只做边界接入、协议适配、标准化和回写，不承载业务编排与 Agent 决策。
- 每个外部系统都应被视为独立 Adapter，而不是在 Gateway 中追加特判分支。
- Adapter 优先采用事件驱动、订阅驱动或回调驱动，避免无效轮询。
- Gateway 产生标准化事件后，后续的路由、鉴权、审批与治理统一交给 Controller。
- Adapter 插件的启停、装配、权限和可见性必须受 Workspace 与控制面策略约束。

## Adapter Plugin 模式

在 PuddingGateway 中，钉钉、飞书、Email、MQTT、Webhook、嵌入其他 C# 桌面软件的运行节点，都应被视为独立的 Gateway Adapter Plugin。

这样做的核心收益有三点：

- 扩展新接入源时，不需要修改 Controller 主干代码，只需要新增插件。
- Agent 感知到的始终是统一消息模型，而不是各个平台的原始协议。
- 不同平台的连接方式和心跳策略被封装在 Adapter 内部，避免把性能成本扩散到控制面主流程。

## 统一契约

Gateway 适配器层建议定义统一契约，而不是让每个渠道各自发明自己的接口。

建议抽象：

- `IPuddingGatewayAdapter`：适配器主接口。
- `GatewayAdapterDescriptor`：声明 Adapter 类型、版本、来源、支持能力、信任级别。
- `GatewayAdapterContext`：宿主提供的日志、配置、审计、回调与取消令牌上下文。
- `PuddingIngressEnvelope`：统一入站事件模型。
- `PuddingEgressEnvelope`：统一出站回复模型。
- `GatewayAdapterHost`：负责加载、启停、探活、隔离与热更新。

建议接口语义：

- `SubscribeAsync`：建立事件订阅、Webhook 挂载、长连接或本地监听。
- `PublishAsync`：向外部系统回写消息、命令、通知或结构化结果。
- `OnEventReceived` 或等价回调：把原始平台事件转换为统一 Envelope 并投递给 Gateway。

如果首个实现阶段仍沿用 `IChannelProvider` / `ChannelPluginHost` 命名，也应把它视为 Adapter Plugin 模式的 V1 子集，而不是最终边界。

## 动态加载与热插拔

- Gateway 应支持内置 Adapter 与外部 Adapter 两种来源。
- 外部 Adapter 可通过程序集加载、配置热更新或插件目录扫描动态接入。
- 新增接入源时，不应要求重启整个 Controller。
- 热插拔必须带有版本、签名、来源和兼容性校验，避免把任意 DLL 直接注入控制面。

## 事件驱动与性能收益

Adapter Plugin 模式的关键价值之一，是把“连接策略”下沉到适配器内部，让不同平台按自己的最佳方式工作，而不是统一走高频轮询。

典型模式：

- 云端协作工具：适配器内部封装 Webhook、SSE 或长连接，只有事件发生时才上送 Gateway。
- 嵌入式 Runtime 节点：适配器监听本地 WebSocket、Named Pipe、内存总线或进程回调，在宿主软件状态变化时立即上报。
- MQTT 设备：适配器只订阅关注的 Topic，设备在心跳间隔之外出现告警时仍可即时推送。

因此，心跳或健康检查只应作为节点保活和降级判断机制存在，而不应成为业务事件传递的主通道。

## 事件总线与订阅模型

如果需要单独讨论事件命名、Envelope 结构、幂等、重放、死信、订阅治理与唤醒语义，应进一步参考 [10事件系统与事件总线](10事件系统与事件总线.md)。

建议最小事件模型至少包含以下维度：

- `eventId`：事件唯一标识。
- `eventType`：事件类型，例如 `device.alarm.triggered`、`task.completed`、`agent.completed`、`runtime.heartbeat`。
- `scope`：`global` 或 `workspace`。
- `workspaceId`：Workspace 域事件的治理边界。
- `source`：来源 Adapter、Runtime、Agent、系统模块。
- `subject`：事件关联对象，例如设备、任务、Session、Agent。
- `payload`：结构化负载。
- `trustLevel`：来源信任等级。
- `occurredAt`：事件发生时间。

事件总线上的订阅对象不应只有基础设施组件，也应包括 Agent 本身。Agent 可以声明：

- 感兴趣的事件类型。
- 可见的 Workspace 域。
- 唤醒条件与过滤条件。
- 消费后的动作，例如直接恢复执行、创建任务、写入记忆候选、发起审批。

Controller 负责判定“谁可以订阅什么事件”；Runtime 负责落实“订阅命中后如何恢复和执行”。

## MQTT 到 Workspace Agent 的典型链路

以智能家居场景为例：

1. 智能家居设备通过 MQTT 与 Gateway 的 MQTT Adapter 建立连接。
2. MQTT Adapter 订阅设备 Topic，并在设备状态变化或告警时收到原始消息。
3. Gateway 将 MQTT 消息转换为统一事件，例如 `device.sensor.triggered`。
4. Controller 根据设备映射、来源身份和策略，把事件路由到特定 Workspace 域。
5. 该 Workspace 中由 Runtime 托管的 Agent 订阅了这类事件，因此被直接唤醒。
6. Agent 在权限和审批约束下执行响应动作，例如通知用户、触发工作流、调用宿主原生能力或联动其他设备。

这个链路的重点不是“把 MQTT 接进来”，而是通过事件总线把外部世界的变化直接变成平台内部可治理、可订阅、可执行的触发器，从而避免低效轮询。

## 嵌入式 Runtime 节点如何接入 Gateway

当 `PuddingRuntime` 被嵌入其他 C# 桌面软件时，这个桌面软件会成为可调度 Runtime 节点。对应到 Gateway 层，不应把它当成普通 UI 客户端，而应通过专门的 Adapter 暴露为受控接入源。

建议抽象：

- `EmbeddedRuntimeAdapter`：负责接收宿主软件事件和回写控制命令。
- `NativeCapabilityDescriptor`：声明宿主软件暴露的原生能力，例如查询状态、执行测试、读取结果、获取日志。
- `RuntimeNodeIngressEvent`：Runtime 节点上送给 Gateway 的结构化事件。

这意味着旧桌面软件即使不原生理解 Pudding 协议，只要提供一个桥接 Adapter，也能被纳入可调度网络。

## 建议首批模块

- GatewayAdapterHost
- AdapterRegistry
- AdapterPolicyEvaluator
- ChannelManager
- SessionRouter
- AuthorizationService
- ApprovalService
- WorkflowEngine
- AuditStore
- RuntimeManager
- BillingManager

## Gateway 内部处理链

1. Adapter 建立订阅并监听原始平台事件。
2. Adapter 将原始数据清洗为 `PuddingIngressEnvelope`。
3. Gateway 完成基础校验、来源标记、信任分级和审计入队。
4. Controller 执行身份解析、Workspace 命中、事件域判定、AgentTemplate 路由和权限校验。
5. 事件被发布到对应的全局域或 Workspace 域事件总线。
6. Runtime、Agent、审计链路或其他控制面组件消费事件，并触发后续动作。
7. 如需对外反馈，处理结果经 `PuddingEgressEnvelope` 返回给对应 Adapter，由 Adapter 负责回写到原始系统。

## 冻结 / 恢复的事件广播实现原则

对于 `Freeze Agent`、`Freeze Workspace`、`Resume Agent`、`Resume Workspace` 这类治理动作，建议优先采用 **Controller 发布全局治理事件、Runtime 订阅执行** 的模型，而不是 Controller 逐个点对点调用 Runtime。

建议链路：

1. 用户在 `PuddingPlatform` / `PuddingPlatformAdmin` 点击 `Stop Workspace` 或 `Recover Workspace`
2. Platform 将治理命令发送给 Controller
3. Controller 完成权限、审计、Workspace 边界与目标合法性校验
4. Controller 发布全局治理事件，例如：
       - `workspace.freeze.requested`
       - `workspace.resume.requested`
       - `agent.freeze.requested`
       - `agent.resume.requested`
5. 所有 Runtime 订阅这些全局治理事件
6. 每个 Runtime 根据事件中的 `workspaceId` / `agentId` 检查自己当前承载的 Agent
7. 命中目标的 Runtime 执行冻结或恢复，并 stop / restart 对应 Docker 容器或 sandbox
8. Runtime 再回发结果事件，例如：
       - `workspace.freeze.applied`
       - `workspace.resume.applied`
       - `agent.freeze.applied`
       - `agent.resume.applied`

这种设计的关键收益：

- Controller 仍保持无状态控制面特征
- Runtime 不需要被 Controller 精确点名才能参与治理动作
- 多 Runtime 部署时，Workspace 全局冻结天然具备广播一致性
- Runtime 只处理自己当前承载的实例，避免控制面维护过重的点对点执行表

## 适配器治理要求

- Adapter 必须声明来源、能力、信任级别和运行模式。
- Adapter 的安装、启用、停用和升级必须可审计。
- Workspace 应能声明允许哪些 Adapter、哪些原生能力、哪些事件类型进入本域。
- 高风险 Adapter 能力必须接入审批链或人工确认链路。
- 异常 Adapter 必须可冻结、熔断、隔离，不得影响整个 Controller 主干。

## 不应下沉到 Runtime 的内容

- 全局审批规则。
- Workspace 暴露策略。
- 渠道到 Workspace 的映射控制。
- Adapter 插件是否允许启用。
- 高风险动作的治理判定。
