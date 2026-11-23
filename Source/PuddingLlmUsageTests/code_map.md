# PuddingLlmUsageTests
> 源指纹: *.cs=2-49316a5a6ab5, *.csproj=1-a9f82126de01 · 条目数: 0 · 最近整理: 2026-11-23

测试工程，引用 PuddingLlmUsage；用例全部为进程内单测，不需要真实宿主或外部服务。

`LlmBalanceSamplerTests.cs` 覆盖 18 条用例：
- `InitializeAsync`
- `CompleteAttemptAsync`
- `Success_WritesStartedThenCompleted_AndReportsSuccess`
- `ProbeThrows_IsRecordedAsFailure_AndDoesNotEscape`
- `StoreBeginFails_SkipsExternalQueryEntirely`
- `StoreCompleteFails_ReportsStoreFailure_ButKeepsQueryResult`
- （其余 12 条同类用例略）

`LlmUsageAnalyzerTests.cs` 覆盖 17 条用例：
- `DownwardBalance_IsConsumption_WithExactAmount`
- `UpwardBalance_IsRecharge_AndConsumptionStaysNull`
- `FailedEndpoint_IsUnavailable_NotZero`
- `IntervalOverGapThreshold_IsGap_EvenWhenBalanceFell`
- `IntervalAtExactThreshold_IsStillDecidable`
- `EqualBalance_IsNoChange_AndDoesNotClaimZeroSpending`
- （其余 11 条同类用例略）
