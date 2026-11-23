# PuddingBrowser.AutomationTests
> 源指纹: *.cs=43-00bdf5a0fb71, *.csproj=1-0f138a6bd384 · 条目数: 0 · 最近整理: 2026-11-23

测试工程，引用 PuddingBrowser.Automation；用例全部为进程内单测，不需要真实宿主或外部服务。

`ArrivalScrollTests.cs` 覆盖 11 条用例：
- `ToRefScrollsJustEnoughToBringTheElementIntoView`
- `ToRefStopsWhenTheElementIsVisible`
- `ToRefStopsWhenTheElementNeverMovesOrTheBudgetRunsOut`
- `ScrollingDownContinuesUntilTheLimitIsCovered`
- `ScrollingUpUsesANegativeDeltaAndStopsAtTheTop`
- `ReachingTheBottomStopsInsteadOfReportingSuccessForever`
- （其余 5 条同类用例略）

`ArticleCompactJsonFormatTests.cs` 覆盖 8 条用例：
- `BlocksKeepKindTextDepthHrefAndRows`
- `TruncatedOutputIsStillValidJson_AndSaysSoExplicitly`
- `EachRepresentationPagesByItsOwnBytes_AndStillCoversEverything`
- `SingleBlockOverBudget_IsEmittedWholeAndFlagged`
- `BytesAreMeasuredInUtf8_NotCharacters`
- `MalformedBlocksAreCountedNotSilentlyDropped`
- （其余 2 条同类用例略）

`ArticleCursorFormatTests.cs` 覆盖 7 条用例：
- `EncodeAndParseRoundTrip`
- `PageIdentityCarriesAShortHashNotTheRawPageId`
- `MatchesPageOnlyForTheSamePageAndVersion`
- `MalformedCursorsAreRejectedInsteadOfTreatedAsZero`
- `KindAndFormatMapBothWays`
- `DistinctRepresentationsGetDistinctCursors`
- （其余 1 条同类用例略）

`ArticleMarkdownFormatTests.cs` 覆盖 13 条用例：
- `HeadingsKeepTheirLevel`
- `ListsKeepNestingByDepth`
- `CodeBlocksPreserveWhitespaceAndUseALongerFenceWhenNeeded`
- `TablesBecomeValidMarkdown`
- `LinksKeepTheirDestination`
- `PagingStopsAtBlockBoundaries_AndContinuesWithoutGapsOrRepeats`
- （其余 7 条同类用例略）

`AuthorityEntryAuditTests.cs` 覆盖 1 条用例：
- `EveryAuthorityEntryPointIsListedInTheAuditTable`

`B0Fixture.cs` 覆盖 1 条用例：
- `Advance`

`BrowserAssertionRedactionTests.cs` 覆盖 11 条用例：
- `Non_Sensitive_Value_Is_Bounded_And_Flagged_When_Truncated`
- `Short_Value_Is_Kept_Verbatim`
- `Sensitive_Value_Never_Leaves_Any_Plaintext`
- `Audit_Summary_Carries_Only_Kind_And_Outcome`
- `Missing_Actual_Is_Represented_As_Unknown_Not_As_An_Empty_Value`
- `Control_Characters_In_Actual_Are_Stripped`
- （其余 5 条同类用例略）

`BrowserControlAuthorityTests.cs` 覆盖 16 条用例：
- `Takeover_Blocks_New_Write_Steps`
- `Takeover_Does_Not_Revoke_Read_Authorization`
- `Takeover_Confirm_Makes_Queued_Revalidate_Fail`
- `Resume_Does_Not_Resurrect_The_Pre_Takeover_Lease`
- `Resume_Is_Idempotent_When_Nothing_Was_Interrupted`
- `Takeover_And_Pause_Are_Idempotent`
- （其余 10 条同类用例略）

`BrowserInteractionReceiptTests.cs` 覆盖 25 条用例：
- `Fill_With_ExpectValue_Matching_Is_Verified`
- `Fill_Receipt_Never_Echoes_The_Form_Value`
- `Fill_With_ExpectValue_Mismatch_Is_Failed_Not_Dispatchable`
- `Fill_Without_Expectation_Is_Dispatched_Not_Verified`
- `Expectation_Declared_Without_Observation_Is_Unverified_Not_Failed`
- `Expectation_Declared_But_Value_Not_Echoed_Is_Unverified_Not_Failed`
- （其余 19 条同类用例略）

