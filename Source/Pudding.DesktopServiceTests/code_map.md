# Pudding.DesktopServiceTests
> 源指纹: *.cs=66-77009685c24e, *.csproj=1-380d253dd542 · 条目数: 0 · 最近整理: 2026-11-23

测试工程，引用 Pudding.DesktopService、PuddingBrowser.Authorization；用例全部为进程内单测，不需要真实宿主或外部服务。

`AgentTargetAdmissionTests.cs` 覆盖 9 条用例：
- `SiblingPage_InTheSameAgentAuthorizedContext_IsDenied`
- `TheDesignatedAgentTarget_IsAllowed`
- `MovingTheAgentTarget_MovesTheGate`
- `ClearingTheAgentTarget_DeniesPreviouslyAllowedCapabilities`
- `GeneralPurposeBrowsing_IsNotGatedByTheAgentTarget`
- `ContextScopedBrowserCapabilities_AreUnaffected`
- （其余 3 条同类用例略）

`AutomationDriverPolicyTests.cs` 覆盖 4 条用例：
- `ArrivalStepDelegatesToThePurePolicy`
- `ArrivalStepStopsWithAReasonInsteadOfScrollingForever`
- `ToRefStepDelegatesToThePurePolicy`
- `TheNullPolicyRefusesArrivalScrolling`

`BrowserAdmissionAdapterTests.cs` 覆盖 3 条用例：
- `AdmitIsRefusedWithoutAGrantAndAcceptedWithOne`
- `RevalidateSeesARevokedGrantAndRefuses`
- `ReleaseIsSafeForNullAndForRepeatedCalls`

`BrowserAdmissionGateTests.cs` 覆盖 2 条用例：
- `WithoutAGrantTheCapabilityIsRefusedBeforeAnyDriverCall`
- `WithAGrantTheCapabilityReachesTheDriverExactlyOnce`

`BrowserAutomationAuthorityGenerationTests.cs` 覆盖 1 条用例：
- `TheLiveGenerationIsReadableAndFollowsTheNotifiedHandshake`

`BrowserContextScopeAdmissionTests.cs` 覆盖 4 条用例：
- `DefaultProductOptions_SeparateTheTwoCallerTrustSources`
- `BrowserContextScope_HasASingleDefinition`
- `DefaultProductOptions_BrowserContexts_PassTheTrustGate_WhileShellDialogStillDoesNot`
- `ExplicitShellTrustOverride_StillAppliesToShellCapabilitiesOnly`

`BrowserControlPlaneEndToEndTests.cs` 覆盖 5 条用例：
- `ApplyingABindingForTheUserSelectedPageIsAcknowledgedWithAGrant`
- `ApplyingABindingWithoutAnExplicitSelectionInAnAgentAuthorizedContextDefaultsToThePage`
- `ApplyingABindingInANonAgentAuthorizedContextIsRefusedEvenWithoutSelection`
- `ReleasingADefaultedTargetDoesNotBlockALaterBinding`
- `RevokingIsAcknowledgedAndIdempotent`

`BrowserDefaultAgentGrantWiringTests.cs` 覆盖 3 条用例：
- `Screenshot_WithoutUserSelectedTarget_IsAdmittedByTheDefaultGrant`
- `Screenshot_WithoutUserSelectedTarget_IsStillRejectedForASubAgent`
- `TabsNew_WithoutAnyPriorGrant_GetsADefaultManagementGrant`

`BrowserDefaultManagementGrantPolicyTests.cs` 覆盖 7 条用例：
- `NoGrantMeansIssue`
- `SameCallerDifferentRunStillCounts`
- `ExpiredGrantMeansIssue`
- `GrantWithoutExpiryNeverExpires`
- `AnotherCallerDoesNotCount`
- `DifferentCallerIdButSameTaskIdIsSameScope`
- （其余 1 条同类用例略）

