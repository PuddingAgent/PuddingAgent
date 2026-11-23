# PuddingSshTests
> 源指纹: *.cs=26-ff9f94a4efcc, *.csproj=1-711d3be16866 · 条目数: 0 · 最近整理: 2026-11-23

测试工程，引用 PuddingSsh；用例全部为进程内单测，不需要真实宿主或外部服务。

`ComponentBoundaryTests.cs` 覆盖 5 条用例：
- `Boundary_Detector_Flags_Forbidden_Names`
- `Test_Process_Must_Not_Load_Forbidden_Assemblies`
- `Test_Dependency_Closure_Must_ContainComponent_AndNoForbiddenAssemblies`
- `Component_Assembly_References_Must_StayBclPlusSshNet`
- `TestProject_MustOnlyReferenceTheComponent`

`SshBoundedOutputCollectorTests.cs` 覆盖 6 条用例：
- `CaptureIsBounded_WhileReceivedBytesKeepCounting`
- `BudgetIsSharedBetweenStdoutAndStderr`
- `Truncation_DoesNotSplitCharacters`
- `MultibyteCharacters_SurviveBytewiseReads`
- `EmptyStreams_ProduceEmptyResults`
- `BudgetMustBePositive`

`SshCloseBudgetContractTests.cs` 覆盖 2 条用例：
- `RealSession_AbortThenDisposeWithBudget_AppliesTheConfiguredBudget`
- `Coordinator_NarrowedCleanupGrace_ReachesRealSessionClose`

`SshCommandAndRequestTests.cs` 覆盖 13 条用例：
- `Command_WithoutCwd_IsPassedThroughVerbatim`
- `Cwd_IsSingleQuoted_AndCdFailureStopsTheCommand`
- `RequestedCwd_WinsOver_DefaultCwd_AndIsFlaggedWhenInherited`
- `Quote_EncodesPerPosixSingleQuoteRules`
- `SingleQuoteInCwd_CannotEscapeTheWrapper`
- `InvalidCwd_IsRejected`
- （其余 7 条同类用例略）

`SshFailureClassifierTests.cs` 覆盖 11 条用例：
- `ComponentException_KeepsItsStableCode`
- `HostKeyRejection_IsNeverRetrySafe`
- `Authentication_And_Passphrase_HaveTheirOwnCodes`
- `Timeout_And_Cancellation_AreDistinct`
- `ConnectionFailure_DependsOnWhetherTheCommandWasSubmitted`
- `SocketErrors_SplitDnsFromConnect`
- （其余 5 条同类用例略）

`SshFakes.Transfer.cs` 覆盖 4 条用例：
- `Rename`
- `RenamePosix`
- `Delete`
- `Dispose`

`SshFakes.TransferSession.cs` 覆盖 1 条用例：
- `RequestAbort`

`SshFakes.cs` 覆盖 1 条用例：
- `RequestAbort`

`SshIdentityOwnershipTests.cs` 覆盖 6 条用例：
- `LateConnect_PendingConnectKeepsMaterialReadable_ThenLateSuccessZeroesOnce`
- `LateConnect_LateFailure_ZeroesMaterialOnce`
- `LateConnect_TestEntry_KeepsMaterialReadableUntilConnectSettles`
- `LateSessionDisposeHangs_KeepsMaterialAndReportsPending`
- `LateConnect_RacingWithBusinessReturn_ReleasesIdentityExactlyOnce`
- `LateConnect_NeverExecutesMarkerCommand`

`SshLimitsValidationTests.cs` 覆盖 11 条用例：
- `CoordinatorOptions_ValidBoundaries_AreAccepted`
- `CoordinatorOptions_OutOfRange_AreRejectedWithConfigInvalid`
- `Coordinator_WithInvalidOptions_RefusesToConstruct`
- `OperationLimits_ValidBoundaries_AreAccepted`
- `OperationLimits_OutOfRange_AreRejectedWithConfigInvalid`
- `InvalidHostLimits_HaveZeroSideEffectsOnBothEntries`
- （其余 5 条同类用例略）

