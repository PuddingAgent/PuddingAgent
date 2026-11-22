# PuddingRetrievalEvalTests — 评测组件独立测试工程（S2/S3）
> 源指纹: *.cs=9-6d0220e8fff6, *.csproj=1-dbfc4ae42722 · 条目数: 9 · 最近整理: 2026-11-22

> 被侧：`Source/PuddingRetrievalEval`（组件化交付规程 S2）。
> **`ProjectReference` 恰好 1 条**，且只指向 `PuddingRetrievalEval`。

## 为什么它就是一个独立工程

评测组件的价值在于它是**叶子**：能在**不加载宿主、不加载 Roslyn/MSBuild/Lucene** 的情况下构建与测试
（规程 R1：不重启宿主即可开发调试）。因此测试也**不得**引用 `PuddingCodeIntelligence` / `PuddingRuntime` /
`PuddingHost` / `PuddingAgent` / `PuddingCodeIndex` / `PuddingFullTextIndex`。

## 文件

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `StubSearchProbe.cs` | 可编排的手工替身探针：命中列表、耗时与失败注入 | `StubSearchProbe`、`TestHits` | `EvalRunnerTests.cs` | 准确率断言只能喂已知期望值，不得依赖任何引擎行为 |
| `RetrievalMetricsTests.cs` | recall@k 窗口与分母、MRR 位置、precision@k 与噪声率 | `RetrievalMetricsTests` | `RetrievalMetrics` | recall@k 分母改成 `expected.Count + 1` 取红 8 条；MRR 位置改成 `1d/(bestRank+2)` 取红 5 条 |
| `PathIdentityTests.cs` | 路径身份归一的匹配行为 | `PathIdentityTests` | `PathIdentity` | 引擎不报 symbol 时不得惩罚调用方期望 |
| `NoiseDirectoryRulesTests.cs` | 噪声段名集合是三套规则的 46 项并集 | `NoiseDirectoryRulesTests` | `PathNoiseRules` | 段名必须精确匹配：`bin.cs` 不是噪声而 `bin` 是 |
| `LatencyStatisticsTests.cs` | 最近秩百分位与亚毫秒分辨率 | `LatencyStatisticsTests` | `LatencySummary` | 空桶必须报 `LatencySummary.Empty`，不得报「很快」 |
| `EvalSetLoaderTests.cs` | 受版本控制用例集的加载与 fail-closed 反例 | `EvalSetLoaderTests` | `EvalSetLoader` | 未知 kind 或 language 必须拒绝，不得降级成默认分层 |
| `EvalRunnerTests.cs` | 冷/热调用序列与只给第一个已测重复打分 | `EvalRunnerTests` | `StubSearchProbe.cs` | 重复间不一致必须报出；探针失败计 0 分并计数 |
| `EvalReportWriterTests.cs` | 报告契约：显式不设阈值与逐 case 列出 | `EvalReportWriterTests` | `EvalReportWriter` | 同一 run 两次序列化必须逐字节相同且落盘 UTF-8 无 BOM |
| `ComponentBoundaryTests.cs` | 进程内与 `deps.json` 闭包都不得含禁用程序集 | `ComponentBoundaryTests` | `PuddingRetrievalEvalTests.csproj` | 检测器必须自带阳性对照，否则检查可能整体空转 |

## 验证

`dotnet test Source\PuddingRetrievalEvalTests\PuddingRetrievalEvalTests.csproj -p:CollectCoverage=false`
78/78 通过、exit 0（不启动 Core/Desktop，不需要任何后台服务）。

## 变异取红

四条变异（recall 分母、MRR 位置、边界加引用、复原）的原始日志在 `temp/test-out/`；
要点已并入上表 `约束` 列，复原后 78/78 绿。