`BrowserDefaultPageGrantPolicyTests.cs` 覆盖 7 条用例：
- `NoGrantMeansIssue`
- `SameActorAndLiveMeansSkip`
- `SameCallerButDifferentRunMustIssueAgain`
- `ExpiredGrantMeansIssue`
- `GrantWithoutExpiryNeverExpires`
- `GrantForAnotherPageDoesNotCount`
- （其余 1 条同类用例略）

`BrowserDelegationPortContractTests.cs` 覆盖 3 条用例：
- `DeniedGrantIsNotGrantedAndMustCarryAReason`
- `GrantedGrantCarriesAnIdentifierAndADeadline`
- `RequestCarriesTheTrustedParentAndTheRealChildRun`

`BrowserExecutionPermitTests.cs` 覆盖 4 条用例：
- `TakeoverThenResumeWhileQueuedMustNotReviveTheOldOperation`
- `CancellationBeforeQueueMustNotLeakTheAdmittedLease`
- `VersionRejectionMustNotLeakTheAdmittedLease`
- `SelectingAPageMustNotAuthorizeEveryUnrelatedTask`

`BrowserGrantIssuerTests.cs` 覆盖 11 条用例：
- `ATrustedAgentGetsAPageGrantBoundToThisDesktopAndGeneration`
- `AnUnregisteredTargetIsRefused`
- `AValidityBeyondTheDesktopLimitIsRefusedInsteadOfBeingClamped`
- `AnEmptyOrNegativeValidityIsRefused`
- `ASubAgentCannotExceedItsParentsScope`
- `ADeadConnectionGenerationIsRefused`
- （其余 5 条同类用例略）

`BrowserLeaseLifecycleTests.cs` 覆盖 1 条用例：
- `AWriteOperationReleasesItsLeaseSoTheNextWriteStillWorks`

`BrowserLedgerNamespaceTests.cs` 覆盖 1 条用例：
- `NamespaceCarriesTheProcessInstanceAndFollowsTheConnectionGeneration`

`BrowserLifecycleTests.cs` 覆盖 13 条用例：
- `ClosePage_ClearsBothRegistries_AndRevokesTheGrant`
- `ClosingAPageRevokesMoreThanSwitchingTheAgentTarget`
- `ClosePage_ReleasesTheWriteLeaseHeldForIt`
- `ClosePage_IsIdempotent_AndStillRevokesStaleAuthorization`
- `CloseContext_ClearsEveryPageAndItsAuthorization`
- `CreateThroughThePort_RegistersBothRegistries`
- （其余 7 条同类用例略）

`BrowserOperationFailureMappingTests.cs` 覆盖 2 条用例：
- `DriverCodesMapToStableSemantics`
- `UnregisteredDriverCodeIsInternalAndDoesNotLeakDriverMessage`

`BrowserOperationLedgerTests.cs` 覆盖 9 条用例：
- `Advance`
- `FirstBegin_WinsAndExecutes`
- `SameFingerprintWhilePending_AwaitsTheSameCompletionSource`
- `SameFingerprintAfterCompletion_ReplaysTheSameTerminal`
- `DifferentFingerprint_ConflictsAndDoesNotLeakTheOriginalResult`
- `DifferentConnectionGeneration_IsANewNamespace`
- （其余 3 条同类用例略）

`BrowserPageScopedBaselineTests.cs` 覆盖 5 条用例：
- `BridgeLivenessAlone_DoesNotAuthorizeAPage`
- `UserSelectedTargetPage_IsAuthorizedWithoutAnyGrant`
- `SwitchingTheTarget_RevokesTheOldPageBaseline`
- `RevokingTheTarget_RemovesTheBaseline`
- `APageGrantStillAuthorizesANonTargetPage`

`BrowserParentChildSamePageSerializationTests.cs` 覆盖 1 条用例：
- `AChildWriteIsRefusedWhileTheParentWriteOnTheSamePageIsInFlight`

