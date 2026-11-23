# Pudding.CapabilityBrokerTests
> 源指纹: *.cs=15-7dbfde309b74, *.csproj=1-213be971241a · 条目数: 0 · 最近整理: 2026-11-23

测试工程，引用 Pudding.CapabilityBroker；用例全部为进程内单测，不需要真实宿主或外部服务。

`BindingIssuanceEndToEndTests.cs` 覆盖 4 条用例：
- `AnAcceptedAckTravelsFromTheSessionBackToTheProducer`
- `ARejectedAckTravelsBackWithItsReason`
- `AKnownEpochTravelsBackUnchanged`
- `AnUnspecifiedEpochIsReportedAsNoneNotAsOne`

`ComponentBoundaryTests.cs` 覆盖 5 条用例：
- `BrokerAssembly_ReferencesOnlyContractsAndTheWireProtocol`
- `TestProcess_DoesNotLoadDesktopHostOrServerAssemblies`
- `BrokerCsproj_DeclaresExpectedReferencesAndKeepsItsBoundaryTarget`
- `BrokerTestsCsproj_ReferencesExactlyOneProject`
- `PublicSurface_ExposesNoUiOrHostTypes`

`DesktopBrowserBindingProducerTests.cs` 覆盖 5 条用例：
- `AnAcceptedAckIsReportedAsIssuedWithTheGrantId`
- `ARejectedAckIsReportedWithItsReasonAndNoGrant`
- `AThrowingChannelIsReportedAsFailureNotSuccess`
- `ConcurrentIdenticalRequestsAreSentOnceAndShareOneOutcome`
- `DifferentTargetsAreSentSeparately`

`DisconnectAndQueueTests.cs` 覆盖 10 条用例：
- `DesktopClosingTheStream_CompletesPendingAsOutcomeUnknown`
- `ReadFault_FaultsTheSessionAndCompletesPending`
- `ExplicitDisconnect_ReleasesQueuedWaitersWithNotConnected`
- `Disconnect_ReleasesInFlightSlots`
- `OutboundQueueFullUntilDeadline_ReturnsResourceExhausted`
- `Audit_RecordsOneTerminalRecordPerOperation`
- （其余 4 条同类用例略）

`EndpointNamingTests.cs` 覆盖 5 条用例：
- `NamedPipeName_IsDeterministicAndScopedPerUserAndProductInstance`
- `NamedPipeName_IsAValidPipeNameAndDoesNotLeakTheScope`
- `ScopeHash_RequiresBothParts`
- `NamedPipeEndpoint_PublishesTheDerivedNameAndCoreInstance`
- `LoopbackAndTlsEndpoints_AreShapedAsExpected`

`InvokeAndCorrelationTests.cs` 覆盖 20 条用例：
- `Navigate_SendsCommandAndCompletesFromResult`
- `EveryCapability_RoundTripsItsTypedResult`
- `Invoke_AfterDisconnect_IsNotConnected`
- `Invoke_WhenCallTargetsAnotherDesktop_IsInvalidTarget`
- `Invoke_WithoutConfiguredAuthorizer_IsDeniedByDefault`
- `Invoke_WhenAuthorizerDenies_IsUnauthorizedAndAudited`
- （其余 14 条同类用例略）

`MappingTests.cs` 覆盖 13 条用例：
- `EncoderCarriesTheTrustedCallerOntoTheWire`
- `EncoderDoesNotInventACallerWhenTheContextHasNone`
- `TheBrowserCapabilityClassifierSeparatesShellCapabilities`
- `Encoder_ProducesTheExpectedPayloadPerCapability`
- `Encoder_RejectsRequestWithoutMatchingPayload`
- `Fingerprint_IsStableForTheSameRequestAndSensitiveToPayloadChanges`
- （其余 7 条同类用例略）

`NegotiationAndRegistryTests.cs` 覆盖 12 条用例：
- `Accept_NegotiatesAndSendsAck`
- `Accept_GrantsOnlyTheDeclaredIntersectionWithLocalPolicy`
- `Accept_SucceedsWithNoNegotiatedCapability`
- `Accept_RejectsUnexpectedDesktopIdentity`
- `Accept_RejectsNonHelloFirstFrame`
- `Accept_RejectsUnsupportedProtocolVersion`
- （其余 6 条同类用例略）

`PageVersionRecordingTests.cs` 覆盖 6 条用例：
- `LocateResult_AdvancesTheKnownVersion`
- `SnapshotResult_AdvancesTheKnownVersion`
- `PageStateResult_AdvancesTheKnownVersion`
- `NavigateResult_AdvancesTheKnownVersion`
- `InteractionResult_AdvancesTheKnownVersion`
- `EveryResultKind_RecordsUnderTheRequestedTarget`

`ScreenshotResultDecodeTests.cs` 覆盖 3 条用例：
- `DecodedFieldsMatchTheWireAndZeroStaysZero`
- `AnUnregisteredDetailIsRefused`
- `APayloadThatDoesNotMatchTheCommandedCapabilityIsRefused`

`StalePageVersionTests.cs` 覆盖 5 条用例：
- `RequestPinnedToAnOlderVersion_IsRejectedBeforeSending`
- `RequestPinnedToTheCurrentOrNewerVersion_IsSentNormally`
- `UnknownVersionIsNeverTreatedAsStale`
- `VersionTrackingIsPerTarget`
- `FailedResults_DoNotAdvanceTheKnownVersion`

`TabsResultDecodeTests.cs` 覆盖 4 条用例：
- `CreatingATabTakesItsTargetFromTheResult`
- `CreatingATabWithoutACreatedTargetIsRefused`
- `AnActionOtherThanCreateStillNeedsTheRequestedTarget`
- `TheRequestedTargetStillWinsWhenItIsKnown`

`TestInfrastructure.cs` 覆盖 5 条用例：
- `Push`
- `PushClose`
- `PushFault`
- `ReleaseSends`
- `Record`

`TransportRoutingTests.cs` 覆盖 10 条用例：
- `ReadyChannel_WinsDuringMigration`
- `ChannelNotReadyYet_FallsBackToTheLegacyBridge`
- `AfterTheChannelWasAttempted_ThereIsNoFallback`
- `AttemptedChannelThatIsStillReady_KeepsWaitingOnIt`
- `NothingAvailable_IsReportedAsFailureNotAsSilentRetry`
- `Reasons_NeverContainPageContentOrCredentials`
- （其余 4 条同类用例略）

`TrustedCallerDesktopCapabilityAuthorizerTests.cs` 覆盖 12 条用例：
- `MissingIdentity_DeniesEveryCapabilityInTheCatalogue`
- `IdentityWithoutSession_Denies`
- `ReadOnlyCapability_WithIdentity_IsAllowed`
- `TrustedContextCapability_WithoutAgentIdentity_Denies`
- `MutatingCapability_WithoutExecution_Denies`
- `MutatingCapability_WithFullExecution_IsAllowed`
- （其余 6 条同类用例略）
