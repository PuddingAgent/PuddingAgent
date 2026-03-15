# PuddingController 与 Gateway

## 定位

PuddingController 是整个协作网络的控制面宿主。PuddingGateway 是它内部的边界接入模块，而不是长期独立存在的平级产品。

## Controller 负责的能力

- Runtime 节点注册、发现、健康检查与容量感知。
- 渠道路由、Session 建立、用户与租户边界隔离。
- 认证、鉴权、审批、审计、治理与冻结控制。
- Workflow 调度入口、Swarm 协调与控制面事件输出。
- 插件注册、安装、启停、版本与装配策略。
- 对 CLI、Web、Avalonia 以及其他客户端暴露控制接口。

## Gateway 负责的能力

- HTTP、WebSocket、MQTT 等接入协议承载。
- 统一入口与统一出站回复。
- 渠道消息标准化、身份解析和边界协议转换。

## 建议首批模块

- ChannelManager
- ChannelPluginHost
- SessionRouter
- AuthorizationService
- ApprovalService
- WorkflowEngine
- AuditStore
- RuntimeManager
- BillingManager

## 不应下沉到 Runtime 的内容

- 全局审批规则。
- Workspace 暴露策略。
- 渠道到 Workspace 的映射控制。
- 插件是否允许启用。
- 高风险动作的治理判定。