`BrowserReadOnlyBaselineAdmissionTests.cs` 覆盖 4 条用例：
- `Screenshot_ReachesTheSurface_WithOnlyTheUserSelectedTargetPage`
- `Screenshot_WithoutAUserSelectedTarget_IsRejectedBeforeTheSurface`
- `MutatingCapability_CannotBorrowTheReadOnlyBaseline`
- `SessionPermitMarker_SkipsTheLegacyAuthorityAdmissionForWrite`

`BrowserReadingDiffResolverTests.cs` 覆盖 7 条用例：
- `WithoutSinceRevision_ItOnlyReportsAndRemembers`
- `SameContent_ReportsNoChange_AndDifferentContent_ReportsTheDiff`
- `MalformedRevision_IsAnArgumentError_NotAChange`
- `IncomparableRevision_SaysRefreshRequired_InsteadOfInventingAChange`
- `MissingOrEvictedBaseline_SaysRefreshRequired_AndStillRemembersThisRead`
- `PageWithoutLiveVersion_CannotCompare`
- （其余 1 条同类用例略）

`BrowserRevalidateBeforeTouchingTests.cs` 覆盖 1 条用例：
- `RevokingWhileQueuedIsRefusedBeforeAnyDriverCall`

`BrowserSessionAdmissionTests.cs` 覆盖 8 条用例：
- `HandoverIsAdmittedWithoutAnExplicitClaimUnderV1`
- `AnotherOwnerCannotUseOrClaimTheHandedOverPage`
- `PauseBlocksWritesButKeepsReadsAndCancelsTheInFlightWrite`
- `TargetSwitchRevokesTheOldPermitsButTheNextCommandRebinds`
- `ClosingTheTargetPageRevokesTheSession`
- `BridgeLossRevokesTheSession`
- （其余 2 条同类用例略）

`BrowserSessionPermitMarkerWireGuardTests.cs` 覆盖 1 条用例：
- `SessionPermitMarker_IsNeverReferencedByWireOrProtocolMapping`

`BrowserSmokePlanTests.cs` 覆盖 12 条用例：
- `TheCanonicalPlanIsValidAndCoversEveryGate`
- `EveryCheckStatesAnObservationAndAnExpectation`
- `AMissingGateMakesThePlanIncomplete`
- `DuplicateIdsAndMissingFieldsAreRefused`
- `ACheckThatNeedsNoRealDeviceMustNameItsAutomatedCoverage`
- `TheEventReportingCheckStatesTheRealBoundFromTheCode`
- （其余 6 条同类用例略）

`BrowserSubAgentCascadeReleaseTests.cs` 覆盖 3 条用例：
- `RevokingTheParentGrantStopsTheChildFromWriting`
- `EndingTheSubTaskReleasesItsPermission`
- `ANewConnectionGenerationStopsBothParentAndChild`

`BrowserSubAgentGrantSubsetTests.cs` 覆盖 4 条用例：
- `ASubAgentWithoutARealParentGrantIsRefused`
- `ASubAgentAskingForMoreThanTheParentGrantIsRefused`
- `ASubAgentWithinTheParentGrantIsAccepted`
- `ASubAgentGrantIsClampedToTheParentsRemainingTime`

`BrowserSubAgentTimeoutAndCancelTests.cs` 覆盖 3 条用例：
- `AnExpiredGrantStopsTheChildFromWriting`
- `APreCancelledWriteNeverReachesTheDriver`
- `CancellingWhileInFlightStillReleasesTheLease`

`BrowserTargetRegistryTests.cs` 覆盖 4 条用例：
- `UnregisteredContextsAreUntrustedAndUnknownPagesAreNotAgentTargets`
- `ActivePageIsReportedAndCanBeCleared`
- `UnregisteringTheActivePageNeverLeavesAStaleActivePage`
- `ClearResetsEverything`

`BrowserTaskBindingServiceTests.cs` 覆盖 5 条用例：
- `ABindingForAPageTheUserDidNotSelectIsRefused`
- `ABindingForTheSelectedTargetIsIssuedAndCanBeRevoked`
- `ReacceptingTheSameBindingDoesNotLeaveTwoGrants`
- `SwitchingAwayFromTheTargetRevokesItsBindings`
- `EndingTheTaskRevokesItsBindingsAndLeavesOthersAlone`

