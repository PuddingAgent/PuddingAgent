# Tests/ModelEvaluationClient.Tests

模型评估样例的离线测试工程：假评估 API，不接真实网络、Core 或 Agent。

> 源指纹: Tests/ModelEvaluationClient.Tests/**=pending · 条目数: 2 · 最近整理: 2026-11-22

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `EvaluationDemoTests.cs` | 覆盖分类与评审回执的字段与降级语义 | `EvaluationDemoTests` | `Samples/ModelEvaluationClient/ModelEvaluationClient.csproj` | 回执不得凭空补 `verified` 字段；Abstain 与 Degraded 都要原样保留并以 exit 0 输出 |
| `ModelEvaluationClient.Tests.csproj` | 定义 MSTest 运行器与本工程的唯一引用 | — | `Samples/ModelEvaluationClient/ModelEvaluationClient.csproj` | `MSTest.Sdk/4.0.1` 且 `OutputType=Exe`、`UseVSTest=false`；唯一 ProjectReference 是样例工程 |
