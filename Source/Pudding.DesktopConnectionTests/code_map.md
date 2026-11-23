# Pudding.DesktopConnectionTests
> 源指纹: *.cs=16-21f4284e6b68, *.csproj=1-bb96b44c1aea · 条目数: 0 · 最近整理: 2026-11-23

测试工程，引用 Pudding.DesktopConnection；用例全部为进程内单测，不需要真实宿主或外部服务。

`BackpressureAndDisconnectTests.cs` 覆盖 9 条用例：
- `Heartbeat_IsAcknowledgedWithTheSameSequence`
- `BestEffortFrames_AreDroppedAndCountedWhenTheBudgetIsExhausted`
- `TerminalResult_IsNotDroppedWhenTheBudgetIsTight`
- `Disconnect_CompletesPendingAsOutcomeUnknownOrDisconnected`
- `ServerClosesStream_EndsCleanlyAndHalfClosesRequestStream`
- `EmptyFrame_FaultsTheConnection`
- （其余 3 条同类用例略）

`BridgeCallerContextTests.cs` 覆盖 4 条用例：
- `MissingCallerIdentity_IsRejected`
- `UnregisteredCallerKind_IsRejected`
- `SubAgentWithoutParentTask_IsRejected`
- `ValidAgentCaller_IsAccepted`

`BrowserBindingDecodeTests.cs` 覆盖 4 条用例：
- `Decode_BindingApply_ProducesTheTaskBinding`
- `Decode_BindingApply_WithAMismatchedPayloadIsRefused`
- `Decode_BindingApply_WithAnInvalidStructureIsRefused`
- `Decode_BindingRevoke_ProducesTaskAndTarget`

`BrowserOperationIdentityTests.cs` 覆盖 2 条用例：
- `ChildBindingMustPreserveParentRunOnApplyAndRevoke`
- `IdempotencyFingerprintMustIncludeCallerAndCompletePayload`

`ByteBudgetTests.cs` 覆盖 6 条用例：
- `TryReserve_RespectsCapacity`
- `ShrinkTo_LowersCapacityButAllowsCurrentUsageToDrain`
- `ReserveAsync_WaitsForReleaseAndKeepsFifoOrder`
- `ReserveAsync_ReturnsFalseWhenCancelled`
- `Constructor_RejectsNonPositiveCapacity`
- `Release_ClampsAtZero`

`CallerIdentityDecodeTests.cs` 覆盖 7 条用例：
- `ATrustedAgentCallerIsDecodedOntoTheCallContext`
- `ABrowserCapabilityWithoutAnyCallerIsRefused`
- `AnUnknownCallerKindIsRefusedInsteadOfDefaultingToAKind`
- `AUserCallerCannotBeUsedOnABrowserCapability`
- `ACallerWithoutARunIdentityIsRefused`
- `ASubAgentCallerWithoutItsParentIsRefused`
- （其余 1 条同类用例略）

`CommandDispatchTests.cs` 覆盖 27 条用例：
- `PageStateCommand_ReturnsPageStateOutcome`
- `PageStateCommand_WithoutTarget_IsRejectedAsInvalidTarget`
- `NavigateCommand_ProducesTypedResultAndAudit`
- `JavaScriptCommand_AppliesDefaultResultBudget`
- `NotificationCommand_MapsPriorityAndReturnsOutcome`
- `PayloadThatDoesNotMatchCapability_IsRejectedWithoutExecution`
- （其余 21 条同类用例略）

`ComponentBoundaryTests.cs` 覆盖 6 条用例：
- `ConnectionAssembly_ReferencesOnlyItsDeclaredClosure`
- `TestProcess_DoesNotLoadUiOrServerAssemblies`
- `ConnectionCsproj_DeclaresExpectedReferencesAndKeepsItsBoundaryTarget`
- `ConnectionTestsCsproj_ReferencesExactlyOneProject`
- `ExecutorSeam_ExposesNoTransportOrUiTypes`
- `ConnectionComponent_HasNoHandWrittenGrpcServerCode`

`HandshakeTests.cs` 覆盖 11 条用例：
- `Handshake_SendsHelloWithDeclaredCapabilities_AndBecomesReady`
- `Handshake_AppliesCoreLimitsByTakingTheStricterOne`
- `Handshake_RejectsAckThatGrantsAnUndeclaredCapability`
- `Handshake_RejectsUnsupportedProtocolVersion`
- `Handshake_RejectsNonAckFirstFrame`
- `Handshake_RejectsMissingGenerationOrConnectionId`
- （其余 5 条同类用例略）

`InteractPointerDecodeTests.cs` 覆盖 3 条用例：
- `CoordinatesAtTheOriginSurviveInsteadOfLookingUnset`
- `FractionalCoordinatesAndMoveActionTravel`
- `PointerActionsWithoutCoordinatesAreRefusedAtTheWireBoundary`

`MappingRoundTripTests.cs` 覆盖 17 条用例：
- `Hello_UsesContractsProtocolRangeAndDeclaredCapabilities`
- `Decode_NavigateCommand_ProducesDomainRequestAndContext`
- `Decode_JavaScriptAndNotification_MapDomainValues`
- `Decode_FingerprintIgnoresVolatileFieldsButDetectsPayloadChanges`
- `Decode_RejectsMalformedInputFailClosed`
- `DecodePageStateCommand_ProducesTargetOnlyRequest`
- （其余 11 条同类用例略）

`RunnerTests.cs` 覆盖 8 条用例：
- `Runner_RetriesWithBackoffAndResetsAfterAHealthyConnection`
- `Runner_DoesNotReplayCommandsAfterReconnect`
- `Runner_StopsOnCancellation`
- `Runner_WhenMaxAttemptsReached_StopsRetrying`
- `Runner_RejectsInvalidBackoffBounds`
- `Transport_RequiresLoopbackForPlaintextHttp2`
- （其余 2 条同类用例略）

`ScreenshotResultEncodeTests.cs` 覆盖 4 条用例：
- `TheObservationIdAndScalarsSurviveEncoding`
- `ZeroValuedOptionalsArePresentNotAbsent`
- `ChunksKeepTheirOrderAndLastFlag`
- `NoChunksStaysEmptyInsteadOfAFabricatedOne`

`SnapshotBudgetDecodeTests.cs` 覆盖 2 条用例：
- `Decode_TextModeBudget_ThatOnlyRequestsContentBlocksIsAccepted`
- `Decode_BudgetRequestingNoRepresentationAtAllIsRefused`

`TestInfrastructure.cs` 覆盖 9 条用例：
- `Advance`
- `Dispose`
- `Send`
- `SendRaw`
- `CloseFromServer`
- `FailFromServer`
- （其余 3 条同类用例略）
