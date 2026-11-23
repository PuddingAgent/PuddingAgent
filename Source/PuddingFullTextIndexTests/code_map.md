# PuddingFullTextIndexTests
> 源指纹: *.cs=38-271f052c91ce, *.csproj=1-6966c2641d38 · 条目数: 0 · 最近整理: 2026-11-23

测试工程，引用 PuddingFullTextIndex；用例全部为进程内单测，不需要真实宿主或外部服务。

`BuildIndexWalkTests.cs` 覆盖 8 条用例：
- `Initialize`
- `Cleanup`
- `SingleWalk_IndexesEveryNestedFileExactlyOnce`
- `NoiseDirectories_AreNotIndexed`
- `CallerPatterns_AreStillHonoured`
- `EmptyAndOversizedFiles_AreSkipped`
- （其余 2 条同类用例略）

`CheckpointAdvancePolicyTests.cs` 覆盖 9 条用例：
- `Decide_StateMatrix_AllTwentyFourCells_AreEvaluatedAndFailClosed`
- `ThreeSucceededOneFailed_BlocksAdvance_AndAllFourFilesReplayNextRound`
- `RetainedOld_SinglePendingRetryPath_BlocksAdvance`
- `CancelledBatch_BlocksAdvance`
- `FullyAppliedBatch_IsTheOnlyAdvancingCase`
- `BlockReasons_AreDistinguishableAcrossFailureCancellationAndBudgetRejection`
- （其余 3 条同类用例略）

`CheckpointCrashSafetyTests.cs` 覆盖 6 条用例：
- `CrashDuringTemporaryWrite_KeepsCommittedCheckpointUnchanged_AndResidueIsNeverCommitted`
- `ResidueAlongsideCommitted_OnlyTheCommittedFileIsRead`
- `AtomicReplacement_NewCheckpointTakesEffect_WithMonotonicGeneration`
- `CrashAfterCommitBeforeCheckpointWrite_ReplaysTheSameBatch_WithoutLosingAnyFile`
- `PreCommitFailures_BlockAdvance_SoTheNextRoundRescansEveryCandidate`
- `CorruptOrUnreadableCheckpoint_IsFailClosed_AndNeverMeansNoWorkNeeded`

`ComponentBoundaryTests.cs` 覆盖 13 条用例：
- `Boundary_Detector_Flags_Forbidden_Names`
- `A1_Component_Project_References_Only_The_Path_Filtering_Leaf`
- `A2_Component_Package_References_Match_The_Frozen_Set`
- `A3_Component_Project_Has_No_Forbidden_References`
- `A4_Test_Project_References_Only_The_Component`
- `A5_Test_Process_Must_Not_Load_Forbidden_Assemblies`
- （其余 7 条同类用例略）

`CrossPathExclusionFixTests.cs` 覆盖 9 条用例：
- `Initialize`
- `Cleanup`
- `F1_Drive_Root_Yields_The_Same_ScopeKey_And_The_Same_Lease_File_On_Both_Paths`
- `F2_Ancestor_Or_Same_Matrix`
- `F3_Ancestry_Judgement_Has_Exactly_One_Definition_In_The_Component`
- `F4_Default_Owners_Make_Supply_Hold_Block_Maintenance_With_Busy`
- （其余 3 条同类用例略）

`CrossPathMutexTests.cs` 覆盖 11 条用例：
- `Initialize`
- `Cleanup`
- `M1_BothPaths_Derive_The_Same_ScopeKey_And_The_Same_LeaseFile`
- `M2_Boundary_Matrix_Each_Input_Is_Judged_On_Its_Own`
- `M2b_Normalization_Folds_Equivalent_Spellings_To_The_Same_Key`
- `M3_Positive_Control_Two_Distinct_CorpusRoots_Yield_Distinct_Keys_And_Lease_Files`
- （其余 5 条同类用例略）

`ExtensionFilterRecallTests.cs` 覆盖 8 条用例：
- `Initialize`
- `Cleanup`
- `ExtensionFilter_StillReturnsTheOnlyMatchingFile_WhenNoiseFillsTheTopK`
- `UnfilteredSearch_OnTheSameCorpus_ReturnsHits`
- `SubDirectoryFilter_AlsoSurvivesNoiseSaturation`
- `ScanBudgetExhaustion_IsIncomplete_NotSuccessZeroHits`
- （其余 2 条同类用例略）

`FileSystemSupplyInventoryTests.cs` 覆盖 6 条用例：
- `Inventory_Mirrors_The_Engine_File_Filter`
- `Inventory_Respects_MaxFileSize_And_Excluded_File_Names`
- `Inventory_Skips_Noise_Directories_And_Counts_Nested_Files`
- `Inventory_Keys_Extensions_Case_Insensitively`
- `Inventory_Throws_For_A_Missing_Directory`
- `Inventory_Is_Read_Only_And_Creates_Nothing`

