# PuddingModelsTests

模型策略与报价的离线测试工程：假配置存储、假报价与假目录，不需要真实 provider。

> 源指纹: Source/PuddingModelsTests/**=pending · 条目数: 7 · 最近整理: 2026-11-22

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `ModelTestHost.cs` | 提供配置存储、报价源与目录源的独立假实现 | `FakeModelPolicyStore`、`FakeQuoteSource`、`FakeCatalogSource`、`ModelFixtures` | `Source/PuddingModels.Contracts/ModelPolicyPorts.cs` | 这些是测试替身而不是产品实现，不得被生产工程引用 |
| `EffectiveModelPolicyTests.cs` | 覆盖配置分层优先级与 CAS 写入语义 | `EffectiveModelPolicyTests` | `Source/PuddingModels/ModelPolicyResolver.cs` | 断言区分「写入 revision」与「生效 revision」；生效只从下一轮开始 |
| `ModelPolicyPatchApplierTests.cs` | 覆盖补丁的 set、clear 与 inherit 三种形态 | `ModelPolicyPatchApplierTests` | `Source/PuddingModels/ModelPolicyPatchApplier.cs` | 补丁只能落到实例层；重复或自相矛盾的补丁必须被拒 |
| `ModelQuoteTests.cs` | 覆盖报价窗口与缓存价计费 | `ModelQuoteTests` | `Source/PuddingModels/ModelQuoteService.cs` | 报价窗口是半开区间，判据取真实派发时刻而不是取单时刻 |
| `QuoteWindowAdjusterTests.cs` | 覆盖跨窗口派发时的报价刷新与预算阻断 | `QuoteWindowAdjusterTests` | `Source/PuddingModels/QuoteWindowAdjuster.cs` | 刷新报价不得复用旧价；预算不足只能阻断，不得把未知价当零 |
| `ModelLeaseGenerationTests.cs` | 覆盖租约代际切换与槽位释放 | `ModelLeaseGenerationTests` | `Source/PuddingModels/ModelLeaseRegistry.cs` | 新代际只拦新派发，旧租约仍须可释放；降级不得抢占在跑的槽位 |
| `PuddingModelsTests.csproj` | 定义 MSTest 运行器与本工程的唯一引用 | — | `Source/PuddingModels/PuddingModels.csproj` | `MSTest.Sdk/4.0.1` 且 `OutputType=Exe`、`UseVSTest=false`；唯一的 ProjectReference 是被测工程 |