`CapabilityDispatchCoverageTests.cs` 覆盖 2 条用例：
- `EveryCatalogCapability_HasADispatchArm`
- `NoDispatchArm_IsImmediatelyFollowedByAnotherCaseLabel`

`ChannelTransportResolverTests.cs` 覆盖 21 条用例：
- `Resolve_MapsEveryEndpointKind`
- `Resolve_RejectsUnsupportedProtocolVersion`
- `Resolve_RejectsMissingEndpoint`
- `ResolveFromText_RoundTripsThePublishedDescription`
- `ResolveFromText_FailsClosedOnMalformedText`
- `ResolvedTransport_ExposesNoCredentials`
- （其余 15 条同类用例略）

`ComponentBoundaryTests.cs` 覆盖 5 条用例：
- `ServiceAssembly_ReferencesOnlyItsDeclaredClosure`
- `TestProcess_DoesNotLoadUiOrServerAssemblies`
- `ServiceCsproj_DeclaresExpectedReferencesAndKeepsItsBoundaryTarget`
- `ServiceTestsCsproj_ReferencesTheComponentAndThePureAuthorizationLeaf`
- `PublicSurface_ExposesNoTransportOrUiTypes`

`DeliveryFactCollectorTests.cs` 覆盖 11 条用例：
- `Dispose`
- `CollectsArtifactsChecksAndUnverifiedItemsFromDisk`
- `MissingDirectoriesAreReportedSpecifically`
- `EmptyPublishOrResultsDirectoriesAreRefusedNotGuessed`
- `AFailingSuiteMakesTheCollectionRefuseToProduceAManifest`
- `UnreadableTrxIsTreatedAsMissingFact`
- （其余 5 条同类用例略）

`DeliveryManifestBuilderTests.cs` 覆盖 7 条用例：
- `BuildsAManifestThatCarriesTheUnverifiedItemsFromTheLog`
- `AdditionalUnverifiedItemsAreMergedAndDeduplicated`
- `RefusesToBuildWithoutTheMinimumFacts`
- `FailingChecksMakeTheBuildRefuseToProduceAManifest`
- `EmptyChangelogsStillProduceAnExplicitKnownUnverifiedField`
- `CustomExternalStepsAreKeptWhenTheyCoverTheLifecycle`
- （其余 1 条同类用例略）

`DeliveryManifestReportTests.cs` 覆盖 7 条用例：
- `ReportCarriesACompleteFrontMatterBlock`
- `ReportCarriesEveryUnverifiedItemVerbatim`
- `ReportStatesExplicitlyWhenThereAreNoUnverifiedItems`
- `ReportCarriesTheFactsAndTheStage`
- `ReportLinksTheDesignDocRelativeToTheReportsDirectory`
- `ReportSaysSoWhenTheUnverifiedListIsTruncated`
- （其余 1 条同类用例略）

`DeliveryManifestTests.cs` 覆盖 14 条用例：
- `ACompleteManifestIsAccepted`
- `TheBuildAndCommitMustBeTraceable`
- `FailingChecksAreNeverDeliverable`
- `ChecksMustHaveActuallyRun`
- `ADirtyWorkingTreeCannotBeDeclaredDeliverable`
- `DirtyTriStateSurvivesTheCanonicalWriterAndReader`
- （其余 8 条同类用例略）

`DesktopAcceptanceTests.cs` 覆盖 6 条用例：
- `ReadyForExternalDeploy_NeedsABuildIdAndAtLeastOneCheck`
- `InProductFunctionalComplete_RequiresANewBuild`
- `InternalAgent_CannotClaimLifecycleVerification`
- `ExternalController_NeedsToActuallyExerciseTheLifecycle`
- `UnregisteredValuesAreRefused`
- `StageNamesMatchTheDeliveryVocabulary`

