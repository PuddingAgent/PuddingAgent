# PuddingComputer.BenchmarkRunner
> 源指纹: Source/PuddingComputer.BenchmarkRunner/**=3-dd83d84ced8e · 条目数: 2 · 最近整理: 2026-11-22

只读端到端跑批工程（net10.0，引 PuddingComputer.Benchmark 与 PuddingComputer.UIAutomation）。加载清单、抓 UIA 快照、求 oracle 判定、按应用分组写 markdown 报告；不点击、不输入。

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `Program.cs` | 加载清单并抓快照的只读跑批入口 | `EnsureAppWindow`、`FindWindowByHints` | `Source/PuddingComputer.Benchmark/benchmarks/native-apps.json` | 收尾只关闭本程序启动的进程，原本就在运行的应用不动 |
| `OracleEvaluator.cs` | 把声明式 oracle 对快照求值成三态判定 | `OracleEvaluator` | `Source/PuddingComputer.Benchmark/OutcomeOracle.cs` | 读不到或未实现的判定返回 Unknown，不得猜成 Pass |
