# Operator API Integration Tests
> 源指纹: *.cs=2-8f36ab99e5e8, *.csproj=1-8dd8410b917a · 条目数: 3 · 最近整理: 2026-11-12

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `OperatorAdapterTests.cs` | 原始打分器等价性与越界观测校验。 | `OperatorAdapterTests` | `Source/Pudding.Api.Contracts/OperatorEvaluation.cs` | 无数据与真实零分必须可区分。 |
| `OperatorWorkerTests.cs` | Worker 到原始打分器的端到端链路验收。 | `OperatorWorkerTests` | `Tests/PuddingToolPlugins.Fixture/OperatorPlugin.cs` | 打分失败不得据此断言早前工具写入不存在。 |
| `TestScripts/test-operator-api.ps1` | 隔离构建测试入口，与 Worker 同 apphost 形态。 | — | `Tests/PuddingOperatorApi.IntegrationTests/PuddingOperatorApi.IntegrationTests.csproj` | 不接线上产品与 LLM。 |
