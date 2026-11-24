# PuddingMemoryEngineTests
> 源指纹: *.cs=34-f33edb2cc456, *.csproj=1-a3db38e10aea · 条目数: 0 · 最近整理: 2026-11-23

测试工程，引用 PuddingMemoryEngine、PuddingRuntime；用例全部为进程内单测，不需要真实宿主或外部服务。

`ChapterSourceMessageIdTests.cs` 覆盖 4 条用例：
- `AddChapter_ShouldPersistSourceMessageId`
- `AddChapter_WithoutSourceMessageId_ShouldNotBePolluted`
- `Initializer_ShouldHealLegacyChaptersTable_AndBeIdempotent`
- `UpsertExperience_ShouldPassThroughSourceMessageId`

`ChapterSourceTurnIdTests.cs` 覆盖 5 条用例：
- `AddChapter_ShouldPersistSourceTurnId`
- `AddChapter_WithoutSourceTurnId_ShouldNotBePolluted`
- `ChapterRecord_RoundTripsSourceTurnId`
- `Initializer_ShouldHealLegacyChaptersTable_AndBeIdempotent`
- `UpsertExperience_ShouldPassThroughSourceTurnId`

`ContextCompactionCommitRevalidationTests.cs` 覆盖 10 条用例：
- `GenerationPhase_DoesNotHoldTheCompactionLock`
- `StaleGenerationAtCommit_WritesNothing_AndReportsStaleCandidate`
- `BackgroundCompaction_DefersWhenForegroundDemandAppears`
- `UnchangedPreconditions_StillApplies`
- `CandidateContentMutatedDuringGeneration_IsRejected`
- `CandidateRowDeletedDuringGeneration_IsRejected`
- （其余 4 条同类用例略）

`ContextCompactionPressureTests.cs` 覆盖 5 条用例：
- `ProviderUsage_DoesNotMislabelAnOverestimate_AndRelaxesCalibrationWithinFloor`
- `MissingUsage_PreservesEstimateAndItsConfidence`
- `MissingTotal_DerivesPromptPlusCompletionInMeasurementOnly`
- `Health_RejectsUsageFromBeforeCheckpoint_InMemoryAndAfterRestart`
- `Health_CurrentRequestTakesPrecedenceOverPreviousDatabaseUsage`

`ContextCompactionServiceTests.cs` 覆盖 23 条用例：
- `ContextHealthEvaluator_ReturnsCriticalWhenUsageExceedsCriticalThreshold`
- `GetHealthAsync_UsesLatestOutboundContextUsageSnapshot_WhenAvailable`
- `GetHealthAsync_ExposesLastMeasuredMainRequest_SeparatelyFromPreparedEstimate`
- `GetHealthAsync_AuxiliaryMeasurement_DoesNotPopulateMeasuredInput`
- `GetHealthAsync_LargeWindow_DoesNotCompactAtOldAbsoluteCap`
- `GetHealthAsync_LargeWindow_StillCompactsAtRealPressure`
- （其余 17 条同类用例略）

`ContextSegmentLedgerTests.cs` 覆盖 5 条用例：
- `InitializeSchema_Twice_ShouldBeIdempotent`
- `InsertAndQuery_ContextSegment_ShouldRoundTripAllFields`
- `NullableFields_ShouldPersistAsNull_WithDefaultsApplied`
- `DuplicateSegmentId_ShouldViolatePrimaryKey`
- `SelfHealing_LegacyDatabaseWithoutContextSegments_ShouldCreateTable`

`EventPreprocessorTests.cs` 覆盖 2 条用例：
- `ProcessAsync_DifferentPayloadsFromSameSourceWithinDedupWindow_AreNotDropped`
- `ProcessAsync_ExactDuplicateFromSameSourceWithinDedupWindow_IsDropped`

`FactMemoryServiceTests.cs` 覆盖 5 条用例：
- `ComputeFreshness_Exponential_ShouldDecayByHalfLife`
- `MatchContext_RequiredKeyConflict_ShouldReturnZero`
- `CreateFactAsync_ShouldPersistEvidenceFreshnessContextAndRevision`
- `CreateFactAsync_WithoutEvidence_ShouldReject`
- `MemoryLibraryDbInitializer_ShouldCreateFactMemoryTables`

