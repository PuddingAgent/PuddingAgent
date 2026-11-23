# PuddingComputer.BenchmarkRunnerTests
> 源指纹: *.cs=2-a9ea971005de, *.csproj=1-696d62284e5e · 条目数: 0 · 最近整理: 2026-11-23

测试工程，引用 PuddingComputer.BenchmarkRunner；用例全部为进程内单测，不需要真实宿主或外部服务。

`OracleEvaluatorTests.cs` 覆盖 13 条用例：
- `NameEndsWith_Matches_IsPass`
- `NameEndsWith_NotMatching_IsFail`
- `NameEquals_IsExact`
- `Operator_IsCaseInsensitive`
- `ElementNotFound_IsUnknown_NotFail`
- `UnsupportedProperty_IsUnknown`
- （其余 7 条同类用例略）
