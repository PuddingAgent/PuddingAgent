# PuddingContextHandoffTests
> 源指纹: *.cs=2-8794ec522b01, *.csproj=1-a92b23a31569 · 条目数: 0 · 最近整理: 2026-11-23

测试工程，引用 PuddingContextHandoff；用例全部为进程内单测，不需要真实宿主或外部服务。

`ComponentBoundaryTests.cs` 覆盖 1 条用例：
- `ComponentAndTests_HaveOnlyAllowedDependencies`

`HandoffStoreTests.cs` 覆盖 9 条用例：
- `Cleanup`
- `RestartAndRevision_KeepBothExactVersions`
- `ScopeIsolation_DoesNotAllowCrossSessionWorkspaceOrAgentReads`
- `RejectedOrCancelledWrite_PreservesPreviousCompleteNote`
- `ByteBudget_AllowsExactUtf8LimitWithoutTruncation`
- `InvalidIdsAndCorruptPointer_AreRejected`
- （其余 3 条同类用例略）
