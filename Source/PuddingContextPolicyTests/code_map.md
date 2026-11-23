# PuddingContextPolicyTests
> 源指纹: *.cs=8-ffe213c250fe, *.csproj=1-a27626741098 · 条目数: 0 · 最近整理: 2026-11-23

测试工程，引用 PuddingContextPolicy；用例全部为进程内单测，不需要真实宿主或外部服务。

`CompletedExecutionCompactionTests.cs` 覆盖 8 条用例：
- `LargeCompletedTurnCanCrossTailPreferenceButNeverCurrentUser`
- `BytePreparationThresholdCreatesCandidateBeforeHardUploadLimit`
- `LongCurrentTurn_SelectsCompletedExecutionInsteadOfResummarizingTinyOldCheckpoint`
- `IncompleteOrMismatchedBatch_IsABarrier_EvenOutsideRecentTail`
- `TailBoundaryInsideParallelBatch_ProtectsEntireBatch`
- `FixedFloorCannotFit_DoesNotGrantSummaryBudget`
- （其余 2 条同类用例略）

`ComponentBoundaryTests.cs` 覆盖 4 条用例：
- `Boundary_Detector_Flags_Forbidden_Names`
- `Test_Process_Must_Not_Load_Forbidden_Assemblies`
- `Test_Dependency_Closure_Must_Not_Contain_Forbidden_Assemblies`
- `Component_Assembly_References_Must_Stay_BclOnly`

`ContextBackoffPolicyTests.cs` 覆盖 8 条用例：
- `Decide_HardProtectionOverridesBackoff`
- `Decide_NoPreviousRecordAttempts`
- `Decide_SameCandidateWithinIntervalIsSuppressed`
- `Decide_IntervalExpiredAttemptsAgain`
- `Decide_NewCandidateFingerprintAttemptsAgain`
- `Decide_GenerationChangeAttemptsAgain`
- （其余 2 条同类用例略）

`ContextCandidateApplicabilityTests.cs` 覆盖 7 条用例：
- `Validate_UnchangedContextIsApplicable`
- `Validate_NewlyAppendedMessageInvalidatesCandidate`
- `Validate_AnyVersionChangeIsStale`
- `Validate_RemovedOrReorderedSpanIsNotIntact`
- `Validate_InsertedMessageInsideRetainedSuffixInvalidates`
- `Validate_ShrunkRetainedSuffixInvalidates`
- （其余 1 条同类用例略）

`ContextCandidateSelectorTests.cs` 覆盖 5 条用例：
- `Select_BelowSoftTrigger_CreatesNothing`
- `Select_RemovesWholeOldUnits_AndKeepsSystemProtectedTailAndCurrentUser`
- `Select_NeverRemovesCurrentUserTurn_EvenWithAggressiveTarget`
- `Select_NeverSplitsToolExchangeFromItsCall`
- `Fingerprint_IsDeterministic_AndDependsOnlyOnIdentityAndVersions`

`ContextCapacityArithmeticTests.cs` 覆盖 8 条用例：
- `Resolve_MatchesRecordedIncidentEffectiveLimit`
- `Classify_ReplaysRecordedIncidentSamples`
- `Classify_HardBoundaryIsIndependentOfSoftTriggerRatio`
- `Classify_BelowTriggerDoesNothing`
- `Resolve_ProviderInputLimitWinsAndIsReported`
- `Resolve_FallbackOutputBudgetIsNotReportedAsConfiguredBudget`
- （其余 2 条同类用例略）

`ContextNetGainAdmissionTests.cs` 覆盖 5 条用例：
- `Evaluate_UsesRealSummaryTokens_ForProjectedAfter`
- `Evaluate_ProjectedAfterIsLargerThanPlanEstimateExcludingSummary`
- `Evaluate_EmptyOrNonShrinkingSummaryIsNoGain`
- `Evaluate_SmallReclaimThatStillExceedsHardLimitIsRejected`
- `Evaluate_AcceptedButBelowTargetIsRecordedHonestly`

`PrefixCompactionPolicyTests.cs` 覆盖 4 条用例：
- `TokenAndBytePressureIndependentlyDriveStages`
- `CacheEpochCooldownNeverSuppressesCapacityProtection`
- `RegisteredWindowScalesThresholdsAndSmallerInputLimitStillProtects`
- `AppendedUserAndToolMessagesArePreservedButAnyFrozenMutationIsRejected`