`FactMemorySinkTests.cs` 覆盖 3 条用例：
- `Sink_writes_one_fact_row`
- `Repeated_sink_reuses_the_same_memory_space`
- `Empty_statement_is_rejected_before_any_write`

`FactMemoryWriteMonitorTests.cs` 覆盖 11 条用例：
- `Disabled_is_not_an_alarm`
- `Enabled_but_empty_is_never_written`
- `Recent_write_is_healthy`
- `Old_write_is_stalled`
- `Boundary_exactly_at_threshold_is_still_healthy`
- `Rows_without_timestamp_is_stalled_because_it_cannot_be_trusted`
- （其余 5 条同类用例略）

`JiebaSegmenterPoolTests.cs` 覆盖 5 条用例：
- `Resources_DictTxt_ShouldExistInOutputDirectory`
- `JiebaSegmenterPool_Initialize_ShouldNotThrow`
- `TryResolveResourceDir_MissingDictTxt_ShouldNotMatchDirectStrategies`
- `ResolveResourceDirectory_ShouldResolveDirectoryWithDictTxt`
- `LastInitError_ShouldBeCleared_AfterSuccessfulInitialization`

`LlmInputBudgetRegressionTests.cs` 覆盖 7 条用例：
- `ResolveEffectiveInputLimit_UsesSmallestProviderOrReservedContextLimit`
- `RecordProviderUsage_CalibratesNextRequestFromItsOwnRaw_WithoutReplacingPreparedSnapshot`
- `CaptureLlmRequest_CountsReasoningAndToolCallPayloads`
- `Prepare_TrimsOldConversationUnitsUntilCalibratedRequestFits`
- `ProviderInputLengthError_ParsesLimitAndRaisesConservativeCalibration`
- `DirectLlmClient_PropagatesResolvedMaxOutputTokensToProviderRequest`
- （其余 1 条同类用例略）

`MemoryDatabaseInitializationTests.cs` 覆盖 1 条用例：
- `CoreInitializer_RepairsDatabaseAlreadyCreatedByLibraryContext`

`MemoryLibraryAdr029Tests.cs` 覆盖 6 条用例：
- `DbInitializer_ShouldNotCrashOnDuplicateColumn`
- `DbInitializer_ShouldCreateAdr028TablesOnFreshDatabase`
- `GrepMemory_ShouldNotReturnOtherWorkspace`
- `SaveMemorySkill_ShouldInjectWorkspace`
- `CreateGeneralPointer_ShouldSupportBookToUrl`
- `GetPointersBySource_ShouldBeWorkspaceScoped`

`MemoryLibraryTests.cs` 覆盖 57 条用例：
- `CreateLibrary_ShouldSucceed`
- `ListLibraries_ShouldReturnByWorkspace`
- `CreateBook_ShouldSucceed`
- `CreateBook_WithTagPaths_ShouldCreateIndexes`
- `CreateBook_ConcurrentSameTitle_ShouldReturnSingleActiveBook`
- `GetBook_ShouldIncrementAccessCount`
- （其余 51 条同类用例略）

`MemoryPersistenceTests.cs` 覆盖 14 条用例：
- `SessionCrud_ShouldCreateReadUpdateDelete`
- `MessageTree_ShouldTraceFromLeafToRoot`
- `MessageBranchType_ShouldFilterMainAndRetry`
- `MemoryCrud_ShouldSupportTagAndSupersededFiltering`
- `BuildMemoryContext_ShouldRespectAgentIsolationAndParentSessionRecall`
- `TagTreeIndexer_ShouldSearchPrefixAndReturnChildren`
- （其余 8 条同类用例略）

`MemoryWriteCoordinatorExecutionTests.cs` 覆盖 1 条用例：
- `CoordinateAsync_ShouldExecuteAppendNewByCreatingChapter`

`MessageCompactionColumnsTests.cs` 覆盖 3 条用例：
- `InitializeSchema_Twice_MessagesCompactionColumnsShouldExist`
- `InsertAndQuery_Message_ShouldRoundTripCompactionColumns`
- `Message_CompactionColumns_ShouldBeNullableByDefault`

