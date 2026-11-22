# Mcp.Cli
> 源指纹: Tests/Mcp.Cli/**=2-4fb811fdbfc3 · 条目数: 1 · 最近整理: 2026-11-22

MCP 协议与本机 Codex 服务链路的探针命令行（net10.0，引 ModelContextProtocol.Core）。自带最小 MCP 服务端与被测客户端，另有面向本机 Pudding MCP 端点的冒烟模式。

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `Program.cs` | MCP 协议冒烟与本机 Codex 服务链路探针 | `StrictMcpServer`、`FakeCodexStdioServer`、`RunCodexServiceSmokeAsync` | `Source/PuddingMcp/` | 服务端断言进程内自起自停，不连线上产品也不改配置文件 |
