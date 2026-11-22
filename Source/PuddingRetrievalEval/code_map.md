# PuddingRetrievalEval

检索评测叶子组件：只测检索质量与耗时，不改变任何检索行为，通过 `ISearchProbe` 端口观察引擎。

> 源指纹: Source/PuddingRetrievalEval/**=pending · 条目数: 13 · 最近整理: 2026-11-22

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `Contracts/EvalCase.cs` | 定义标注用例与用例集的数据形状 | `EvalCase`、`EvalSet`、`EvalKind`、`EvalLanguage` | `Services/EvalSetLoader.cs` | `EvalKind.Unknown = 0` 只为占位，加载器永不产出它 |
| `Contracts/SearchProbeContracts.cs` | 定义探针端口及请求与命中契约 | `ISearchProbe`、`SearchProbeRequest`、`SearchProbeOutcome`、`SearchProbeClaim` | `Services/EvalRunner.cs` | 耗时由探针自己测；`Claim` 只能用纯字符串描述已查与未查面，不得引用任何引擎类型 |
| `Services/PathIdentity.cs` | 统一路径比较口径并支持按文件去重 | `PathIdentity` | `RetrievalMetrics` | 期望命中按文件去重，同文件多行只算一次，否则话多的文件能占满 top-k |
| `Services/NoiseDirectoryRules.cs` | 取三套现存噪声目录规则的并集作为噪声定义 | `NoiseDirectoryRules` | `PathIdentity` | 并集是保守取法（宁可多报噪声）；选哪套规则由 U4-1 裁决，本组件不拍板 |
| `Services/RetrievalMetrics.cs` | 计算四项检索质量指标及其分子分母 | `RetrievalMetrics` | `PathIdentity` | 分母必须在 XML 文档逐条写明；`precision@k` 的 k 是请求数，不是实际返回条数 |
| `Services/LatencyStatistics.cs` | 用最近秩法计算百分位与延迟摘要 | `LatencyStatistics`、`LatencySummary` | `EvalRun` | 空样本必须返回 `Empty`（表示无样本），不得当成「很快」 |
| `Services/EvalSetLoader.cs` | 加载受版本控制的标注集并做严格格式校验 | `EvalSetLoader`、`EvalSetFormatException` | `eval/sets` | fail-closed：未知 kind、空 query、重复 expectedHits、非法 JSON 一律抛异常 |
| `Services/EvalRunner.cs` | 每个用例按冷调用加预热加测量重复执行并比对一致性 | `EvalRunner`、`EvalRunOptions` | `ISearchProbe`、`EvalRun` | 准确率取第一个已测重复；重复间不一致必须暴露；空用例集或 `MaxResults < 10` 直接拒绝 |
| `Services/EvalRun.cs` | 运行结果模型：分数与分层聚合、冷热延迟摘要 | `EvalRun`、`EvalCaseScore`、`EvalSlice`、`EvalObservation` | `EvalRunner` | 冷与热必须分开计时，不得合并成一个平均值 |
| `Services/EvalReportWriter.cs` | 把运行结果渲染成 Markdown 与 JSON 并落盘 | `EvalReportWriter` | `EvalRun` | 报告必须显式声明不设阈值；落盘为 UTF-8 无 BOM |
| `eval/sets` | 受版本控制的标注集，含种子集与中文概念反例集 | — | `Services/EvalSetLoader.cs` | 反例集断言的是 `claim.kind == None` 且已查面含 vector，不是断言命中 |
| `eval/reports` | 基线报告与四变体矩阵的历史产物，含环境冻结块 | — | `eval/sets` | 报告是历史快照，重跑前先看 `.probe.json` 里的数据 sha256 与 HEAD 是否对得上 |
| `PuddingRetrievalEval.csproj` | 把本工程锁成零引用的叶子，并带上标注集内容 | — | `eval/sets` | `ProjectReference` 与 `PackageReference` 必须保持为 0；边界由独立测试工程的运行时断言守护 |
