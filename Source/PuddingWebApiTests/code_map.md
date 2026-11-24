# PuddingWebApiTests
> 源指纹: *.cs=28-b232c1cdb0e8, *.csproj=1-bc0c1a44ca61 · 条目数: 0 · 最近整理: 2026-11-23

测试工程，引用 PuddingAgent；用例全部为进程内单测，不需要真实宿主或外部服务。

`AgentChatApiControllerTests.cs` 覆盖 23 条用例：
- `TestInit`
- `TestCleanup`
- `AgentStatusEndpoint_ReturnsWorkspaceAgentProjectionShape`
- `AgentStatusEndpoint_MapsActiveMainSessionWithoutRunningEventsToIdle`
- `AgentStatusEndpoint_MapsUnfinishedExecutionEventsToRunning`
- `AgentStatusEndpoint_MapsStaleUnfinishedExecutionEventsToIdle`
- （其余 17 条同类用例略）

`ApprovalControllerAuthTests.cs` 覆盖 4 条用例：
- `TestInit`
- `TestCleanup`
- `ApprovalEndpoints_RequireAuthentication`
- `ApprovalEndpoints_DoNotRejectAuthenticatedCallers`

`ApprovalControllerFunctionalTests.cs` 覆盖 11 条用例：
- `TestInit`
- `TestCleanup`
- `Pending_Is_Reachable_For_Authenticated_Caller`
- `Confirm_With_Correct_Code_Succeeds_And_Resolves_The_Record`
- `Confirm_With_Wrong_Code_Is_Rejected_And_Leaves_Record_Pending`
- `Confirm_With_Right_Code_On_Unknown_Approval_Is_Rejected`
- （其余 5 条同类用例略）

`BootstrapApiControllerTests.cs` 覆盖 1 条用例：
- `Complete_CreatesAdminProviderAndDefaultModel`

`BuildIdentityApiE2ETests.cs` 覆盖 1 条用例：
- `BuildIdentity_IsAnonymousAndReportsLoadedCoreAssemblies`

`BuiltInAgentTemplateToolIdTests.cs` 覆盖 1 条用例：
- `PuddingAgentBuiltInTemplates_Use_Registered_Runtime_Tool_Ids`

`ChatCommandContractTests.cs` 覆盖 8 条用例：
- `TestInit`
- `TestCleanup`
- `SubmitTurn_ReturnsCanonicalAcceptanceContract`
- `SubmitTurn_DifferentRequests_CreateDifferentCommands`
- `SubmitTurn_PreservesRouteConversationId`
- `SubmitTurn_SameClientRequestId_IsIdempotent`
- （其余 2 条同类用例略）

`ConversationSteeringApiTests.cs` 覆盖 4 条用例：
- `TestInitialize`
- `TestCleanup`
- `SteeringEndpoint_AcceptsRunningTurn_AndCreatesRuntimeQueueItem`
- `SteeringEndpoint_RejectsTerminalTurn_WithoutCreatingQueueItem`

`CustomWebApplicationFactory.cs` 覆盖 2 条用例：
- `StartAsync`
- `StopAsync`

`FakeLlmControllerTests.cs` 覆盖 5 条用例：
- `ChatCompletions_ReturnsOpenAiCompatibleResponse`
- `ChatCompletions_Stream_ReturnsSseDeltasAndDone`
- `ChatCompletions_WithTools_ReturnsToolCall`
- `ChatCompletions_AfterToolResult_ReturnsTextResponse`
- `ChatCompletions_WithTools_Stream_ReturnsSseToolCallDeltas`

`GoalApiContractTests.cs` 覆盖 9 条用例：
- `TestInit`
- `TestCleanup`
- `GoalCommands_Create_Pause_Resume_Cancel_Lifecycle`
- `GoalCommands_Replay_With_Same_ClientRequestId_Returns_First_Goal`
- `GoalCommands_Conflict_When_NonTerminal_Goal_Exists`
- `GoalCommands_Rejects_Out_Of_Range_Rounds`
- （其余 3 条同类用例略）

