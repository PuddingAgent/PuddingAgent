# PuddingLlmUsageStorageTests
> 源指纹: *.cs=1-a7ec3a10554f, *.csproj=1-6c09a2128a19 · 条目数: 0 · 最近整理: 2026-11-23

测试工程，引用 PuddingLlmUsage.Storage、PuddingLlmUsage；用例全部为进程内单测，不需要真实宿主或外部服务。

`SqliteLlmUsageStoreTests.cs` 覆盖 14 条用例：
- `Initialize`
- `Cleanup`
- `RoundTrip_KeepsDecimalPrecision`
- `MissingAmounts_StayNull_AfterRoundTrip`
- `FailedAttempt_SurfacesAsFailureObservation`
- `EmptyObservationList_IsRecordedAsFailure_NotSuccess`
- （其余 8 条同类用例略）
