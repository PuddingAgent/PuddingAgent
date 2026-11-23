# PuddingComputer.BenchmarkTests
> 源指纹: *.cs=3-efff6fecb485, *.csproj=1-86d5b34bdbf6 · 条目数: 0 · 最近整理: 2026-11-23

测试工程，引用 PuddingComputer.Benchmark；用例全部为进程内单测，不需要真实宿主或外部服务。

`BenchmarkCatalogTests.cs` 覆盖 9 条用例：
- `ShippedCatalog_LoadsAndValidates`
- `ShippedCatalog_OracleTypesDeserializeConcretely`
- `ShippedCatalog_HasSelectorWithoutAutomationId`
- `SaveThenLoad_RoundTrips`
- `Save_WritesRiskAsReadableString`
- `Load_OnMalformedJson_Throws`
- （其余 3 条同类用例略）

`BenchmarkPolicyTests.cs` 覆盖 11 条用例：
- `Pass_YieldsSucceeded`
- `Pass_OnLastAllowedAttempt_StillSucceeds`
- `Fail_WithRemainingAttempts_Retries`
- `Fail_WithAttemptsExhausted_Blocks`
- `Unknown_OnNonIdempotentAction_Blocks`
- `Unknown_OnDestructiveAction_Blocks`
- （其余 5 条同类用例略）
