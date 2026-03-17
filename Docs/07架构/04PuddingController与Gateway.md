# PuddingController 与 Gateway

## 定位

PuddingController 是整个协作网络的控制面宿主。PuddingGateway 是它内部的边界接入模块，而不是长期独立存在的平级产品。

比如：
路由
授权
审批
审计
调度
Adapter 治理
Runtime 节点管理
Session 控制链路


Gateway 的核心设计不应是为每一种外部系统硬编码接入逻辑，而应采用 Adapter Plugin 模式，把协议转换、事件订阅、身份标准化和出站回写封装为可热插拔的适配器插件。

## Controller 负责的能力

- Runtime 节点注册、发现、健康检查与容量感知。
- 渠道路由、Session 建立、用户与租户边界隔离。
- 认证、鉴权、审批、审计、治理与冻结控制。
- Workflow 调度入口、Swarm 协调与控制面事件输出。
- 插件注册、安装、启停、版本与装配策略。
- 对 CLI、Web、Avalonia 以及其他客户端暴露控制接口。



## Gateway 负责的能力

- 作为 Adapter Plugin 宿主，承载外部系统接入与生命周期管理。
- HTTP、WebSocket、MQTT、Webhook、本地总线等接入协议承载。
- 统一入口与统一出站回复。
- 渠道消息、宿主事件、设备事件的标准化、身份解析和边界协议转换。
- 把不同外部系统产生的原始事件转换为统一的 `PuddingIngressEnvelope` 或 `PuddingMessage`。

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
4. Controller 执行身份解析、Workspace 命中、AgentTemplate 路由和权限校验。
5. 处理结果经 `PuddingEgressEnvelope` 返回给对应 Adapter，由 Adapter 负责回写到原始系统。

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
