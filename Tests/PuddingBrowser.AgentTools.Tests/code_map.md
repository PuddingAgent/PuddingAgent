# Tests/PuddingBrowser.AgentTools.Tests
> 源指纹: *.cs=5-486fe2986fd1, *.csproj=1-b917ba21573b · 条目数: 0 · 最近整理: 2026-11-23

测试工程，引用 PuddingBrowser.AgentTools；用例全部为进程内单测，不需要真实宿主或外部服务。

`BrowserAgentToolsTests.cs` 覆盖 108 条用例：
- `Snapshot_ContinuationConsumesEachLineExactlyOnce`
- `Interact_RefRequiresObservedVersion_AndNeverRebinds`
- `Tabs_NewWithImplicitContext_NeverReusesAnOperationId`
- `Interact_WithoutExplicitContext_IssuesOneOperationIdPerCommand`
- `SingleCommandTools_WithImplicitContext_IssueOneOperationIdPerCommand`
- `Screenshot_WithoutExplicitContext_IssuesOneOperationIdPerCommand`
- （其余 102 条同类用例略）

`BrowserCallerIdentityAdmissionTests.cs` 覆盖 7 条用例：
- `ABrowserToolWithoutATrustedIdentityIsRefusedAndDoesNotRun`
- `ASubAgentWithoutItsParentRunIsRefused`
- `TheProjectedIdentityIsVisibleToConsumersDuringTheCallAndClearedAfterwards`
- `TheIdentityScopeIsRestoredEvenWhenTheToolThrows`
- `ConcurrentCallsDoNotShareIdentity`
- `TheAccessorReadsTheCallScopeWithoutCachingIt`
- （其余 1 条同类用例略）

`BrowserCallerIdentitySourceSubAgentTests.cs` 覆盖 4 条用例：
- `ASubAgentIsProjectedWithItsParentTask`
- `ASubAgentWithoutAParentIsRefusedWithAConcreteReason`
- `AConversationTurnIsProjectedAsAnAgentWithoutAParent`
- `MissingExecutionIdentityIsRefusedRatherThanInferred`

`BrowserDelegationTasksTests.cs` 覆盖 8 条用例：
- `AReadOnlyBrowserTaskRequiresSnapshotAndInjectsTheRealToolIds`
- `AWriteCapableTaskAlsoRequiresNavigateAndInteract`
- `ExtraRequiredToolsAreHonoredAndDeduplicated`
- `TryCreateRefusesAScopeThatExceedsTheParent`
- `TryCreateRefusesAnUnusableEnvelope`
- `NullScopeIsRefused`
- （其余 2 条同类用例略）

`BrowserToolOriginTests.cs` 覆盖 6 条用例：
- `BrowserTool_PushesAgentOriginDuringRuntimeCall`
- `BrowserTool_RestoresPreviousOriginAfterSuccess`
- `BrowserTool_RestoresPreviousOriginAfterFailure`
- `ConcurrentBrowserToolCalls_DoNotLeakOriginAcrossAgents`
- `NonBrowserTool_IsUnaffectedByExecutionScopeHook`
- `CloseContextAsync`
