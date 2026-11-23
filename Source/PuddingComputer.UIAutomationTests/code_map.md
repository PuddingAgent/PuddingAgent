# PuddingComputer.UIAutomationTests
> 源指纹: *.cs=8-2a797602e04a, *.csproj=1-9f3a27cba825 · 条目数: 0 · 最近整理: 2026-11-23

测试工程，引用 PuddingComputer.UIAutomation；用例全部为进程内单测，不需要真实宿主或外部服务。

`BoundaryTests.cs` 覆盖 7 条用例：
- `RealAssembly_ReferencesOnlyAllowedLayers`
- `FindViolations_DetectsForbiddenLayer`
- `FindViolations_DetectsUnknownAssembly`
- `FindViolations_AcceptsAllowedCombination`
- `ForbiddenTakesPrecedenceOverAllowed`
- `FindViolations_EmptyInput_IsClean`
- （其余 1 条同类用例略）

`SelectorResolutionTests.cs` 覆盖 5 条用例：
- `Decide_SingleHitOnCompleteTraversal_IsOk`
- `Decide_MultipleHits_IsAmbiguousRegardlessOfTruncation`
- `Decide_NoHitOnCompleteTraversal_IsNotFound`
- `Decide_NoHitOnTruncatedTraversal_IsPartialNotNotFound`
- `Decide_SingleHitOnTruncatedTraversal_IsPartialNotOk`

`UiaAvailabilityTests.cs` 覆盖 14 条用例：
- `PlentyOfActionableNodes_IsSupported`
- `TooFewActionableNodes_IsSparse`
- `ExactlyThresholdActionableNodes_IsSupported`
- `OneBelowThreshold_IsSparse`
- `Timeout_DominatesOtherSignals`
- `Error_WithoutTimeout_IsUnavailable`
- （其余 8 条同类用例略）

`UiaNodeMatchingTests.cs` 覆盖 10 条用例：
- `Matches_ByAutomationId`
- `Matches_WithoutAutomationId_ByTypeAndClass`
- `Matches_MultipleFieldsAreAnd`
- `Matches_EmptySelector_NeverMatches`
- `Matches_CaseSensitivityDiffersByField`
- `FindAll_ReturnsEveryMatch`
- （其余 4 条同类用例略）

`UiaPreprobeTests.cs` 覆盖 3 条用例：
- `Probe_ZeroHandle_ReturnsZeroWithoutTouchingUiAutomation`
- `Wake_InvokesProbeExactlyOnce`
- `Wake_SwallowsProbeFailure`

`UiaStabilizationTests.cs` 覆盖 6 条用例：
- `ShouldKeepWarming_StopsAsSoonAsChildrenAppear`
- `ShouldKeepWarming_WaitsWhileStillEmpty`
- `ShouldKeepWarming_StopsWhenWindowExhausted`
- `ShouldKeepWarming_ZeroWindowNeverWaits`
- `EffectiveWarmup_NeverExceedsCaptureTimeout`
- `EffectiveWarmup_ZeroConfiguredMeansNoWarmup`

`UiaValueWriterTests.cs` 覆盖 10 条用例：
- `MatchesFields_EmptySelector_MatchesNothing`
- `MatchesFields_NameIsCaseSensitive`
- `MatchesFields_AutomationIdIsCaseSensitive`
- `MatchesFields_ControlTypeAndClassNameIgnoreCase`
- `MatchesFields_AllGivenFieldsMustMatch`
- `MatchesFields_NullFieldsOnSelectorAreIgnored`
- （其余 4 条同类用例略）
