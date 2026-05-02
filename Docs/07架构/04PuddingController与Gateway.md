# Controller 与 Gateway（进程内模块）

> **2026-05-02 简化**：Controller 和 Gateway 不再是独立进程，而是 Pudding Agent 内的模块。

## Controller 模块

同进程内的控制逻辑：

- HTTP API 路由（接收 Web UI 和外部 Agent 的请求）
- 用户鉴权与 Workspace 权限校验
- Session 管理
- 审计日志记录
- 对等 Agent 的 P2P 请求转发

## P2P 网络层（替代原 Gateway）

- mDNS 自动发现对等 Agent
- UDP 广播节点存在
- HTTP/gRPC 直连通信
- 事件在 Agent 之间直接传播

不再需要 Gateway Adapter Plugin 架构。