# Tests/AgentDirectoryClient.Tests

Agent 目录样例的离线测试工程：假目录 API，不接真实网络、Core 或 Agent。

> 源指纹: Tests/AgentDirectoryClient.Tests/**=pending · 条目数: 2 · 最近整理: 2026-11-22

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `DirectoryDemoTests.cs` | 覆盖目录查询与列表参数的原样透传 | `DirectoryDemoTests` | `Samples/AgentDirectoryClient/AgentDirectoryClient.csproj` | 断言区分「调用零次」与「恰好一次」；空页必须保留 `agents` 与 `cursor` 字段；未知命令与空字段走 exit 2 |
| `AgentDirectoryClient.Tests.csproj` | 定义 MSTest 运行器与本工程的唯一引用 | — | `Samples/AgentDirectoryClient/AgentDirectoryClient.csproj` | `MSTest.Sdk/4.0.1` 且 `OutputType=Exe`、`UseVSTest=false`；唯一 ProjectReference 是样例工程 |