`DesktopBrowserExecutorCompositionTests.cs` 覆盖 4 条用例：
- `CreateService_ReusesTheLifecycleRegistriesAndAuthority`
- `Executor_ConstructedWithoutAnyCapabilityChannel_IsUsable`
- `Executor_WithoutAuthorityOwnership_DoesNotCloseTheSharedAuthority`
- `Executor_WithAuthorityOwnership_ClosesTheSharedAuthority`

`DesktopCapabilityChannelCompositionTests.cs` 覆盖 12 条用例：
- `DisabledSettings_DoNothingAndReportWhy`
- `NullSettings_AreTreatedAsDisabledNotAsAnError`
- `EnabledButEndpointUnusable_FailsLoudlyInsteadOfStayingSilent`
- `EnabledButEndpointMalformed_FailsInsteadOfFallingBackToAnotherTransport`
- `EnabledWithUsableEndpoint_StartsAndReleasesTheSingleTransportClaim`
- `DisposingTheChannel_DoesNotCloseTheSharedAuthority`
- （其余 6 条同类用例略）

`DesktopCapabilityChannelPreflightTests.cs` 覆盖 4 条用例：
- `DisabledSettingsKeepTheLegacyBridge`
- `MissingOrUnusableDescriptionMeansNoStartAndNoFallback`
- `EnabledSettingsWithAGoodDescriptionStartOnThatTransport`
- `NullSettingsAreTreatedAsDisabled`

`DesktopCapabilityCompositionTests.cs` 覆盖 7 条用例：
- `TwoServicesBuiltFromTheSameLifecycleShareOneAuthority`
- `DisabledChannelLeavesTheSingleAuthorityUntouched`
- `TheChannelCompositionRefusesToStartWithoutTheLifecyclePort`
- `DisabledChannel_KeepsTheLegacyBridgeAndNeverBuildsAHost`
- `DisabledChannelWithNoDescription_StillKeepsTheLegacyBridge`
- `EnabledChannelWithAGoodDescription_PassesBothGatesAndYieldsAStoppedHost`
- （其余 1 条同类用例略）

`DesktopCapabilityHostFactoryTests.cs` 覆盖 4 条用例：
- `DisabledSettingsDoNotProduceAHost`
- `EnabledSettingsProduceAStoppedHost`
- `UnusableHandshakeTimeoutBecomesAnErrorInsteadOfAnException`
- `InvalidDesktopIdIsReportedAsConfigurationError`

`DesktopShellSurfaceTests.cs` 覆盖 10 条用例：
- `ClipboardOverBudget_IsTruncatedAndFlaggedEvenIfTheFacilityIgnoresTheBudget`
- `CanceledDialog_StaysAResultNotAFailure`
- `FacilityFailuresArePropagatedUnchanged`
- `ThrownExceptionsBecomeInternalErrorsWithoutLeakingTheMessage`
- `CancellationPropagatesInsteadOfBeingFoldedIntoAFailure`
- `NotificationNotShown_StaysASuccessfulResult`
- （其余 4 条同类用例略）

`DispatchRaceTests.cs` 覆盖 12 条用例：
- `ThreadAccess_RunsInlineWithoutQueueing`
- `BusyUiThread_QueuesThenRunsOnPump`
- `PageClosedWhileQueued_FailsWithInvalidTarget`
- `PageVersionAdvancedWhileQueued_FailsWithPageVersionMismatch`
- `WindowClosedWhileQueued_CompletesWithUiUnavailable`
- `DeadlineExpiredWhileQueued_FailsWithoutCallingTheSurface`
- （其余 6 条同类用例略）

`DocsMarkdownEncodingTests.cs` 覆盖 3 条用例：
- `DocsMarkdownHasNoBomAndStartsWithFrontMatter`
- `TheBrowserAutomationDeliverablesHaveNoBrokenRelativeLinks`
- `TheB6WiringDecompositionNamesOnlyExistingFilesAndVerifications`

