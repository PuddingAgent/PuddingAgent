# PuddingCoreTests
> 源指纹: *.cs=25-35d2f83a4da6, *.csproj=1-d24e40d6b1f6 · 条目数: 0 · 最近整理: 2026-11-23

测试工程，引用 PuddingCore；用例全部为进程内单测，不需要真实宿主或外部服务。

`AnthropicMessagesLlmGatewayTests.cs` 覆盖 9 条用例：
- `ChatAsync_BasicRequest_UsesMessagesEndpointHeadersAndTopLevelSystem`
- `ChatAsync_SystemAndTools_CarryPromptCacheBreakpoints`
- `ChatAsync_ToolRound_UsesToolUseAndGroupedToolResults`
- `ChatAsync_Continuation_ReplaysSignedThinkingAndToolUseBlocks`
- `ChatAsync_ParsesTextThinkingToolsUsageAndContinuation`
- `ChatStreamAsync_ToolEvents_MapDenseIndexAndPreserveContinuation`
- （其余 3 条同类用例略）

`ContextUsageCalibrationRelaxationTests.cs` 覆盖 5 条用例：
- `Higher_Measured_Ratio_Raises_The_Calibration_Immediately`
- `Lower_Measured_Ratio_Relaxes_Instead_Of_Sticking_At_The_Maximum`
- `Repeated_Low_Measurements_Converge_To_One_And_Never_Below_It`
- `Relaxation_Is_Scoped_To_The_Model_That_Reported_It`
- `A_Higher_Measurement_After_Relaxation_Raises_Again`

`ContextUsageSnapshotAttributionTests.cs` 覆盖 4 条用例：
- `CaptureLlmRequest_SplitsMessagesIntoMutuallyExclusiveSourceBuckets`
- `CaptureLlmRequest_CompactionSummaryIsNotCountedAsConversation`
- `CaptureLlmRequest_WarmPrefixCheckpointIsCountedAsCompactionSummary`
- `CaptureLlmRequest_ReasoningIsDeductedFromConversationBucket`

`ContextUsageSnapshotRequestAttributionTests.cs` 覆盖 6 条用例：
- `MeasureLlmRequest_DoesNotPublishPreparedRequest`
- `RecordProviderUsage_AuxiliaryPurposeDoesNotTouchPreparedOrCalibration`
- `RecordProviderUsage_MainPurposeWritesSeparateMeasurement`
- `RecordProviderUsage_LateAuxiliaryUsageDoesNotClobberNewerPreparedRequest`
- `RecordProviderUsage_MissingUsageWritesNoMeasurement`
- `InterleavedMainAndAuxiliaryUsage_StaySeparated`

`GoalCheckEvidencePolicyTests.cs` 覆盖 28 条用例：
- `CleanTestReport_Passes`
- `SelfReportedPassed_WithoutRunnerId_Fails`
- `ReportForAnotherCriterion_Fails`
- `ExitZero_WithZeroTests_CannotPass`
- `MissingInvocationReference_Fails`
- `MissingRunReport_Fails`
- （其余 22 条同类用例略）

`GoalSettlementCriterionAggregationTests.cs` 覆盖 12 条用例：
- `TwoChecksForOneCriterion_BuildPassedTestFailed_DoesNotAdvance`
- `TwoChecksForOneCriterion_SameVerdict_WhenOrderIsReversed`
- `TwoChecksForOneCriterion_BuildPassedTestWaiting_NeverAdvances`
- `InvalidatedCheck_OutranksPassedSibling`
- `AllChecksPassed_ForAllRequiredCriteria_Advances`
- `MissingCriterionResult_DoesNotPass`
- （其余 6 条同类用例略）

`GoalSettlementDecisionCalculatorTests.cs` 覆盖 10 条用例：
- `CompletedTurn_WithPlanOnly_DoesNotVerifyWorkUnit`
- `EmptyContract_IsNeverAVacuousPass`
- `PartiallyVerifiedCriteria_StayOnCurrentUnit`
- `FailedCriterion_RepairsCurrentUnit`
- `StaleRevisionResult_DoesNotCount`
- `OptionalCriterion_DoesNotBlockAdvance`
- （其余 4 条同类用例略）

`LlmBodyReasoningProbeTests.cs` 覆盖 4 条用例：
- `ReadsEffortFromRequestBody`
- `ReturnsNullWhenReasoningSectionIsAbsent`
- `ReturnsNullWhenEffortIsNotAString`
- `ReturnsNullForMalformedOrEmptyInput`

`LlmBodyTextSanitizerTests.cs` 覆盖 5 条用例：
- `RedactInlineImages_LeavesTextWithoutImagesUntouched`
- `RedactInlineImages_StripsPayloadAndKeepsSurroundingJson`
- `RedactInlineImages_CountsEveryImage`
- `RedactInlineImages_DoesNotTreatAudioPayloadAsImage`
- `RedactInlineImages_HandlesNullAndEmpty`

`LlmMessageSequenceNormalizerTests.cs` 覆盖 5 条用例：
- `ProviderProjection_UsesTypedSummaryOnly_AndKeepsRuntimeBoundary`
- `ProviderProjection_RejectsSummaryWithNativeResponseState`
- `Normalize_PreservesCompleteToolRound`
- `Normalize_DowngradesAssistantAndDropsPartialResults_WhenToolRoundIsIncomplete`
- `Normalize_RemovesEmptyIncompleteAssistantAndOrphanTools`

`LlmRequestBodyFingerprintTests.cs` 覆盖 3 条用例：
- `Compute_CountsUtf8Bytes_NotCharacters`
- `Compute_DoesNotNormalizeNewlines`
- `Compute_IsDeterministicAndMatchesRawSha256`

