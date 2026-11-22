# Tests/ExecutionBudgetClient.Tests
> 源指纹: *.cs=1-0077fae976ae, *.csproj=1-84f659e6d315 · 条目数: 3 · 最近整理: 2026-11-22

执行预算样例的离线测试工程：假预算 API，不接真实网络、Core 或 Agent。

> 源指纹: Tests/ExecutionBudgetClient.Tests/**=pending · 条目数: 2 · 最近整理: 2026-11-22

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `BudgetDemoTests.cs` | 覆盖预算快照字段的原样输出 | `BudgetDemoTests` | `Samples/ExecutionBudgetClient/ExecutionBudgetClient.csproj` | Unknown 金额不得归零，耗尽或已取消的快照仍须 exit 0；缺快照走 exit 3 而不是空对象 |
| `ExecutionBudgetClient.Tests.csproj` | 定义 MSTest 运行器与本工程的唯一引用 | — | `Samples/ExecutionBudgetClient/ExecutionBudgetClient.csproj` | `MSTest.Sdk/4.0.1` 且 `OutputType=Exe`、`UseVSTest=false`；唯一 ProjectReference 是样例工程 |