`SshOperationCoordinatorFaultMatrixTests.cs` 覆盖 35 条用例：
- `ReleaseAll`
- `ReleaseOne`
- `PerHostLimit_ThirdOperationWaitsForASlot`
- `HostGate_IsKeyedByRevision_SoAChangedConfigDoesNotShareTheOldGate`
- `WaitQueueFull_ReturnsBusyWithoutConnecting`
- `QueuedOperation_CallerCancellation_ReturnsCancelledAndNeverConnects`
- （其余 29 条同类用例略）

`SshOperationLifetimeFenceTests.cs` 覆盖 13 条用例：
- `PreCancelled_Execute_HasZeroSideEffects`
- `PreCancelled_Test_HasZeroSideEffects`
- `PreExpiredHostDeadline_Execute_HasZeroSideEffects`
- `Identity_IgnoringToken_IsBounded_AndLateSuccessNeverConnects`
- `Identity_LateFailure_IsObserved_AndNeverConnects`
- `Connect_IgnoringToken_IsBounded_AndLateSessionIsRecycledWithoutExec`
- （其余 7 条同类用例略）

`SshOutputIntegrityTests.cs` 覆盖 15 条用例：
- `NormalEndOfStream_IsComplete`
- `ReadFailure_IsNotReportedAsEndOfStream`
- `AbortedStream_IsReportedAsAborted_NotAsFailure`
- `BudgetTruncation_StillCountsAsCompleteOutput`
- `ExitZeroWithCompleteOutput_IsCompleted`
- `ExitZeroWithIncompleteOutput_IsFailedAndKeepsTheExitCodeFact`
- （其余 9 条同类用例略）

`SshPosixCommandGuardTests.cs` 覆盖 9 条用例：
- `GuardIsAFullLine_BeforeTheVerbatimCommand`
- `GuardUsesLineFeedOnly_NeverCarriageReturn`
- `GuardDoesNotRelyOnSetEorSubshell`
- `CompositeScripts_KeepTheGuardOnItsOwnLine_AndTheOriginalBodyIntact`
- `CwdWordIsStillSingleQuoted_AndCwdIsReported`
- `WithoutCwd_OriginalScriptIsUntouched`
- （其余 3 条同类用例略）

`SshResourceAccountingTests.cs` 覆盖 7 条用例：
- `QuarantineCapacity_IsHardLimit_EvenForAlreadyQueuedRequests`
- `QuarantineCapacityZero_DisablesTransfer_ButKeepsNormalRequestsWorking`
- `OrphanExec_StaysAccountedAfterBusinessReturn_AndStopReportsPending`
- `StageWorkerPool_ConcurrentBarrier_PeakNeverExceedsCapacity`
- `StageWorkerPool_SyncBlockingWork_HoldsTokenUntilItEnds`
- `CloseCapacityExhausted_RefusesNewResourceCreation`
- （其余 1 条同类用例略）

`SshResourceLeaseTests.cs` 覆盖 12 条用例：
- `SynchronouslyBlockingConnect_DoesNotBlockTheBusinessWait`
- `StageWorkerPoolFull_ReturnsBusyWithoutConnecting`
- `SessionThatNeverCloses_IsQuarantined_AndReportedAsPending`
- `QuarantineFull_RefusesNewWorkWithBusy`
- `QuarantineDrains_WhenTheResourceFinallyEnds`
- `RequestAbort`
- （其余 6 条同类用例略）

`SshSyncEntryBoundaryTests.cs` 覆盖 12 条用例：
- `SynchronouslyBlockingExec_IsBounded_AndNeverReportsCompleted`
- `SynchronouslyBlockingDispose_IsBounded_OnBothEntries`
- `BlockingCancellationCallback_DoesNotBlockStopAsync`
- `CleanupBudget_IsIndependentOfOperationDeadline`
- `LateExecSuccessWithinGrace_DoesNotOverwriteLocalTermination`
- `CleanupState_RequiresCloseEvidence_AndFaultedDisposeIsNotCompleted`
- （其余 6 条同类用例略）

