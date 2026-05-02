# Controller、Connector 与 P2P 网络（进程内模块）

> **2026-05-02 简化**：Controller 和 Gateway 不再是独立进程，而是 Pudding Agent 内的模块。
> **2026-05-02 修订**：引入"连接器（Connector）"概念，替代旧的 Gateway Adapter Plugin 架构。

## Controller 模块

同进程内的控制逻辑：

- HTTP API 路由（接收 Web UI 和外部 Agent 的请求）
- 用户鉴权与 Workspace 权限校验
- Session 管理
- 审计日志记录
- 对等 Agent 的 P2P 请求转发
- **事件路由**：将连接器入站事件分发到正确的处理链路（自己处理或 P2P 转发）

## 连接器（Connector）

连接器是对外部协议通道的完整抽象，**替代旧的 Gateway Adapter Plugin**：

- 连接器 = 双向通道管理器，不仅能接收外部事件，还能主动发送和操作外部系统
- 类比 MCP 为 LLM 提供工具，**连接器为 Agent 提供外部通道的操作接口**
- 每个协议一个连接器：邮箱、MQTT、Webhook、Home Assistant 等
- 全部作为进程内模块，通过 DI 注册
- 参考设计：[Tasks/task36-event-trigger-and-subagent.md](../Tasks/task36-event-trigger-and-subagent.md) §0

```
┌─ IPuddingConnector ──────────────────────────────┐
│  StartAsync → 建立通道连接，开始监听               │
│  StopAsync  → 关闭通道连接                        │
│  SendAsync  → 向外部发送消息                       │
│  OperateAsync → 操作外部通道（如管理邮箱）          │
│  GetDiagnosticsAsync → 健康检查与诊断               │
│  (via ConnectorContext) OnEventReceived → 入站回调 │
└──────────────────────────────────────────────────┘
```

已规划连接器：
- **WebChat 连接器**（Web UI 对话通道）
- **CLI 连接器**（命令行交互通道）
- **邮箱连接器**（IMAP/SMTP：收/发/管）
- **Webhook 连接器**（HTTP POST 事件接收）
- **MQTT 连接器**（IoT 设备消息订阅/发布）

## P2P 网络层

- mDNS 自动发现对等 Agent
- UDP 广播节点存在
- HTTP/gRPC 直连通信
- 事件在 Agent 之间直接传播

不再需要旧的 Gateway Adapter Plugin 动态加载架构。连接器以编译时 DI 注册取代运行时插件扫描。