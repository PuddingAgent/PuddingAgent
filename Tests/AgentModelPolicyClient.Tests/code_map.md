# Tests/AgentModelPolicyClient.Tests
> 源指纹: *.cs=1-23a093aacd76, *.csproj=1-e0e4d5b5f3bf · 条目数: 3 · 最近整理: 2026-11-22

模型策略只读示例客户端的离线测试工程：用注入的假 API 锁住退出码、输出契约与不写行为。

> 源指纹: Tests/AgentModelPolicyClient.Tests/**=pending · 条目数: 2 · 最近整理: 2026-11-22

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `PolicyDemoTests.cs` | 断言查询一次、零次写入，以及四类快照原样透出的 JSON 契约 | `PolicyDemoTests` | `PolicyDemo`、`IAgentModelPolicyApi` | 假 API 的 `UpdateAsync` 必须抛异常：只读示例一旦写入，测试立即取红 |
| `AgentModelPolicyClient.Tests.csproj` | MSTest.Sdk 的可执行测试工程，只引用示例客户端 | — | `../../Samples/AgentModelPolicyClient/AgentModelPolicyClient.csproj` | `UseVSTest=false`，产物是自宿主测试可执行文件；不得引入真实网络或 Core 依赖 |