`BrowserMutationPostconditionTests.cs` 覆盖 12 条用例：
- `Fill_That_Leaves_The_Document_Generation_Unchanged_Is_Accepted`
- `Click_That_Only_Opens_A_Menu_Is_Accepted`
- `Version_Advance_Is_Accepted_For_Any_Expectation`
- `Observed_Navigation_Is_Accepted_Even_When_The_Version_Did_Not_Advance`
- `Declared_Navigation_Without_Observed_Commit_Is_Reported_As_Unknown_Outcome`
- `Missing_Live_Version_Is_A_Real_Failure`
- （其余 6 条同类用例略）

`BrowserNodeIdentityFormatTests.cs` 覆盖 10 条用例：
- `EncodeAndParse_RoundTripWithAndWithoutHostIdentity`
- `MalformedRefs_AreRejectedInsteadOfGuessed`
- `SamePageVersionIndexAndNode_IsValid`
- `SamePositionButDifferentNode_IsReplaced_AndMustBeRejected`
- `DifferentIndexAtTheSameVersion_IsTreatedAsReplaced`
- `VersionOrPageMismatch_KeepsTheExistingStaleSemantics`
- （其余 4 条同类用例略）

`BrowserOperationLedgerTests.cs` 覆盖 15 条用例：
- `First_Admission_Must_Execute`
- `Retransmission_While_Running_Must_Not_Execute_Again`
- `Terminal_Receipt_Is_Replayed_Without_Re_Execution`
- `Same_Operation_Id_With_A_Different_Request_Is_Refused`
- `Expired_Terminal_Receipt_Reports_Receipt_Expired_Instead_Of_A_New_Action`
- `Capacity_Exhaustion_Refuses_New_Writes_And_Never_Evicts_In_Flight_Work`
- （其余 9 条同类用例略）

`BrowserOperationPolicyTests.cs` 覆盖 10 条用例：
- `Policy_Table_Matches_The_Design`
- `Every_Operation_Is_Covered_By_The_Table`
- `Side_Effecting_And_ReadOnly_Partition_Is_Exhaustive`
- `Page_Target_Requirement_Follows_The_Grant_Kind`
- `Contexts_List_Needs_No_Grant_But_Anything_Else_Does`
- `Operation_Must_Be_Registered_To_Be_Classified`
- （其余 4 条同类用例略）

`BrowserPageGrantTests.cs` 覆盖 22 条用例：
- `Page_Without_Any_Grant_Is_Refused_For_Read_And_Write`
- `Non_Target_Page_In_The_Same_Context_Is_Refused`
- `Read_Grant_Does_Not_Authorize_Writes`
- `Write_Grant_Does_Not_Authorize_Tab_Close_Without_Manage`
- `Revoked_Grant_Is_Refused_And_Advances_The_Grant_Epoch`
- `RevokeExecutionScope_Keeps_Read_But_Drops_Write`
- （其余 16 条同类用例略）

`BrowserReadingFormatTests.cs` 覆盖 14 条用例：
- `Outline_Stays_Within_The_Design_Budget_On_A_Large_Tree`
- `Outline_Keeps_Interactive_And_Structural_Lines_Only`
- `Interactive_Keeps_Only_Lines_The_Driver_Marked_With_A_Ref`
- `Full_Keeps_Everything`
- `Full_Ignores_The_Output_Budget`
- `Output_Budget_Truncation_Is_Distinct_From_Incomplete_Scan`
- （其余 8 条同类用例略）

`BrowserReceiptJudgeTests.cs` 覆盖 21 条用例：
- `Same_Document_Fill_Is_Verified_When_The_Value_Reads_Back`
- `Click_That_Only_Opens_A_Menu_Is_Dispatched_Not_Failed`
- `Click_With_A_Satisfied_Expect_Is_Verified_Even_Without_A_Version_Advance`
- `Navigation_Completed_Is_Verified_With_A_Url_Expectation`
- `Failed_Expectation_Is_A_Failure_Even_Though_The_Input_Was_Dispatched`
- `Timeout_After_Dispatch_Is_Dispatched_With_An_Unverified_Assertion`
- （其余 15 条同类用例略）