`ExternalAcceptanceFactsTests.cs` 覆盖 7 条用例：
- `ACompleteFactsFileIsParsedIntoAConcludableInput`
- `UnknownFieldsAreRefusedAndAcceptRetriesGetsAnActionableHint`
- `EveryKeyTheTemplateWritesIsActuallyReadByTheParser`
- `UnknownOutcomeWordsAreRefusedInsteadOfTreatedAsPassed`
- `MissingFieldsAreNamedInsteadOfSilentlyDefaulted`
- `InvalidOrEmptyDocumentsAreRefused`
- （其余 1 条同类用例略）

`ExternalAcceptanceSessionTests.cs` 覆盖 9 条用例：
- `RetriedResultsAreRefusedUnlessTheAcceptorAcknowledgesThem`
- `ACompleteExternalRunIsConcluded`
- `NotRunningAnObservationIsNotAPass`
- `AFailedObservationBlocksTheVerdict`
- `PassingWithoutEvidenceIsRefused`
- `ObservationsFromAnotherBuildAreRefused`
- （其余 3 条同类用例略）

`GateEvidenceReportTests.cs` 覆盖 4 条用例：
- `TheBrowserAutomationComponentKeepsItsCompileTimeBoundary`
- `TheKnownFlakyRegistryIsConsultedByTheRetryPath`
- `DocumentedPlanDeviationsStillDescribeTheActualCode`
- `EveryTestMethodNamedInTheGateReportExists`

`HostTests.cs` 覆盖 11 条用例：
- `Start_RunsSupervisorAndForwardsState`
- `Start_WhenLegacyBridgeSelected_RefusesWithoutCreatingSupervisor`
- `Start_WhenAlreadyRunning_Throws`
- `SecondHost_ForTheSameDesktop_IsRejectedUntilTheFirstStops`
- `SingleActiveTransportGuard_CanBeDisabledForDiagnostics`
- `Stop_CancelsTheLoopAndDisposesTheSupervisor_AndIsIdempotent`
- （其余 5 条同类用例略）

`InteractionStateTests.cs` 覆盖 22 条用例：
- `State_Writes_Land_In_The_Single_Authority`
- `State_Reads_The_Authority_Not_A_Private_Flag`
- `Takeover_WrittenDirectlyToTheAuthority_StillBlocksMutatingCalls`
- `Authority_IsRequired_FailClosed`
- `WindowClose_Closes_TheAuthority`
- `Paused_RejectsMutatingCapability_WithPaused`
- （其余 16 条同类用例略）

`KnownUnverifiedExtractorTests.cs` 覆盖 10 条用例：
- `ExtractsTheHonestBoundariesFromChangelogText`
- `DeduplicatesAndPreservesOrder`
- `ReturnsEmptyListRatherThanNull`
- `IgnoresTextThatMerelyMentionsVerificationAsDone`
- `ItemsAreBoundedInLengthAndCount`
- `ExtractsFromTheRealChangelogCorpus`
- （其余 4 条同类用例略）

`PresentationScopeAdmissionTests.cs` 覆盖 6 条用例：
- `DefaultProductOptions_SeparateTheThreeCallerTrustSources`
- `PresentationScope_HasASingleDefinition`
- `DefaultProductOptions_PanelRender_PassTheTrustGate_WhileClipboardStillDoesNot`
- `DefaultProductOptions_DocumentPreview_PassTheTrustGate`
- `PresentationTrust_CanStillBeMadeFailClosed`
- `PresentationCapabilities_AreNeverOpenToUntrustedTargets`

`RegistryVersionRecordingTests.cs` 覆盖 12 条用例：
- `Snapshot_RecordsTheObservedVersion`
- `Locate_RecordsTheObservedVersion`
- `PageState_RecordsTheObservedVersion`
- `Navigate_RecordsTheObservedVersion`
- `WaitFor_RecordsTheObservedVersion`
- `Interact_RecordsTheAdvancedVersion`
- （其余 6 条同类用例略）