`SshTransferCoordinatorTests.cs` 覆盖 12 条用例：
- `Setup`
- `Cleanup`
- `Transfer_WithoutHostTransferLimits_IsUnavailable_WithZeroSideEffects`
- `Upload_HappyPath_CompletesAndReleasesEverything`
- `Download_HappyPath_UsesDownloadPlanAndRoot`
- `Upload_LocalPathOutsideRoot_IsLocalPathInvalid_WithZeroSideEffects`
- （其余 6 条同类用例略）

`SshTransferLifecycleStageTests.cs` 覆盖 7 条用例：
- `Setup`
- `Cleanup`
- `Upload_CapabilityProbeHanging_IsBoundedAndNeverCompletes`
- `Upload_CapabilityProbeThrows_IsStructuredFailureWithoutEscape`
- `Download_DoesNotProbeCapabilities`
- `Upload_Cancelled_MergesLateResidueEvidence_AndKeepsTermination`
- （其余 1 条同类用例略）

`SshTransferOwnershipAndLimitTests.cs` 覆盖 7 条用例：
- `Setup`
- `Cleanup`
- `Upload_ExclusiveCreateLosesRace_IsRemotePathExists_AndNeverDeletesOtherClientsTarget`
- `Upload_CreateOutcomeUnknown_DoesNotDeleteTheFile`
- `Upload_LocalSourceOpenFailure_LeavesRemoteUntouched`
- `Download_RemoteGrowsAfterStat_AbortsAtRuntimeLimit`
- （其余 1 条同类用例略）

`SshTransferSessionTests.cs` 覆盖 19 条用例：
- `Setup`
- `Cleanup`
- `DisposeWithBudget_RunsAfterInFlightTransferCleansUp_SoNoPartialTargetSurvives`
- `Capabilities_AreMeasuredOnceAndCached`
- `Capabilities_UnsupportedServer_ReportsAllFalse`
- `Upload_NoOverwrite_ReservesTargetAtomically_WithoutTempFile`
- （其余 13 条同类用例略）

`SshTransferValidationTests.cs` 覆盖 22 条用例：
- `TransferLimits_DefaultAndHardCaps_AreTheDesignValues`
- `TransferLimits_OutOfRange_AreRejectedWithConfigInvalid`
- `TransferLimits_InvalidRemoteRoot_IsRejected`
- `TransferLimits_FileSystemRoot_IsNotAValidBoundary`
- `TransferLimits_LocalRootMustBeAbsolute`
- `RequestBytes_AboveHardCap_AreRejected_AndNarrowingIsHonoured`
- （其余 16 条同类用例略）

`SshTrustSnapshotRealFixtureTests.cs` 覆盖 10 条用例：
- `PlainFixture_AllFourTokens_AreTrusted`
- `HashedFixture_AllFourTokens_AreTrusted`
- `RsaRecord_ForSameHost_IsAlsoTrusted`
- `OurSha256Fingerprint_EqualsOpenSshOwn`
- `OpenSshItselfFindsEveryFixtureToken`
- `RevokedSameKey_IsRejectedAsRevoked`
- （其余 4 条同类用例略）

`SshValidationTests.cs` 覆盖 11 条用例：
- `ForTarget_UsesOpenSshTokenSyntax`
- `ForTarget_RejectsEmptyHostAndBadPort`
- `ParsesPlainCommaSeparatedAndHashedRecords`
- `ParsesRevokedMarker`
- `UnsupportedOrMalformedContent_FailsClosed`
- `EmptyOrCommentOnlyFile_IsUsableButTrustsNothing`
- （其余 5 条同类用例略）
