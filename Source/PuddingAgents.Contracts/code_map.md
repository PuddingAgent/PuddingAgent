# Agent backend ports

Agent 后端的 BCL-only 端口：宿主绑定的身份、目录读取、工作事件与控制入口；调用方不得自带根身份。

> 源指纹: Source/PuddingAgents.Contracts/**=pending · 条目数: 3 · 最近整理: 2026-11-22

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `AgentPorts.cs` | 定义宿主绑定的 Agent 端口：目录存储与工作事件源、取消网关与子代理租约池 | `AgentBinding`、`IAgentDirectoryStore`、`IWorkEventSource`、`AgentErrorCodes` | `AgentDirectoryQueryResult` | 根/执行者/父身份由宿主注入，调用方不能自报 |
| `AgentAdapterPorts.cs` | 外部适配器绑定的 workspace、Agent 与插件安装身份及连接租约 | `AgentAdapterBinding`、`AgentAdapterLease`、`AgentAdapterErrorCodes` | `AgentBinding` | 租约用于栅栏外部适配器操作，过期即失效 |
| `PuddingAgents.Contracts.csproj` | 纯契约工程，无工程引用与包依赖 | — | `PuddingAgents.Contracts` | 保持 BCL-only，引入实现依赖会破坏分层 |