`RunbookCommandTests.cs` 覆盖 1 条用例：
- `EverySwitchMentionedInTheRunbooksExistsInItsScript`

`ScreenshotDispatchTests.cs` 覆盖 1 条用例：
- `Screenshot_DispatchesToTheScreenshotSurface_NotTheSnapshotPath`

`ShellStatusTests.cs` 覆盖 5 条用例：
- `ShellStatus_ComesFromTheSurfaceAndIsEnrichedByTheService`
- `ShellStatus_ReportsPausedAndUserTakeoverStates`
- `ShellStatus_PropagatesSurfaceFailureWithoutInventingData`
- `ShellStatus_RequiresItsPayloadAndAcceptsAnUntrustedCaller`
- `ShellStatus_IsRejectedWhenNotAllowed`

`SmokePlanCoverageClaimTests.cs` 覆盖 2 条用例：
- `EveryNumberOfThePlanIsTraceableToItsSourceOfTruth`
- `EveryAutomatedCoverageClaimResolvesToARealTestClass`

`SnapshotTests.cs` 覆盖 24 条用例：
- `Snapshot_OnAgentAuthorizedPage_ReachesTheSurfaceWithTheBudget`
- `Snapshot_IsDeniedOnUntrustedPages`
- `Snapshot_RequiresItsPayloadAndAValidBudget`
- `Snapshot_PropagatesPageVersionMismatchFromTheSurface`
- `Budgets_AreEnforcedByTheServiceEvenIfTheSurfaceIgnoresThem`
- `Interaction_ThatDoesNotAdvanceTheVersion_IsAcceptedWithTheObservedVersion`
- （其余 18 条同类用例略）

`SurfaceAndAdmissionTests.cs` 覆盖 17 条用例：
- `SurfaceDomainFailure_PassesThroughUnchanged`
- `SurfaceException_MapsToInternalErrorWithoutLeakingItsMessage`
- `SurfaceBrowserOperationException_KeepsItsStableCodeAndMessage`
- `SurfaceUnknownBrowserOperationCode_ReportsCodeButNotPageText`
- `GetPageState_RoutesThroughTheUiDispatcher`
- `GetPageState_OnUnknownTarget_IsRejected`
- （其余 11 条同类用例略）

`TargetAndPolicyTests.cs` 覆盖 32 条用例：
- `Navigate_OnUnregisteredTarget_IsRejectedWithInvalidTarget`
- `Navigate_OnClosedPage_IsRejectedWithInvalidTarget`
- `Navigate_IsAllowedOnEveryTrustLevel`
- `JavaScript_IsDeniedOnUntrustedAndWorkbenchTargets`
- `JavaScript_IsAllowedOnAuthorizedAgentTarget`
- `ExpectedPageVersionMismatch_IsRejectedBeforeDispatch`
- （其余 26 条同类用例略）

`TestInfrastructure.cs` 覆盖 6 条用例：
- `Advance`
- `InvokeAsync`
- `Publish`
- `Release`
- `RunAsync`
- `Send`

`TestSiteFixtureTests.cs` 覆盖 8 条用例：
- `TheChineseSerpFixtureHasExactlyTenResultsWithFixedTitleLength`
- `TheEnglishSerpFixtureHasExactlyTenResultsWithFixedTitleLength`
- `TheArticleFixtureCoversHeadingListTableCodeAndLink`
- `TheScrollFixtureHasNestedContainerAndABoundedInfiniteList`
- `TheEventsFixtureCanTriggerEveryPeripheralCase`
- `TheRunbookOnlyReferencesRoutesThatExistAndStatesTheExpectedNumbers`
- （其余 2 条同类用例略）

`TrxTestResultParserTests.cs` 覆盖 5 条用例：
- `ReadsPassedAndFailedCountersFromATrx`
- `MissingOrMalformedResultsAreRefusedInsteadOfGuessed`
- `ZeroCountsAreReportedAsTheyAre`
- `AllOrNothingAcrossSuites`
- `ParsedChecksFeedTheDeliveryBuilder`