`GoalStepsApiTests.cs` 覆盖 7 条用例：
- `TestInit`
- `TestCleanup`
- `Steps_Are_Sorted_By_SequenceNo_With_Progress_And_Evidence`
- `CurrentStepId_Matches_GoalRunStore_Selection`
- `NoPlan_Returns_200_With_Empty_Projection`
- `Unknown_Goal_Returns_404_GoalNotFound`
- （其余 1 条同类用例略）

`InMemoryApprovalServiceTests.cs` 覆盖 4 条用例：
- `Active_Record_Is_Listed_And_Retrievable`
- `Expired_Record_Is_Pruned_On_Query_And_No_Longer_Retrievable`
- `Expired_Record_Cannot_Be_Confirmed_Even_With_Correct_Code`
- `Write_Path_Also_Prunes_Expired_Records`

`InMemorySessionRepositoryTests.cs` 覆盖 7 条用例：
- `RebindApi_LockedFileReturnsCorrelatedSafeStorageDiagnostics`
- `FailedCreate_DoesNotPublishUnpersistedSession`
- `FailedUpdateAndDelete_PreserveMemoryAndDisk`
- `Rebind_PartialDemotionFailureMatchesRestartAndCanResume`
- `CreateAsync_ShouldPersistSessionToDataRoot`
- `RepositoriesUsingDifferentDataRoots_ShouldNotShareSessions`
- （其余 1 条同类用例略）

`MemoryLibraryAdminApiControllerTests.cs` 覆盖 18 条用例：
- `GetAgentOverview_EmptyAgentId_Returns400`
- `GetAgentLibraries_EmptyAgentId_Returns400`
- `EnsureAgentDefaultLibrary_EmptyAgentId_Returns400`
- `GetAgentTree_EmptyAgentId_Returns400`
- `GetAgentBook_EmptyAgentId_Returns400`
- `AgentSearch_EmptyAgentId_Returns400`
- （其余 12 条同类用例略）

`MessageApiControllerTests.cs` 覆盖 13 条用例：
- `TestInit`
- `TestCleanup`
- `ListMessages_Returns200_WithPagination`
- `ListMessages_EmptySession_ReturnsEmptyList`
- `ListMessages_LimitExceedsMax_ClampsToMax`
- `ListMessages_WhenMaterializedMessagesExist_DoesNotUseEventFallback`
- （其余 7 条同类用例略）

`SessionApiControllerTests.cs` 覆盖 21 条用例：
- `TestInit`
- `TestCleanup`
- `CreateSession_Returns201_WithValidData`
- `CreateSession_Returns400_WhenMissingRequiredFields`
- `CreateSession_Returns401_WithoutAuth`
- `EnsureMainSession_ReturnsWorkspaceAgentMainSession`
- （其余 15 条同类用例略）

`SessionApprovalDecideTests.cs` 覆盖 9 条用例：
- `TestInit`
- `TestCleanup`
- `Decide_Is_Reachable_For_Authenticated_Caller`
- `Decide_Without_Any_Approval_Request_Returns_Approval_Not_Found`
- `Decide_With_Unknown_Decision_Value_Is_Rejected`
- `Decide_Without_ApprovalId_Is_Rejected`
- （其余 3 条同类用例略）

`SessionEventsControllerTests.cs` 覆盖 13 条用例：
- `TestInit`
- `TestCleanup`
- `EventsStream_Returns404_WhenSessionNotFound`
- `EventsStream_Returns410_WhenSessionIsFrozen`
- `EventsStream_Returns404_Not500_ForUnknownSession`
- `EventsStream_WithExplicitZeroCursor_ReplaysHistoryAndMarksFramesAsReplay`
- （其余 7 条同类用例略）