`SessionChunkRecallDedupTests.cs` 覆盖 2 条用例：
- `RecallDedup_ShouldFilterCoveredByDefaultAndReturnWithHashWhenIncluded`
- `RecallDedup_ShouldReturnUncoveredChunkNormally`

`SessionChunkVectorRecallTests.cs` 覆盖 6 条用例：
- `SearchChunksByVector_ShouldReturnDescendingSimilarity`
- `SearchChunksByVector_ShouldRespectWorkspaceIsolation`
- `Recall_WithChunkVectors_ShouldFuseChunkPrefixedSourceIds`
- `SearchChunksByVector_ShouldSkipChunksWithoutEmbedding`
- `SearchChunksByVector_ShouldFilterCoveredByDefaultAndReturnWithHashWhenIncluded`
- `SearchChunksByVector_ShouldExposeMessageHashAndComputeFallbackWhenMissing`

`SessionChunkVectorsTests.cs` 覆盖 5 条用例：
- `InitializeSchema_Twice_ShouldBeIdempotent`
- `InsertAndQuery_SessionChunkVector_ShouldSucceed`
- `DuplicateMessageChunkSeq_ShouldViolateUniqueConstraint`
- `InsertAndQuery_SessionChunkVector_ShouldRoundTripDedupColumns`
- `InitializeSchema_OnLegacyTable_ShouldAddDedupColumnsWithoutDroppingData`

`SkillCurationGateTests.cs` 覆盖 20 条用例：
- `CleanProduct_ShouldProduceShadowVerdict_AndEveryCriterionPassed`
- `C1_ShouldReject_WhenAReplacedTurnIsMissingFromEvidence`
- `C2_ShouldReject_WhenCoveringSessionsDrop`
- `C2_ColdStart_ShouldRecordButNotBlock_WhenNoReplacedSkillHasSessionTags`
- `C3_ShouldReject_WhenCollisionLandsOnThirdPartyEnabledSkill`
- `C3_ShouldPass_WhenCollisionOnlyLandsOnReplacedSkill`
- （其余 14 条同类用例略）

`SkillDeterministicEligibilityTests.cs` 覆盖 4 条用例：
- `Eligibility_ShouldRequireNonEmptyAndIdenticalToolSets`
- `Eligibility_ShouldRejectLooseText_BelowTheFloor`
- `Eligibility_ShouldAcceptBySharedSourceTurn_AtLooseSimilarity`
- `Eligibility_ShouldRequireHighSimilarity_WhenNoSharedTurn`

`SkillEvolutionDeduplicationServiceTests.cs` 覆盖 6 条用例：
- `Admission_ShouldDefer_WhenCreateConfidenceIsBelowThreshold`
- `AdmissionMerge_ShouldAppendEvidenceAndStructuredSourceTags`
- `Consolidation_ShouldDisableOnlyDeterministicallyEligibleDuplicates`
- `ProcessedTurnExtraction_ShouldReadStructuredAndLegacyProvenance`
- `EvaluationWatermark_ShouldRemainCurrentOnlyForTheReviewedVersion`
- `DedupWatermark_ShouldSkipFlash_WhenEveryEnabledVersionWasConfidentlyReviewed`

`SkillFamilyCapJudgeTests.cs` 覆盖 12 条用例：
- `PerFamilyCapNull_ShouldProduceNoReview_ForAnyFamily`
- `ZeroReviews_ShouldStillExposeWhetherTheCapWasEnforced`
- `FamilyAtExactlyTheCap_ShouldNotBeReviewRequested`
- `FamilyOneOverTheCap_ShouldBeReviewRequested_WithEveryContractField`
- `Cap_ShouldComeFromPolicyInstance_NotFromHardCodedValue`
- `PolicyRef_ShouldCarryTheVersionActuallyUsed`
- （其余 6 条同类用例略）