`FullTextChangeCoalescerMatrixTests.cs` 覆盖 8 条用例：
- `FinalStates_AllEightObservationValues_MapToTheContractedAction`
- `SourcesMatrix_EightStatesTimesSevenNonZeroCombinations_AreAllBitwise`
- `SourcesMerge_AllSevenCombinations_MergeBitwiseOrWithoutExtraBits`
- `Determinism_ShufflingIntermediatesOnly_YieldsTheIdenticalSequence`
- `Determinism_ArbitraryShuffleKeepingLastEventPerPath_YieldsTheSamePerPathMap`
- `Purity_DoesNotMutateTheInputList_AndIsIdempotent`
- （其余 2 条同类用例略）

`FullTextIndexFreshnessProbeTests.cs` 覆盖 5 条用例：
- `Initialize`
- `Cleanup`
- `ProbeFreshness_UnbuiltScope_ReportsMissing`
- `ProbeFreshness_AfterBuild_ReportsAvailableStampAndDirectoryMtime`
- `ProbeFreshness_StampMissing_ReportsUnreadableAndNeverFabricatesTime`

`FullTextIndexPathsTests.cs` 覆盖 4 条用例：
- `A3_NormalizeCorpusRoot_Trims_Trailing_Separators_And_Uppercases`
- `A3_NormalizeCorpusRoot_Does_Not_Preserve_The_Drive_Root`
- `A1_ResolveIndexDirectory_Equals_The_Engine_And_The_Frozen_Hashes`
- `ResolveIndexDirectory_Rejects_Blank_Arguments`

`FullTextIndexTests.cs` 覆盖 60 条用例：
- `Tokenizes_Chinese_Text`
- `Tokenizes_Mixed_Chinese_And_English`
- `Tokenizes_Empty_Text`
- `Tokenizes_English_Only`
- `PlainTextExtensions_Contains_Common_Code_Extensions`
- `IsIndexableExtension_Returns_True_For_Whitelisted`
- （其余 54 条同类用例略）

`FullTextMaintenanceScopePlannerTests.cs` 覆盖 4 条用例：
- `Create_AbsoluteExistingScope_IsAcceptedWithNormalizedKeyAndBudget`
- `Create_RelativeScopeWithAbsoluteWorkspaceRoot_IsResolvedAgainstWorkspaceRoot`
- `Create_RelativeScopeWithoutWorkspaceRoot_IsRejectedWithReason`
- `Create_MissingDirectory_IsRejectedAndYieldsNoScope`

`FullTextPolicyFingerprintTests.cs` 覆盖 11 条用例：
- `S1b_Golden_1_Null_Falls_Back_To_The_Default_Literal`
- `S1b_Golden_2_The_Default_Literal_Equals_The_Null_Result`
- `S1b_Golden_3_Single_Cs_Pattern`
- `S1b_Golden_4_Single_Md_Pattern`
- `S1b_Golden_5_Semicolon_List`
- `S1b_Golden_6_Single_Ts_Pattern`
- （其余 5 条同类用例略）

`FullTextSearchEngineIndexBuilderTests.cs` 覆盖 2 条用例：
- `Adapter_Forwards_The_Scope_And_Maps_Engine_Metrics`
- `Adapter_Propagates_Failure_And_The_Engine_Error_Text`

`LastIndexedStampRoundTripTests.cs` 覆盖 3 条用例：
- `Initialize`
- `Cleanup`
- `S1b_LastIndexed_Shape_And_RoundTrip_Feed_The_Incremental_Decision`

`LeaseAndVisibilityTests.cs` 覆盖 15 条用例：
- `Initialize`
- `Cleanup`
- `L1_OneBatchAcquiresTheLeaseExactlyOnce_AndReleasesIt`
- `L1_LeaseIsReleasedOnCancelAndOnFailure_NeverReleasedWhenNotAcquired`
- `L2_MissingLease_IsBoundedWaited_ThenBusy_WithoutWritingAnything_AndReplayingTheSameChangeSetSucceeds`
- `L3_FileRewrittenWhileWaitingForTheLease_IsIndexedFromThePostLeaseStat`
- （其余 9 条同类用例略）

`LuceneMaintenanceEngineTests.cs` 覆盖 13 条用例：
- `Initialize`
- `Cleanup`
- `I1_ExtractionFailure_KeepsOldDocuments_AndDoesNotTouchTheIndex`
- `I1_Control_ExtractionSuccess_DeletesOldDocumentsBeforeAddingTheNewOnes`
- `I2_SingleBatchCommit_IsAtomic_AndUncommittedDocumentsStayInvisible`
- `I5_QuotaPrecheck_RejectsWithoutCommitting_AndKeepsIndexBytesEqual`
- （其余 7 条同类用例略）