`SessionSteeringSchemaBootstrapperTests.cs` 覆盖 1 条用例：
- `EnsureCreatedAsync_AddsTargetTurn_AndExpiresLegacyPendingRows`

`SubAgentDelegationSettingsStoreTests.cs` 覆盖 7 条用例：
- `TestInit`
- `TestCleanup`
- `Validate_Rejects_OutOfRange_And_Inconsistent_Combinations`
- `UpdateAsync_PreservesUnrelatedKeys_And_WritesOnlyTargetSection`
- `UpdateAsync_RejectsInvalidValue_WithoutTouchingTheFile`
- `WrittenValue_IsObservedByAReloadOnChangeConfigurationRoot`
- （其余 1 条同类用例略）

`SubAgentPoolAdminControllerTests.cs` 覆盖 3 条用例：
- `Get_EmptyPool_Returns200WithEmptyArray_Not404`
- `Get_ReturnsEntries_MapsEveryField_ExcludesDeleted_AndOrdersByLastUsedDesc`
- `Controller_IsReadOnly_AndRequiresAdminJwt`

`SubAgentPoolAdminEndpointTests.cs` 覆盖 5 条用例：
- `TestInit`
- `TestCleanup`
- `Get_WithoutToken_Returns401`
- `Get_WithAdminJwt_Returns200AndJsonArray`
- `Get_WithSeededEntry_MapsContractFieldsToCamelCase`

`SubconsciousDebugApiControllerTests.cs` 覆盖 14 条用例：
- `GetDebug_ShouldReturnRuntimeControlSnapshot`
- `Stop_ShouldForwardReasonAndRequestedBy`
- `Start_ShouldForwardReasonAndRequestedBy`
- `Trigger_ShouldEnqueueDebugConsolidationJob`
- `Trigger_ShouldReturnNotFoundWhenDebugApiDisabled`
- `TriggerSessionCompressedHook_ShouldPublishHookEvent`
- （其余 8 条同类用例略）

`Tools/AsrToolTests.cs` 覆盖 2 条用例：
- `ExecuteAsync_AuthorizedWorkspaceArtifact_InvokesTranscription`
- `ExecuteAsync_UnrelatedAbsolutePath_IsRejected`

`Tools/ImageReaderPreprocessingTests.cs` 覆盖 10 条用例：
- `MetadataNeedsNoVisionAndReturnsActualFormatWithoutImageContent`
- `LowCreatesSmallPixelsAndRepeatedReadReusesArtifact`
- `LargePreviewThenTwoOriginalRegionsPreservesIndependentDetails`
- `PrepareCombinesCropRotationGrayAndEncodingWithoutImageThenCanRead`
- `DenoiseSoftensColorBoundaryOnlyWhenRequested`
- `DetailIsCarriedInTypedToolResult`
- （其余 4 条同类用例略）

`Tools/ImageReaderToolTests.cs` 覆盖 15 条用例：
- `VisionCaller_ReturnsNativeImageToolParts`
- `TextCaller_WithHelperConfigured_StillFailsWithoutDelegation`
- `ToolSchema_DoesNotOfferModelDelegationModes`
- `Native_OnNonResponsesProtocol_ReturnsToolOutputNotSupported`
- `ArtifactReference_ReusesWorkspaceArtifactByContentHash`
- `ArtifactReference_FromAnotherWorkspace_IsRejectedAsForbidden`
- （其余 9 条同类用例略）

`UsageSummaryEndpointTests.cs` 覆盖 10 条用例：
- `TestInit`
- `TestCleanup`
- `GroupByModel_AggregatesColumnsAndUsesWeightedCacheHitRate`
- `TimeRange_ExcludesRowsOutsideHalfOpenBounds`
- `GroupByDay_SplitsByUtcDateAndOrdersAscending`
- `EmptyWindow_Returns200WithEmptyRows`
- （其余 4 条同类用例略）