`BrowserRefIdentityTests.cs` 覆盖 30 条用例：
- `Issued_Ref_Is_Valid_On_Its_Own_Scope`
- `Ref_Is_Rejected_On_Another_Page_Even_At_The_Same_Document_Generation`
- `Ref_Is_Rejected_In_Another_Context_With_The_Same_Page_Id`
- `Ref_Is_Rejected_Across_Frames`
- `Unknown_And_Revoked_Refs_Are_Reported_Separately`
- `Issue_Requires_A_Host_Bound_Node_Key`
- （其余 24 条同类用例略）

`BrowserRetryPolicyTests.cs` 覆盖 8 条用例：
- `Verified_Is_Not_Retryable`
- `Dispatched_Is_Observe_Only`
- `Unknown_Outcome_Is_Never_Retried`
- `Blocked_Requires_An_Explicit_Re_Admission_Instead_Of_An_Automatic_Retry`
- `Failed_But_Not_Started_Is_Safe_To_Retry`
- `Failed_With_Possible_Side_Effects_Is_Not_Retryable`
- （其余 2 条同类用例略）

`BrowserScreenshotCoordinateTests.cs` 覆盖 12 条用例：
- `APointOnALowDetailScreenshotMapsBackToCssPixels`
- `DeviceScaleFactorIsDividedOutNotIgnored`
- `CropOriginAndScrollOffsetAreBothApplied`
- `AnyGenerationChangeMakesTheOldCoordinatesStale`
- `CoordinatesFromAnotherObservationAreRefused`
- `APointOutsideTheImageOrTheViewportIsRefused`
- （其余 6 条同类用例略）

`BrowserScreenshotPipelineTests.cs` 覆盖 5 条用例：
- `APlannedLowDetailViewportCapturePassesTransferValidation`
- `ThePlannedPayloadSplitsAndReassemblesToTheDeclaredSize`
- `CoordinatesFromTheSameObservationMapBackIntoTheViewport`
- `ARegionCaptureKeepsTheOffsetThroughTheWholePipeline`
- `ThePipelineRefusesPayloadsThatBreakTheLowDetailPromiseOrTheInputLimit`

`BrowserScreenshotPlannerTests.cs` 覆盖 9 条用例：
- `AViewportCaptureLowDetailIsPlannedWithTheLowEdgeLimit`
- `HighDetailKeepsDevicePixelsAndObeysTheDeviceScale`
- `LowDetailOnASmallViewportIsNotUpscaled`
- `FullPageCaptureIsExplicitlyUnsupportedRatherThanFaked`
- `ThePreconditionsFromTheDesignAllBlockAPlan`
- `ARegionInsideTheViewportIsPlannedWithItsOwnCropAndOutputSize`
- （其余 3 条同类用例略）

`BrowserScreenshotTransferTests.cs` 覆盖 14 条用例：
- `AConformingLowDetailPayloadIsAccepted`
- `TheSpecLimitsAreTheOnesTheDesignDocumentStates`
- `OverTheEightMebibyteLimitIsRefusedWithAnActionableReason`
- `LowDetailOverTheFortyKiBBaselineIsRefusedWithBudgetExceededWording`
- `HighDetailMayExceedTheLowBaselineButStillObeysTheInputLimit`
- `AnUnsupportedMimeTypeIsRefusedInsteadOfBeingAssumed`
- （其余 8 条同类用例略）

`CallerIdentityGateTests.cs` 覆盖 2 条用例：
- `NoHandWrittenCallerIdComparisonOutsideTheIdentityHelper`
- `TheActorJudgeIsActuallyUsedByTheLeaseAndTheAuthority`

`ComponentBoundaryTests.cs` 覆盖 5 条用例：
- `Boundary_Detector_Flags_Forbidden_Names`
- `Test_Process_Must_Not_Load_Forbidden_Assemblies`
- `Test_Dependency_Closure_Must_Not_Contain_Forbidden_Assemblies`
- `Component_Project_File_Keeps_The_Declared_Reference_List`
- `Test_Project_File_References_Only_The_Component`

