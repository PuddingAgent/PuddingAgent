# PuddingConversationReadModelTests
> 源指纹: *.cs=13-d072a0695ea7, *.csproj=1-019bef9d824e · 条目数: 0 · 最近整理: 2026-11-23

测试工程，引用 PuddingConversationReadModel；用例全部为进程内单测，不需要真实宿主或外部服务。

`ComponentBoundaryTests.cs` 覆盖 8 条用例：
- `Component_csproj_has_no_project_or_package_references`
- `Test_project_references_only_the_component`
- `Component_sources_reference_only_their_own_pudding_namespace`
- `Component_sources_are_pure_and_io_free`
- `Component_assembly_references_are_bcl_only`
- `Component_internals_are_visible_only_to_its_test_assembly`
- （其余 2 条同类用例略）

`ConversationReadModelLimitsTests.cs` 覆盖 9 条用例：
- `Defaults_MatchFrozenBudgetTable`
- `NormalizeOpenMessageLimit_ClampsIntoFrozenRange`
- `NormalizeOpenMessageLimit_UsesConfiguredDefaultsAndMaximum`
- `NormalizeDetailItemLimit_ClampsIntoFrozenRange`
- `NormalizeDetailItemLimit_UsesConfiguredDefaultsAndMaximum`
- `Validate_DefaultLimits_Passes`
- （其余 3 条同类用例略）

`ConversationReadModelReducerTests.cs` 覆盖 44 条用例：
- `CreateEmpty_InitializesReaderSafeDefaults`
- `CreateEmpty_PreservesRequestedInitializationState`
- `CreateEmpty_RejectsNullConversationId`
- `CreateEmpty_RejectsEmptyConversationId`
- `Apply_RejectsNullState`
- `Apply_RejectsNullBatch`
- （其余 38 条同类用例略）

`CursorContractTests.cs` 覆盖 13 条用例：
- `Validate_LegalCursorTriple_Passes`
- `Validate_WithoutLastApplied_Passes`
- `Validate_SnapshotAndAppliedAtHead_Passes`
- `Validate_EmptyHeadConversationId_Fails`
- `Validate_EmptySnapshotConversationId_Fails`
- `Validate_EmptyLastAppliedConversationId_Fails`
- （其余 7 条同类用例略）

`HistoryPageBudgeterTests.cs` 覆盖 15 条用例：
- `Bound_DefaultLimit_ReturnsAtMostTwentyNewestFirst`
- `Bound_RequestedLimit_ClampsToFrozenMaximum`
- `Bound_ConfiguredLimits_DriveDefaultAndMaximum`
- `Bound_TruncatesPreviewToEightKiB_OnUtf8Boundary`
- `Bound_DoesNotTruncateShortPreview`
- `Bound_ContentExactlyAtPreviewLimit_IsNotTruncated`
- （其余 9 条同类用例略）

`PageCursorContractTests.cs` 覆盖 23 条用例：
- `ValidateHistory_LegalCursor_Passes`
- `ValidateHistory_NullCursor_Throws`
- `ValidateHistory_EmptyConversationId_Fails`
- `ValidateHistory_ConversationMismatch_Fails`
- `ValidateHistory_EmptyMessageIdentity_Fails`
- `ValidateHistory_SortVersionMismatch_Fails`
- （其余 17 条同类用例略）

`ProcessCompletenessTests.cs` 覆盖 10 条用例：
- `ActiveAndTerminalReadyTurn_WithTurnHeader_ReportsActiveThenComplete`
- `ReadyTurnWithoutTurnHeader_TerminalStaysPartialWithHeaderMissing`
- `TurnStartedAfterContentFirstCreation_UpgradesReasonToTurnActive`
- `NonReadyInitialization_StaysPartialWithBaselineIncomplete`
- `FailedAndCancelledTurns_StillDescribeCompleteCoverageWithoutAssistantBody`
- `TerminalTurnWithOmittedSubAgentRuns_DowngradesCompleteToSubAgentSummaryTruncated`
- （其余 4 条同类用例略）

`ProcessPageBudgeterTests.cs` 覆盖 14 条用例：
- `Bound_DefaultLimit_ReturnsAtMostOneHundredItemsAscending`
- `Bound_RequestedLimit_ClampsToFrozenMaximum`
- `Bound_EmptyCandidateWindow_ReportsNoScanAndNoCursor`
- `Bound_TruncatesItemPreviewToSixteenKiB_OnUtf8Boundary`
- `Bound_StopsAtPageByteBudget`
- `Bound_FirstCandidateRejectedByByteBudget_ReportsRefetchableBoundary`
- （其余 8 条同类用例略）

`SseCatchUpMeterTests.cs` 覆盖 14 条用例：
- `DefaultBudget_ConsumesWithinLimits`
- `EventLimit_RejectsEventBeyondBudgetWithoutConsumingIt`
- `EventLimit_UsesConfiguredBudget`
- `ByteLimit_RejectsOversizedEventWithoutConsumingIt`
- `ByteLimit_DefaultBudgetRejectsSingleOversizedEvent`
- `TimeBudget_RejectsWhenElapsedExceedsBudget`
- （其余 8 条同类用例略）

`SubAgentReadModelTests.cs` 覆盖 7 条用例：
- `Apply_ChildRunTurnScopedEvents_AreIgnoredAndDoNotPolluteParentTurnSummary`
- `Apply_ChildRunTurnScopedEventAfterParentTerminal_IsLateAfterTerminal`
- `Apply_SubAgentRuns_AreBoundedToSummaryLimit_KeepingActiveAndEvictingOldestTerminal`
- `Apply_SubAgentRuns_WhenAllActive_OmitsNewRunWithoutEviction`
- `Apply_SubAgentRuns_EvictedRunReturning_DoesNotOverflowBoundedSummary`
- `Apply_SubAgentLifecycle_TerminalStateCannotRegress`
- （其余 1 条同类用例略）

`TextBudgetTests.cs` 覆盖 15 条用例：
- `Utf8Length_CountsEncodedBytes`
- `TakeUtf8Prefix_ReturnsWholeTextWhenWithinBudget`
- `TakeUtf8Prefix_TruncatesAsciiExactly`
- `TakeUtf8Prefix_DoesNotSplitChineseCodePoints`
- `TakeUtf8Prefix_DoesNotSplitEmojiSurrogatePairs`
- `TakeUtf8Prefix_ExactBoundaryKeepsWholeCodePoint`
- （其余 9 条同类用例略）