`MaintenanceCoreTests.cs` 覆盖 34 条用例：
- `MTime_GreaterOrEqualBoundary_RequiresProcessing`
- `MTime_UnreadableMtimeOrMissingWatermark_FailsStale`
- `MTime_NegativeOverlap_IsRejected`
- `ScanWindow_WatermarkTakesScanStart_SoFileWrittenAfterEnumerationIsStillProcessedNextRound`
- `ClockRollback_RequiresFullScopeRecalibration`
- `StatStability_RequiresBothSidesReadableAndEqual`
- （其余 28 条同类用例略）

`MaintenanceIntegrityTests.cs` 覆盖 4 条用例：
- `M7a_Probe_MissingIndex_IsManualRebuildRequired_AndCreatesNothing`
- `M7b_Probe_UnreadableIndex_IsManualRebuildRequired`
- `M7c_Probe_DegradedThenHealthy_AndIsBitIdenticalReadOnly`
- `M8_HealthCheck_BacksOffWhenUnsampleable_AndRunsSmallSlicesWhenResourcesAreOk`

`MaintenanceLoopHarness.cs` 覆盖 1 条用例：
- `Dispose`

`MaintenanceLoopTests.cs` 覆盖 15 条用例：
- `M1_DefaultOff_CreatesNothingAndTouchesNothing`
- `M1b_DisabledOption_StartAsyncIsANoOpWithZeroSideEffects`
- `M2_Lifecycle_IsIdempotentAndReentrant`
- `M3_WatcherEvents_AreCoalescedPerPath_AndRenameFoldsToDeletePlusUpsert`
- `M3b_RealFileSystemWatcher_DeliversChangesToTheIndex`
- `M4_RecoveryScan_IsAnIndependentPath_AndUnchangedFilesAreNotRewritten`
- （其余 9 条同类用例略）

`MaintenanceOptionsBoundaryTests.cs` 覆盖 4 条用例：
- `OptionsBoundary_EveryThreshold_AcceptsBothEnds_RejectsOneStepBeyond_AndNamesTheOption`
- `OptionsBound_SamplesAreAnchoredToThePublicBoundConstants`
- `OptionsBound_PublicStaticBoundConstants_MatchTheCollectedList`
- `OptionsBound_AboveUpperSample_IsStillRejectedWhenDisabled`

`MaintenancePathBoundaryTests.cs` 覆盖 3 条用例：
- `PathMatrix_EveryRowAssertsIsWithinScopeAndTheRejectionReason`
- `PathMatrix_PrefixSiblings_AreNeverSwallowedAsInScope`
- `PathMatrix_SeparatorAndCaseVariants_AllResolveToTheSameVerdict`

`QuotaEnforcementTests.cs` 覆盖 9 条用例：
- `Initialize`
- `Cleanup`
- `I8_QuotaWrapperUnderGenerousBudget_KeepsContentIdenticalToTheUnwrappedBuildPath`
- `I9_WriteTimeQuotaBreach_AbortsBatch_RollsBack_AndKeepsIndexWithinBudget`
- `I10_MergeOutputIsCountedAgainstTheSameBudget_AndMergeOverrunLeavesNoResidue`
- `I11_RollbackRestoresExactFileSet_NoResidualSegmentFiles`
- （其余 3 条同类用例略）

`ReaderLifecycleTests.cs` 覆盖 4 条用例：
- `Initialize`
- `Cleanup`
- `InvalidateDuringQuery_DoesNotBreakTheInFlightSearch`
- `QueryAfterInvalidate_ReturnsFreshResults`

`ScanPerFileIsolationTests.cs` 覆盖 10 条用例：
- `Initialize`
- `Cleanup`
- `A1_TryCollectCandidate_AcceptsNormalFile_WithDiskFacts`
- `A2_TryCollectCandidate_MissingFile_ReturnsFalseWithoutThrowing`
- `A3_TryCollectCandidate_PolicySkips_KeepTheirOwnReasons`
- `A4_TryCollectCandidate_CancelledToken_ThrowsOperationCanceled`
- （其余 4 条同类用例略）

`StagedSupplyBuilderTests.cs` 覆盖 14 条用例：
- `A1_Swap_Succeeds_And_The_Staging_Directory_Disappears`
- `A2_Precheck_Rejects_Over_Budget_And_The_Build_Never_Starts`
- `A3_Measured_Limit_Rejects_And_Clears_Staging_Leaving_Live_Untouched`
- `A4_Swap_Failure_Rolls_The_Old_Live_Back`
- `A5_Invalidation_Brackets_The_Swap_In_The_Mandated_Order`
- `A6_Budget_Is_Aggregated_Across_All_Live_Scopes`
- （其余 8 条同类用例略）

