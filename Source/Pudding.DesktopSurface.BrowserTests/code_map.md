# Pudding.DesktopSurface.BrowserTests
> 源指纹: *.cs=10-9b5783e0637a, *.csproj=1-0b265757e970 · 条目数: 0 · 最近整理: 2026-11-23

测试工程，引用 Pudding.DesktopSurface.Browser；用例全部为进程内单测，不需要真实宿主或外部服务。

`BrowserTargetObservationTests.cs` 覆盖 4 条用例：
- `InitialEmptySnapshotDoesNotRevokeImplicitControl`
- `RepeatedSnapshotDoesNotRebindAnActiveOperationToTheOldPage`
- `ClosingObservedPageInvalidatesItsPermitWithoutSuspendingTheNextPage`
- `EmptySnapshotCannotUndoExplicitRevocation`

`BrowserTargetSwitchRevokesBindingsTests.cs` 覆盖 1 条用例：
- `SwitchingAwayFromTheSelectedPageRevokesItsBindings`

`BrowserWorkspaceTargetBridgeTests.cs` 覆盖 18 条用例：
- `CreatedPage_IsKnownToBothRegistries`
- `UnregisteredContext_IsUntrustedByDefault`
- `PageBeforeItsContext_FailsLoud`
- `ActivatedPage_IsReportedAsActiveInBothRegistries`
- `VersionAdvance_MovesForwardAndNeverBackwards`
- `VersionAdvance_ForUnknownPage_DoesNotInventATarget`
- （其余 12 条同类用例略）

`ContextsMappingTests.cs` 覆盖 88 条用例：
- `ContextsCarryLiveVersionsTrustAndActivePage`
- `PagesWithoutALiveVersionAreNotListed`
- `ContextsThatDisappearBetweenListAndGetAreSkippedNotInvented`
- `RuntimeThatIsNotReadyIsReportedAsUiUnavailable`
- `UntrustedContextsAreReportedAsUntrustedByDefault`
- `CloseContextAsync`
- （其余 82 条同类用例略）

`DesktopSurfaceCompositionTests.cs` 覆盖 5 条用例：
- `ShellMembers_ReachTheShellSurface`
- `BrowserMembers_DoNotReachTheShellSurface`
- `Constructor_RejectsMissingCollaborators`
- `Composition_IsTheSingleSurfaceSeamDesktopServiceConsumes`
- `CloseContextAsync`

`InteractMappingTests.cs` 覆盖 47 条用例：
- `AmbiguousLocatorReturnsActionableFailureWithoutLeakingPageText`
- `Interact_ReResolvesTheElementAfterTheAction`
- `Interact_MarksTheObservationAsPreAction_WhenTheElementDisappearsAfterTheAction`
- `Interact_SucceedsAndMarksPreAction_WhenReResolveThrows`
- `Interact_FallsBackToPreActionObservation_WhenTheAfterHandleCannotBeMapped`
- `ClickAdvancesTheVersionAndReportsTheElementItActedOn`
- （其余 41 条同类用例略）

`ScreenshotPresentationTests.cs` 覆盖 32 条用例：
- `HiddenSurfacePresentsThePageThenRetriesExactlyOnce`
- `WithoutAPresenterTheSurfaceDoesNotRetry`
- `PresenterThatThrowsKeepsTheOriginalFailureAndDoesNotRetry`
- `GoBackAsync`
- `GoForwardAsync`
- `ReloadAsync`
- （其余 26 条同类用例略）

`SnapshotMappingTests.cs` 覆盖 26 条用例：
- `SnapshotPassesTheBudgetToTheRuntimeAndKeepsThePageVersion`
- `OverBudgetContentIsTruncatedAndFlaggedEvenIfTheRuntimeIgnoresTheBudget`
- `PinnedVersionMismatchIsRejectedWithoutSnapshotting`
- `RequestWithoutAnyContentKindIsRejectedUpFront`
- `CloseContextAsync`
- `ClosePageAsync`
- （其余 20 条同类用例略）

`WaitForMappingTests.cs` 覆盖 27 条用例：
- `ContentGrowsCondition_CarriesTheBaseline_AndABadBaselineIsRefused`
- `SatisfiedWaitReturnsSuccessAndMapsTheConditionByKind`
- `TimeoutIsAResultNotAFailureAndStillCarriesThePageState`
- `PinnedVersionMismatchIsRejectedWithoutWaiting`
- `UnknownTargetAndNotReadyRuntimeUseTheirOwnErrorCodes`
- `CloseContextAsync`
- （其余 21 条同类用例略）

`WebViewMappingTests.cs` 覆盖 33 条用例：
- `ProductManagementUsesWorkspaceOwnerInsteadOfRawDriver`
- `FailedWorkspaceCreationIsNotRetriedThroughTheRawDriver`
- `CloseContextAsync`
- `ActivatePageAsync`
- `ClosePageAsync`
- `SuccessfulNavigationReportsCompletedAndTheNewVersion`
- （其余 27 条同类用例略）
