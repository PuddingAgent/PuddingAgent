# PuddingMcpTests

MCP 连接协调的离线测试工程：可控传输与内存游标，不需要真实 MCP 服务。

> 源指纹: Source/PuddingMcpTests/**=pending · 条目数: 3 · 最近整理: 2026-11-22

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `McpTestHost.cs` | 提供可控传输、内存游标存储与固定时钟 | `FakeMcpTransport`、`FakeCursorStore`、`FixedClock`、`McpFixtures` | `Source/PuddingMcp.Contracts/McpPorts.cs` | 这些是测试替身而不是产品实现，不得被生产工程引用 |
| `McpConnectionTests.cs` | 覆盖连接状态机与超时未知语义 | `McpConnectionTests` | `Source/PuddingMcp/McpConnectionCoordinator.cs` | 超时类用例依赖固定时钟而非真实等待；改 Unknown 语义必须同步改本文件 |
| `PuddingMcpTests.csproj` | 定义 MSTest 运行器与本工程的唯一引用 | — | `Source/PuddingMcp/PuddingMcp.csproj` | `MSTest.Sdk/4.0.1` 且 `OutputType=Exe`、`UseVSTest=false`；唯一的 ProjectReference 是被测工程 |
