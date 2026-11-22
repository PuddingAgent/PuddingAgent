# PuddingMcp
> 源指纹: *.cs=1-30692c6bf09b, *.csproj=1-074e762ee2fd · 条目数: 0 · 最近整理: 2026-11-22

MCP 连接协调实现（PA-05C）：管生命周期、请求相关性与事件游标，宿主不感知传输细节。

> 源指纹: Source/PuddingMcp/**=pending · 条目数: 2 · 最近整理: 2026-11-22

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `McpConnectionCoordinator.cs` | 协调 MCP 连接生命周期并推进事件游标 | `McpConnectionCoordinator`、`ConnectAsync`、`RequestAsync`、`AcceptEventsAsync`、`DrainAsync` | `Source/PuddingMcp.Contracts/McpPorts.cs` | 启动失败绝不得报 Ready；请求需 Ready 且代际一致；drain 后拒绝新请求并释放传输 |
| `PuddingMcp.csproj` | 构建期锁死本工程只依赖端口工程 | — | `Source/PuddingMcp.Contracts/PuddingMcp.Contracts.csproj` | `EnforceMcpBoundary` 目标会失败构建：引用非 Contracts 工程或任何 PackageReference 都报错 |