`StagedSupplyCoordinatorTests.cs` 覆盖 3 条用例：
- `A2_Coordinator_Rejects_Over_Budget_Without_Building_And_Live_Is_Untouched`
- `R2_Plan_And_The_Build_Gate_Agree_On_The_Same_Budget`
- `R6_Terminal_Job_Message_Carries_The_Swap_Fields`

`StagedSupplyEndToEndTests.cs` 覆盖 2 条用例：
- `A1_A5_Real_Lucene_Swap_Is_Queryable_And_The_Same_Engine_Sees_The_New_Content`
- `A3_Over_Budget_Rebuild_Is_Rejected_And_The_Old_Index_Is_Still_Queryable`

`StagedSupplyRegressionGateTests.cs` 覆盖 11 条用例：
- `A1_Zero_Document_Staging_Is_Rejected_And_Live_Stays_Untouched`
- `A1b_Zero_Document_Staging_Is_Rejected_Even_Without_A_Live_Index`
- `A2_Staging_One_Doc_Vs_Live_Hundred_Is_Rejected_By_The_Ratio`
- `A3_Sixty_Docs_Vs_Live_Hundred_Is_Still_Swapped`
- `A4_First_Build_With_Three_Docs_Is_Allowed`
- `A5_Real_Lucene_Near_Empty_Staging_Cannot_Replace_A_Populated_Live_Index`
- （其余 5 条同类用例略）

`StagedSupplyTestDoubles.cs` 覆盖 3 条用例：
- `InvalidateScope`
- `Move`
- `Dispose`

`SupplyCoordinatorConcurrencyTests.cs` 覆盖 5 条用例：
- `A3_Two_Concurrent_Same_Scope_Requests_Run_Builder_Once_And_Share_The_Same_JobId`
- `A4_Eight_Concurrent_Same_Scope_Requests_Run_Builder_Exactly_Once`
- `A6_Two_Different_Scopes_Build_Independently_Without_Blocking_Each_Other`
- `Same_Scope_Within_MinRebuild_Interval_Merges_Without_Rebuilding`
- `Same_Scope_After_MinRebuild_Interval_Starts_A_New_Job`

`SupplyCoordinatorLifecycleTests.cs` 覆盖 8 条用例：
- `A7_Illegal_Transitions_Are_Rejected_With_Message_And_Leave_State_Unchanged`
- `A7_Cancel_While_Running_Ends_Cancelled_And_Signals_The_Builder`
- `A7_Cancel_On_Terminal_Or_Unknown_Job_Is_Rejected_And_Recorded`
- `A8_Builder_Exception_Ends_Failed_With_Reason_And_Releases_The_Lease`
- `A8_Failed_Build_Result_Ends_Failed_With_The_Builder_Error_Text`
- `Terminal_Job_History_Is_Bounded_And_Evicts_The_Oldest`
- （其余 2 条同类用例略）

`SupplyLeaseTests.cs` 覆盖 6 条用例：
- `A5a_External_Lease_Holder_Makes_Build_Busy_Without_Running_The_Builder`
- `A5b_Expired_Lease_Is_Taken_Over_And_The_Reason_Is_Recorded`
- `A5_Control_Unexpired_Lease_Is_Not_Taken_Over_Under_The_Same_Clock_Skew`
- `Lease_File_Lives_Under_Supply_Leases_With_Sha256_Of_The_ScopeKey`
- `Renew_Refreshes_Heartbeat_And_Release_Rejects_Foreign_Owner`
- `Exclusive_Open_Of_The_Lease_File_Blocks_Acquire_Until_Closed`

`SupplyPlanTests.cs` 覆盖 6 条用例：
- `Plan_Writes_Nothing_Under_The_Index_Root`
- `Plan_Prediction_Uses_The_Documented_Index_Size_Factor`
- `Plan_Budget_Defaults_To_One_Gibibyte_And_Honours_Request_Override`
- `Plan_Keeps_Accepted_Part_Visible_When_One_Scope_Is_Rejected`
- `Plan_Rejects_Non_Positive_Budget_At_The_Boundary`
- `Supply_Fixture_Never_Points_At_The_Real_Index_Root`

`SupplyScopeNormalizationTests.cs` 覆盖 11 条用例：
- `Empty_Scope_Is_Rejected_With_Empty_Reason`
- `Empty_Request_Is_Rejected_With_Empty_Reason`
- `Relative_Scope_Is_Rejected_With_Relative_Reason`
- `Missing_Directory_Is_Rejected_With_NotFound_Reason`
- `File_Path_Is_Rejected_With_NotDirectory_Reason`
- `Duplicate_Scope_After_Normalization_Is_Rejected`
- （其余 5 条同类用例略）

`SupplyTestDoubles.cs` 覆盖 1 条用例：
- `Dispose`
