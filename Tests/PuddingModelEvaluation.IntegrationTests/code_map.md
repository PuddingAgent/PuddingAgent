# Model evaluation integration
> 源指纹: *.cs=2-266dbc99d6c2, *.csproj=1-c25cac1f039a · 条目数: 3 · 最近整理: 2026-11-12

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `ModelWorkerTests.cs` | 真实 Worker 的分类与评审、来源隔离和父额度。 | `ModelWorkerTests` | `Tests/PuddingToolPlugins.Fixture/ModelEvaluationPlugin.cs` | 迟到的 provider 结果不得改写已取消的运行。 |
| `ConfiguredModelTests.cs` | 离线 HTTP 网关下的请求限界与精确计量。 | `ConfiguredModelTests` | `Source/PuddingModels/ModelQuoteService.cs` | 无重试；配置不可用须显式失败。 |
| `PuddingModelEvaluation.IntegrationTests.csproj` | 集成测试工程：运行时加真实夹具。 | `PuddingRuntime` | `Source/PuddingRuntime`、`Tests/PuddingToolPlugins.Fixture` | 不得连接真实产品与外部凭据。 |
