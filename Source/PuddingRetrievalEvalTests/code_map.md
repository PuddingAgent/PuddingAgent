# PuddingRetrievalEvalTests — 评测组件独立测试工程（S2/S3）

> 被侧：`Source/PuddingRetrievalEval`（组件化交付规程 S2）。
> **`ProjectReference` 恰好 1 条**，且只指向 `PuddingRetrievalEval`。

## 为什么它就是一个独立工程

评测组件的价值在于它是**叶子**：能在**不加载宿主、不加载 Roslyn/MSBuild/Lucene** 的情况下构建与测试
（规程 R1：不重启宿主即可开发调试）。因此测试也**不得**引用 `PuddingCodeIntelligence` / `PuddingRuntime` /
`PuddingHost` / `PuddingAgent` / `PuddingCodeIndex` / `PuddingFullTextIndex`。

## 文件

| 文件 | 内容 |
|---|---|
`StubSearchProbe.cs` | 手工替身探针：可编排的命中列表（`AlwaysHits` / `ForQuery` / `Sequence`）、可注入耗时与失败；`TestHits` 是构造命中列表的助手。所有准确率断言都喂已知期望值给替身，**不依赖任何引擎的行为** |
`RetrievalMetricsTests.cs` | 指标正确性（A2）逐项：`recall@1/5/10` 的窗口与分母、`MRR` 位置（第 1 位=1.0 / 第 3 位=1/3 / 全落空=0）、`precision@k` 分母为 k、**噪声率**（全落 `node_modules` ⇒ 1.0）、k 大于命中数、重复期望、同文件多行命中、k ≤ 0 抛错。内含 `ExpectThrows<T>` 助手 |
`PathIdentityTests.cs` | 大小写 / 分隔符 / `./` / 仓储相对 vs 绝对路径 / `#Symbol` 约束 / 引擎不报 symbol 时不惩罚 / `DistinctExpected` / `DistinctByFile` |
`NoiseDirectoryRulesTests.cs` | 噪声段名集合是**三套规则的 46 项并集**（断言集合大小与三套各自的代表项）；段名**精确匹配**（`bin.cs` 不是噪声、`bin` 是）；大小写与分隔符不敏感；根级路径与尾部分隔符 |
`LatencyStatisticsTests.cs` | 最近秩百分位（4 样本 p50=20、10 样本 p95=10）、单样本、**亚毫秒分辨率**、空桶 = `LatencySummary.Empty`（不是"很快"）、乱序输入不影响结果、非法百分位抛错 |
`EvalSetLoaderTests.cs` | A1：加载受版本控制的 `eval/sets/seed-v1.json`（80 条、三语言各 ≥15、三类 kind 齐备、无重复标注、每条至少一个期望）；fail-closed 反例：未知 kind/language、空 cases、缺 cases、空 query、空/重复 expectedHits、非法 JSON、坏 version、语言别名 |
`EvalRunnerTests.cs` | 冷/热调用序列（1 冷 + warmup + measured）、**只给第一个已测重复打分**、重复间不一致必须被报出、探针失败计为 0 分且计数、按语言分层、空用例集与 `MaxResults < 10` 抛错、替身探针下确定性 |
`EvalReportWriterTests.cs` | 报告契约：显式"不设阈值"、冷/热两行都在、每 case 与每期望命中都列出、枚举按名字序列化、同一 run 两次序列化逐字节相同、落盘 UTF-8 **无 BOM** |
`ComponentBoundaryTests.cs` | S3/S4 边界断言：进程内 + `deps.json` 依赖闭包都不得含禁用程序集；**检测器自带阳性对照**；控制组断言（组件本身必须在闭包内，否则检查是空的）|

## 验证

```
dotnet test Source\PuddingRetrievalEvalTests\PuddingRetrievalEvalTests.csproj -p:CollectCoverage=false
```
78/78 通过、exit 0（不启动 Core/Desktop，不需要任何后台服务）。

## 变异取红（都实测过，原始输出在 `temp/test-out/`）

| 变异 | 结果 |
|---|---|
`recall@k` 分母改成 `expected.Count + 1` | **8 红 / 70 绿 / 78**（`u4-0-mutation-A-recall-denominator.log`）|
`MRR` 位置改成 `1d / (bestRank + 2)` | **5 红 / 73 绿 / 78**（`u4-0-mutation-B-mrr-position.log`）|
复原 | `RetrievalMetrics.cs` blob hash 逐位相同（`93edd683b5396ebeb03c9026a483290be656ede9`），78/78 绿（`u4-0-restore-green.log`）|
组件临时加一条指向 `PuddingFullTextIndex` 的 `ProjectReference` | **1 红 / 77 绿 / 78**，报出 `Lucene.Net … PuddingFullTextIndex`（`u4-0-a8-boundary-mutation.log`）；撤销后 csproj hash 逐位相同、78/78 绿（`u4-0-a8-boundary-restored.log`）|