`ComponentSurfaceTests.cs` 覆盖 6 条用例：
- `Public_Surface_Matches_The_Frozen_List`
- `Authority_Implements_Both_Narrow_Ports`
- `Read_Port_Exposes_No_Mutating_Members`
- `Control_Port_Exposes_The_Lifecycle_And_Grant_Operations`
- `Component_Assembly_Is_The_One_Under_Test`
- `Test_Method_Counts_Are_Conserved_Per_Class`

`DriverEventHandlingTests.cs` 覆盖 4 条用例：
- `EverySubscribedPageEventIsAlsoUnsubscribedExactlyOnce`
- `TheSubscribedCoreWebViewEventsMatchTheFrozenList`
- `TheDriverDocumentsThatFilePickersCannotBeIntercepted`
- `TheCaptureSafetyDefaultsAreStillInPlace`

`FileUploadPlanFormatTests.cs` 覆盖 4 条用例：
- `SelectorEscapesQuotesAndBackslashes`
- `TheThreeStepsCarryWhatTheNextStepNeeds`
- `FileListIsValidatedBeforeTouchingTheBrowser`
- `NodeIdsAreReadFromResponsesOrRefused`

`GrantExpiryWiringTests.cs` 覆盖 2 条用例：
- `TheParentScopeCheckRunsAfterReclaimingAndNeverSeesTheRawDictionary`
- `ReclaimingReallyRemovesExpiredEntries`

`NativeInputSequenceFormatTests.cs` 覆盖 15 条用例：
- `Click_MovesPressesAndReleasesAtTheCenter`
- `HoverOnlyMoves_NeverPresses`
- `ScrollSendsAWheelEventWithBothDeltas`
- `FillUsesInsertText_AndClearingUsesSelectAllThenDelete`
- `TypeEmitsKeyDownAndKeyUpPerCharacter`
- `TypeUsesInsertTextForNonAsciiCharacters`
- （其余 9 条同类用例略）

`PageEventLogFormatTests.cs` 覆盖 6 条用例：
- `EmptyLogReportsNothing`
- `EntriesKeepTheirOrder`
- `ConsecutiveDuplicatesAreMergedInsteadOfFillingTheLog`
- `CapacityIsEnforced_AndTotalCountStillTellsTheTruth`
- `BlankSummariesAreIgnored`
- `CapacityMustBePositive`

`PageEventPolicyFormatTests.cs` 覆盖 7 条用例：
- `EveryPermissionIsDenied`
- `PopupsAreSuppressed_AndDownloadsCanceled`
- `HostOfKeepsOnlySchemeAndHost`
- `PermissionSummaryNamesTheKindAndNeverLeaksThePath`
- `WindowSummarySaysThePopupWasSuppressed`
- `DownloadSummarySaysNoFileWasWritten`
- （其余 1 条同类用例略）

`PointerInputSequenceTests.cs` 覆盖 5 条用例：
- `PointerClickPressesAndReleasesAtTheSamePoint`
- `PointerCoordinatesAtTheOriginAreCarriedAsZeroNotDropped`
- `PointerMoveDoesNotPressAnything`
- `PointerDragIsPairedAndEndsReleasedAtTheTarget`
- `PointerDragRefusesAZeroStepPlan`

`ReadOnlyDelegationTests.cs` 覆盖 21 条用例：
- `ALeaseCannotBeRiddenByADifferentActorSharingTheSameCallerId`
- `ASubAgentGrantCannotBeIssuedUnderAnExpiredParentGrant`
- `ExpiredGrantsAreReclaimedSoTheDictionaryDoesNotGrowForever`
- `ExpiredGrantsDisappearFromTheActiveProjections_SoTheProjectionAgreesWithAdmission`
- `RevokingAManagementGrantKeepsTheInFlightPageLease`
- `CaptureIsSideEffectFree`
- （其余 15 条同类用例略）

`ScreenshotCaptureContractTests.cs` 覆盖 3 条用例：
- `PlannedCaptureUsesRequestedEncodingActualDprAndDocumentClip`
- `DevicePixelRatioIsIndependentOfVisualViewportScale`
- `HeaderDimensionsComeFromBytesAndMalformedPayloadsAreRejected`

