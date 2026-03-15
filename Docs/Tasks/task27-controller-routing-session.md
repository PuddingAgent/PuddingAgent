# task27 - PuddingController 路由与会话基础

最后更新：2026-03-15

## 任务目标

建立 `PuddingController` 作为统一控制入口的最小骨架，先打通 Gateway Adapter 接入、Workspace 命中、AgentTemplate 路由、ServiceSession 创建与基础权限校验。

对应架构：
- [../07架构/04PuddingController与Gateway.md](../07架构/04PuddingController与Gateway.md)
- [../07架构/07协作网络与治理.md](../07架构/07协作网络与治理.md)
- [../07架构/08数据模型与配置.md](../07架构/08数据模型与配置.md)

## 前置依赖

- 架构分层已确定：Controller 为控制面，Gateway 为内部模块。
- Workspace / ChannelBinding / AgentTemplate 基础模型已明确。

## 可并行关系

- 可与 [task26-runtime-foundation.md](task26-runtime-foundation.md) 并行推进。
- 可与 [task29-agent-template-and-audit.md](task29-agent-template-and-audit.md) 的模板数据建模并行。
- 客户端联调前，需要先完成本任务的 API 契约稳定。

## 顺序任务

1. 建立 `PuddingController` 宿主入口
说明：HTTP API、后台服务、控制面 DI、基础健康接口。
输出：最小可启动 Controller host。

2. 建立 `ChannelManager` 与 `ChannelPluginHost`
说明：把 Gateway 接入层明确为 Adapter Plugin 模式。V1 可保留 `ChannelPluginHost` 命名，但抽象上应支持 `IPuddingGatewayAdapter`、内置 CLI Adapter、Email Adapter，以及后续 Webhook、MQTT、嵌入式 Runtime Adapter。
输出：最小 Adapter 注册、装载与事件上送链路。
前置依赖：任务 1。

2A. 建立 `GatewayAdapterHost` 热插拔边界
说明：定义 Adapter 描述、来源校验、版本信息、探活状态、启停控制和配置 reload 行为，避免把 Adapter 仅实现为一次性 DI 注册。
输出：可查询、可启停、可审计的 Adapter 宿主。
前置依赖：任务 2。

3. 建立 `SessionRouter`
说明：根据 Adapter 来源、身份、消息类型和 Workspace 绑定规则，命中 Workspace 与 AgentTemplate。
输出：可查询的路由决策。
前置依赖：任务 2A。

4. 建立 `ServiceSession` 自动创建或复用逻辑
说明：收到消息时自动创建或复用 ServiceSession，并关联 Workspace 与 Runtime。
输出：Session 索引与状态查询接口。
前置依赖：任务 3。

5. 建立 `AuthorizationService`
说明：执行用户、WorkspaceRole、AgentTemplate 三者交集校验。
输出：拒绝原因与权限判定查询。
前置依赖：任务 3。

6. 建立最小控制协议到 Runtime
说明：把路由后的消息投递到 Runtime，并接收回复和状态。
输出：Controller 到 Runtime 的最小调用协议。
前置依赖：任务 4、任务 5；联调依赖 [task26-runtime-foundation.md](task26-runtime-foundation.md)。

7. 建立首批调试查询接口
说明：支持查询路由、Session 状态、拒绝原因、Runtime 映射和 Adapter 状态。
输出：面向 CLI/Web/Avalonia 的基础调试接口。
前置依赖：任务 6。

8. 建立事件驱动接入与回写验证
说明：验证 Adapter 既能接收入站事件，也能把 Runtime 或 Controller 的结果按原协议回写到外部系统，并尽量使用 Webhook、订阅或长连接而不是高频轮询。
输出：首批双向 Adapter 联调链路。
前置依赖：任务 6。

## 验收标准

- Controller 可接收 CLI through Controller API 的消息。
- Gateway 可装载多个 Adapter，并可查询其启停、版本、健康状态与能力声明。
- 系统可命中 Workspace 和 AgentTemplate。
- ServiceSession 可自动创建或复用。
- Controller 可完成最小权限校验与拒绝原因返回。
- Controller 能把消息投递到 Runtime 并获得真实回复。
- 至少 1 个事件驱动 Adapter 与 1 个非 CLI Adapter 的接入模式被验证，证明新增接入源不需要修改 Controller 主路由骨架。