`LlmRequestCaptureLoggingTests.cs` 覆盖 1 条用例：
- `WriteAsync_LogsBodyTokensAlongsideTheEstimate`

`LlmRequestCaptureTests.cs` 覆盖 2 条用例：
- `WriteAsync_WithoutContent_DoesNotThrow`
- `WriteAsync_PreservesContentTypeAndBytes`

`OpenAiLlmGatewayCompatibilityTests.cs` 覆盖 6 条用例：
- `ChatStreamAsync_MissingIdsAndMultiCallChunk_EmitsEveryCallWithStableUniqueIds`
- `ChatStreamAsync_LateProviderId_ReplacesTemporarySyntheticId`
- `ChatStreamAsync_DuplicateProviderIds_RepairsTheSecondCall`
- `ChatAsync_MissingAndDuplicateIds_ReturnsProtocolSafeToolCalls`
- `ChatStreamAsync_StampsProviderDispatchAndHeadersTimings`
- `ChatStreamAsync_ChunkWithoutModelPayload_DoesNotEmitDelta`

`PromptCalibrationConvergenceTests.cs` 覆盖 5 条用例：
- `Ratio_FallsBelowOne_WhenProviderReportsFewerTokens`
- `Ratio_StopsAtFloor_WhenProviderStaysLow`
- `Ratio_RisesImmediately_WhenProviderReportsMoreTokens`
- `Ratio_IgnoresImplausiblyHighObservation`
- `Ratio_IgnoresObservationWhenCacheReadParticipated`

`ReasoningEffortPolicyTests.cs` 覆盖 16 条用例：
- `ManualLevelsMapOneToOne`
- `ManualLevelBeatsConfigured`
- `AutoPrefersConfiguredOverModelDefault`
- `AutoWithoutConfiguredUsesModelDefault`
- `AutoWithNothingSpecifiedDefaultsToHigh`
- `InvalidConfiguredIsRejectedAndReported`
- （其余 10 条同类用例略）

`RequestAttributionIdentityTests.cs` 覆盖 8 条用例：
- `LateUsageForEarlierRequest_DoesNotRewriteNewerPreparedSnapshot_NorCalibrateWithItsRaw`
- `UnknownIdentityUsage_GoesToUnpairedLedger_WithoutCalibrationOrOverwrite`
- `LastMeasuredProjection_AdvancesByRequestSequence_NotArrivalOrder`
- `RepeatedUsageForSameAttempt_IsIdempotent`
- `SameInvocationDifferentAttempt_PairsPerAttempt`
- `AuxiliaryUsage_NeitherCalibratesNorBorrowsMainSlot`
- （其余 2 条同类用例略）

`RequestAttributionStrictPairingTests.cs` 覆盖 5 条用例：
- `UnknownAttempt_DoesNotFallBackToKnownAttempt`
- `AttemptWithMismatchedInvocation_IsUnpaired`
- `InvocationOnlyFallback_RequiresUniqueness`
- `AuxiliaryUsage_DoesNotBorrowMainPreparedRecord`
- `RetriedAttempts_PairPerAttempt`

`ResponsesLlmGatewayReasoningEffortTests.cs` 覆盖 2 条用例：
- `ChatAsync_WritesReasoningEffortIntoTheRequestBody`
- `ChatAsync_OmitsReasoningSectionWhenEffortIsAbsent`

`ResponsesLlmGatewayTests.cs` 覆盖 45 条用例：
- `CompactedToolLoop_ProjectsCheckpointAsInputAndPreservesNativeReplay`
- `DeepSeekRequest_UsesCompactUnicodeAndPlaintextReasoningReplay`
- `ChatStreamAsync_TerminalEventCompletesWithoutWaitingForEof`
- `ChatStreamAsync_EofBeforeTerminalDoesNotReportPartialReplyAsSuccess`
- `ChatAsync_BasicRequest_UsesResponsesStatePolicy`
- `ChatAsync_ToolRound_UsesFunctionCallAndFunctionCallOutputItems`
- （其余 39 条同类用例略）

`SkillDistillationContractTests.cs` 覆盖 21 条用例：
- `ValidProduct_ShouldPass_WithNoViolations`
- `MissingApplicabilitySection_ShouldViolate`
- `MissingPitfallSection_ShouldViolate`
- `HeadingMatch_ShouldIgnoreHashPrefixAndWhitespace`
- `IdentityLiteralInContent_ShouldViolate`
- `IdentityLiteralInKeyword_ShouldViolate`
- （其余 15 条同类用例略）

`SkillFamilyClusteringTests.cs` 覆盖 21 条用例：
- `Tokenize_ShouldLowercaseTrimAndDropShortTokens`
- `Tokenize_ShouldDropTokensShorterThanPolicyMinimum`
- `Tokenize_ShouldReturnEmptySet_ForNullOrWhitespaceName`
- `Tokenize_ShouldTakeSeparatorsFromPolicy_NotFromHardcodedSet`
- `Jaccard_ShouldReturnIntersectionOverUnion`
- `Jaccard_ShouldBeZeroWheneverEitherSideIsEmpty`
- （其余 15 条同类用例略）

`SkillKeywordNormalizationTests.cs` 覆盖 9 条用例：
- `Collect_ShouldTakeKeywordsTagsSkillIdAndNameTokensTogether`
- `Collect_ShouldDeduplicateCaseInsensitively`
- `Collect_ShouldAlwaysIncludeSkillId_EvenWhenBlank`
- `Collect_ShouldSplitName_AndDropSingleCharacterTokens`
- `Collect_ShouldAddFullName_EvenWhenEveryTokenIsTooShort`
- `NameTokenSeparators_ShouldEqualFamilyTokenizationSeparators`
- （其余 3 条同类用例略）