`ScreenshotDomRevisionTests.cs` 覆盖 5 条用例：
- `TheInstallScriptIsIdempotentAndInstallsAnObserver`
- `ACountIsReadFaithfully`
- `MissingOrIllegalCountsAreRefused`
- `TheCounterIsReadFromARuntimeEvaluateResult`
- `AnUnreadableCounterIsRefusedRatherThanAssumedUnchanged`

`ScreenshotLayoutFactsTests.cs` 覆盖 3 条用例：
- `GeometryIsReadFaithfully`
- `MissingOrIllegalFieldsAreRefused`
- `AnyGeometryChangeChangesTheViewportRevision`

`ScreenshotObservationRegistryTests.cs` 覆盖 4 条用例：
- `RegisteredObservationResolvesWithinItsGenerations`
- `AnyGenerationChangeExpiresAndRemovesTheObservation`
- `UnknownOrEmptyIdsNeverResolve`
- `AnEmptyObservationIdIsRefusedAtRegistration`

`ScreenshotPlanFormatTests.cs` 覆盖 12 条用例：
- `ElementScreenshotUsesTheElementBoxAsClip`
- `AnEmptyElementBoxIsRefusedInsteadOfCapturingNothing`
- `ElementBoxIsReadFromTheBoundingBoxOrRefused`
- `DefaultsToPngWithoutQuality`
- `QualitySwitchesToJpegAndCarriesTheValue`
- `QualityOutsideRangeIsRejectedInsteadOfClamped`
- （其余 6 条同类用例略）

`ScreenshotQualityLadderTests.cs` 覆盖 7 条用例：
- `StepsDescendFromTheRequestedStartOnly`
- `StepsKeepTheStartAndNeverExceedIt`
- `StepsRejectOutOfRangeQuality`
- `FirstStepThatFitsWinsAndNoFurtherCaptureHappens`
- `WithoutABudgetOnlyTheFirstStepIsCaptured`
- `WhenEveryStepExceedsTheBudgetTheSmallestCaptureIsReturnedAndFlagged`
- （其余 1 条同类用例略）

`ScriptDialogPolicyFormatTests.cs` 覆盖 6 条用例：
- `AlertIsAcceptedBecauseItHasNoOtherAnswer`
- `ConfirmAndPromptAreDismissed_NeverAssumedAffirmative`
- `PromptNeverGetsTextBack`
- `SummaryIsBoundedAndSingleLine`
- `SummaryWithoutMessageOmitsTheSnippet`
- `NormalizeCollapsesWhitespaceAndTrims`

`SerpBudgetFixtureTests.cs` 覆盖 6 条用例：
- `ChineseSerpFitsTheBudgetOrPagesExplicitly`
- `TheChineseSerpFitsInOneExtractAtTheDocumentedPageBudget`
- `EnglishSerpFitsTheG5BudgetInOneExtract`
- `ATightBudgetPagesTheSameContentInsteadOfDroppingIt`
- `TheMarkdownRepresentationOfTheSameChineseSerpAlsoFitsTheBudget`
- `TheBudgetNumberMatchesTheDesignDocument`

`SubAgentDelegationLeaseTests.cs` 覆盖 18 条用例：
- `ParentAndChildCannotHoldTheSamePageWriteLease`
- `TwoChildrenCannotWriteTheSamePageConcurrently`
- `WritesArePerPageAndIdempotentForTheSameHolder`
- `ReadObservationsRunInParallel`
- `EndingOrTimingOutADelegationReturnsTheLeaseToTheParent`
- `EndingAnUnknownDelegationChangesNothing`
- （其余 12 条同类用例略）

`SubAgentDelegationTests.cs` 覆盖 10 条用例：
- `AWellFormedReadOnlyDelegationIsAccepted`
- `ADelegationWithoutGrantGoalOrDeadlineIsRefused`
- `AChildScopeCannotExceedItsParent`
- `InjectedToolsMustCoverTheTask`
- `WriteCapableDelegationsMustHaveNavigateInteractAndScreenshot`
- `TheParentConclusionBudgetIsBoundedByDesign`
- （其余 4 条同类用例略）
