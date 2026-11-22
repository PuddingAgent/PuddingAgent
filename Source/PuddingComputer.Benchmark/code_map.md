# PuddingComputer.Benchmark
> 源指纹: Source/PuddingComputer.Benchmark/**=8-8b8a2b578fa5 · 条目数: 7 · 最近整理: 2026-11-22

独立 BCL-only 的 Computer Use 基准任务与判定规则组件（net10.0，仅引 Pudding.Contracts）。
它只描述「什么算做完」，不碰任何观察或执行能力，因此判定逻辑可以在没有窗口的进程里单测。

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `BenchmarkTask.cs` | 单条基准任务的不可变定义与字段自检 | `BenchmarkTask` | `OutcomeOracle.cs` | Setup 必须可复现，不可复现的初始状态会让基准结果不可比 |
| `OutcomeOracle.cs` | 结果判定方式的判别式声明与描述文本 | `OutcomeOracle`、`UiaPropertyOracle`、`VisualRegionOracle` | `Source/Pudding.Contracts/Computer/ElementSelector.cs` | 像素变化不能当成功证据，每种判定必须给出任务专属证据 |
| `OracleEvaluation.cs` | 三态判定与随判定一起留痕的证据 | `OracleEvaluation`、`OracleVerdict` | `OutcomeOracle.cs` | 无证据的判定事后无法复核，等于没有判定 |
| `BenchmarkPolicy.cs` | 把判定与风险折算成下一步动作 | `BenchmarkPolicy`、`AttemptDecision` | `RiskClass.cs` | 只有 Pass 允许声明成功，Unknown 重试多少次都不会变成成功 |
| `RiskClass.cs` | 动作风险等级及其重试与澄清判据 | `RiskClass`、`RiskClassExtensions` | `BenchmarkPolicy.cs` | 风险由任务静态声明，不得在运行时降级 |
| `BenchmarkCatalog.cs` | 任务清单 JSON 的读写与整份校验 | `BenchmarkCatalog` | `BenchmarkTask.cs` | Id 是报告对齐的唯一键，重复会让结果无法归因 |
| `benchmarks/native-apps.json` | 原生应用基准任务的清单数据 | `—` | `BenchmarkCatalog.cs` | 清单是数据不是代码，改任务不必改源码 |
