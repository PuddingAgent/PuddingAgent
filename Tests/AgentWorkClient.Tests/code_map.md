# Tests/AgentWorkClient.Tests
> 源指纹: *.cs=1-23f71ea14c2c, *.csproj=1-a95d2001e193 · 条目数: 2 · 最近整理: 2026-11-22

Agent 工作项样例的离线测试工程：假工作 API，不接真实网络、Core 或 Agent。

> 源指纹: Tests/AgentWorkClient.Tests/**=pending · 条目数: 2 · 最近整理: 2026-11-22

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `WorkDemoTests.cs` | 覆盖提交与查询的原样透传与状态不改写 | `WorkDemoTests` | `Samples/AgentWorkClient/AgentWorkClient.csproj` | 未知副作用只能是 exit 4 且不得重试；查询失败或已取消的条目仍须 exit 0 且不重写状态 |
| `AgentWorkClient.Tests.csproj` | 定义 MSTest 运行器与本工程的唯一引用 | — | `Samples/AgentWorkClient/AgentWorkClient.csproj` | `MSTest.Sdk/4.0.1` 且 `OutputType=Exe`、`UseVSTest=false`；唯一 ProjectReference 是样例工程 |
