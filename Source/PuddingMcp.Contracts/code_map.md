# PuddingMcp.Contracts

MCP 端口契约（PA-05C）：只定义连接状态与请求语义，传输与凭据由适配器实现，BCL-only。

> 源指纹: Source/PuddingMcp.Contracts/**=pending · 条目数: 2 · 最近整理: 2026-11-22

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `McpPorts.cs` | 定义 MCP 连接引用、请求响应与事件游标端口 | `McpServerRef`、`McpConnectionState`、`IMcpTransport`、`IMcpCursorStore`、`McpErrorCodes` | `Source/PuddingMcp/McpConnectionCoordinator.cs` | 超时与不确定结果一律是 Unknown，不得自动重试；进程与 token 存储留在适配器侧 |
| `PuddingMcp.Contracts.csproj` | 构建期锁死端口工程的零依赖边界 | — | — | `EnforceMcpPortsBoundary` 目标会失败构建：出现任何 ProjectReference 或 PackageReference 即报错 |