`SkillFamilyReviewWiringTests.cs` 覆盖 7 条用例：
- `WithoutAnyPolicy_ShouldReturnZeroReviews_AndEvaluateNothing`
- `WithOnlyOneOfTheTwoPolicies_ShouldReturnZeroReviews`
- `NullSkills_ShouldBeRejected`
- `OverCapFamily_ShouldProduceExactlyOneReviewRequest`
- `CompliantOrDefaultUnlimitedCap_ShouldProduceZeroReviewRequests`
- `InputOrder_ShouldNotChangeTheOutcome`
- （其余 1 条同类用例略）

`SkillKeywordOwnershipJudgeTests.cs` 覆盖 12 条用例：
- `Apply_ShouldKeepEveryCompetitor_NotJustTheFirstOne`
- `Apply_ShouldEmitOnePendingItemPerSharedKeyword`
- `Apply_ShouldNotTruncateThePendingList`
- `Apply_ShouldNotDefaultToRejectingLaterDeclarers`
- `Apply_ShouldClassifyViaTheInjectedPredicate_NotViaAPrivateCopy`
- `Apply_WithTheRealToolNamePredicate_ShouldAgreeWithItForEveryDispute`
- （其余 6 条同类用例略）

`SkillKeywordOwnershipProbeTests.cs` 覆盖 13 条用例：
- `Analyze_ShouldCountSlotsAcrossEnabledSkills`
- `Analyze_ShouldExcludeDisabledSkillsEntirely`
- `Analyze_ShouldListEveryCompetitor_NotJustTheFirst`
- `Analyze_ShouldReportSingletonsToo`
- `Analyze_ShouldReportSharedCountAndDisplacedOpportunities`
- `Analyze_ShouldKeepTotalsConsistentWithTheExposedOwnerships`
- （其余 7 条同类用例略）

`SkillMergeEligibilityJudgeTests.cs` 覆盖 18 条用例：
- `Apply_WithAllFourConditions_ShouldBeEligible`
- `Apply_WithADifferentFamily_ShouldFailOnlyTheFamilyCondition`
- `Apply_WithoutKeywordOverlap_ShouldFailOnlyTheKeywordCondition`
- `Apply_WithDifferentProceduralText_ShouldFailOnlyTheTextCondition`
- `Apply_WithoutSharedEvidence_ShouldFailOnlyTheEvidenceCondition`
- `Apply_AtExactlyTheThreshold_ShouldBeEligible`
- （其余 12 条同类用例略）

`SkillPortfolioAdmissionExecutorTests.cs` 覆盖 8 条用例：
- `Create_ShouldMaterializeOnce_AndNeverToggleEnabled`
- `Create_WhenMaterializationFails_ShouldReportNoSkillId_WithoutWriting`
- `Displace_ShouldDisableTargetBeforeMaterializing_ThenReportBothIds`
- `Displace_WhenMaterializationFails_ShouldRollBackTargetToEnabled`
- `Displace_WhenMaterializationThrows_ShouldRollBack_AndRethrow`
- `Displace_WithoutTargetSkillId_ShouldDefer_WithoutAnyWrite`
- （其余 2 条同类用例略）

`SkillPortfolioAdmissionJudgeTests.cs` 覆盖 20 条用例：
- `Policy_ShouldRejectDegenerateConfiguration_AtConstructionTime`
- `BelowSoftTarget_ShouldPassThroughDedupResult_EvenWhenBudgetFieldsAreUnavailable`
- `NoUpgrade_ShouldNeverTurnNonCreateIntoCreate`
- `BudgetFull_WithInsufficientMargin_ShouldDefer_NotCreate`
- `BudgetFull_WithSufficientMargin_ShouldDisplaceLowestValue`
- `MarginEqualToThreshold_ShouldNotAdmit_BecauseComparisonIsStrict`
- （其余 14 条同类用例略）

`SubconsciousJobQueueTests.cs` 覆盖 17 条用例：
- `EnqueueAsync_ShouldReuseActiveJob_WhenIdempotencyKeyMatches`
- `LeaseNextAsync_ShouldMarkPendingJobProcessing`
- `RetryAsync_ShouldBackoffAndDeadLetterAfterMaxRetries`
- `QueueTransitions_ShouldRecordRuntimeActivities`
- `QueueTransitions_ShouldRecordTelemetryMetrics`
- `LeaseNextAsync_ShouldSkipExcludedWorkspace`
- （其余 11 条同类用例略）
