# Pudding.ContractsTests
> 源指纹: *.cs=21-eec46bb6d7e1, *.csproj=1-a068a33861f6 · 条目数: 0 · 最近整理: 2026-11-23

测试工程，引用 Pudding.Contracts；用例全部为进程内单测，不需要真实宿主或外部服务。

`AuditContractShapeTests.cs` 覆盖 5 条用例：
- `AuditRecord_DeclaresExactlyTheAllowedDiagnosticProperties`
- `AuditRecord_HasNoPayloadShapedMemberNames`
- `AuditRecord_MemberTypesStayInsideContractsAndBcl`
- `AuditRecord_RecordsOnlySemanticMetadata`
- `NullSink_AcceptsEveryRecordSilently`

`BrowserTextDiffTests.cs` 覆盖 9 条用例：
- `IdenticalText_HasNoDiff`
- `AppendedLines_AreReportedExactly`
- `RemovedAndInsertedLines_AreReportedPerSide`
- `EmptyBaseline_MeansEverythingIsNew`
- `RepeatedLines_DoNotConfuseThePrefixSuffixTrim`
- `OverBudgetDiff_IsFlaggedInsteadOfSilentlyTrimmed`
- （其余 3 条同类用例略）

`CapabilityCatalogTests.cs` 覆盖 9 条用例：
- `Catalog_IsFrozenSnapshot`
- `AllCapabilities_IsUnionOfCatalog`
- `Enumerate_FollowsCatalogOrder_AndIgnoresUnknownBits`
- `DeclareFor_ProducesWireNamesAndVersions`
- `TryGetByName_IsCaseSensitive`
- `NameOf_RejectsNoneAndComposite`
- （其余 3 条同类用例略）

`CapabilityEndpointTests.cs` 覆盖 8 条用例：
- `NamedPipe_ValidatesTheName`
- `LoopbackRequiresPlaintextLoopbackWithoutCredentials`
- `TlsRequiresHttpsAndRejectsCredentials`
- `ServerInstanceId_MustBeAValidProcessInstanceIdentifier`
- `EndpointString_RoundTrips`
- `Parse_FailsClosedOnMalformedInput`
- （其余 2 条同类用例略）

`CapabilityNegotiationTests.cs` 覆盖 8 条用例：
- `EmptyDeclaration_FailsClosed`
- `NullOrEmptyGrant_YieldsNoCapabilitiesButSucceeds`
- `SubsetGrant_IsAccepted`
- `GrantOfUndeclaredCapability_IsProtocolError`
- `GrantWithHigherVersion_IsProtocolError`
- `DuplicateDeclaration_IsRejected`
- （其余 2 条同类用例略）

`CapabilityResultTests.cs` 覆盖 7 条用例：
- `Success_CarriesValueAndNoError`
- `Failure_CarriesErrorAndNoValue`
- `DefaultStruct_IsUninitializedAndFailsLoudly`
- `Failure_RejectsNullError`
- `Map_PropagatesFailureWithoutInvokingSelector`
- `Map_TransformsSuccess`
- （其余 1 条同类用例略）

`CapabilityUnionTests.cs` 覆盖 8 条用例：
- `Request_Factories_SetExactlyOneBranch`
- `Request_ExposesExpectedPageVersion`
- `Request_RejectsNullPayloads`
- `Response_Factories_AreMutuallyExclusive`
- `Response_RejectsEmptyOrAmbiguousResults`
- `Request_PageStateVariant_CarriesOnlyATarget`
- （其余 2 条同类用例略）

`ComponentBoundaryTests.cs` 覆盖 5 条用例：
- `ContractsAssembly_ReferencesOnlyBcl`
- `TestProcess_DoesNotLoadTransportOrHostAssemblies`
- `ContractsCsproj_DeclaresNoProjectOrPackageReference`
- `ContractsTestsCsproj_ReferencesExactlyOneProject`
- `PublicContracts_ExposeNoTransportOrUiTypes`

`ContractValueTests.cs` 覆盖 8 条用例：
- `Identifiers_RejectIllegalValues`
- `ConnectionGeneration_RequiresPositiveValue`
- `CallContext_RequiresUtcDeadlineAndValidIdentities`
- `PageTarget_RequiresExplicitContextAndPage`
- `PageVersion_UnknownIsZeroAndRequireRejectsNonPositive`
- `NavigateRequest_RequiresAbsoluteUrl`
- （其余 2 条同类用例略）

`Desktop/BrowserBindingAckTests.cs` 覆盖 2 条用例：
- `AnAcceptedAckCarriesTheGrantIdAndEpoch`
- `ARejectedAckKeepsItsDistinguishableReason`

`Desktop/BrowserCallerIdentityProjectionTests.cs` 覆盖 7 条用例：
- `AnAgentExecutionContextProjectsToAnAgentCaller`
- `ASubAgentCarriesTheParentTaskSoItsScopeCanBeCheckedAsASubset`
- `ASubAgentWithoutAParentTaskIsRefused`
- `AMissingRunIdentityIsRefused`
- `AMissingCallerIdentityIsRefused`
- `IdentitiesAreTrimmedSoWhitespaceCannotForgeADifferentActor`
- （其余 1 条同类用例略）

`Desktop/BrowserCallerKindWireTests.cs` 覆盖 3 条用例：
- `KindsRoundTripThroughTheWireName`
- `UnknownWireValuesAreRefusedInsteadOfDefaultingToAKind`
- `AnUnregisteredEnumValueCannotBeFormatted`

`Desktop/BrowserControlPlaneRequestsTests.cs` 覆盖 2 条用例：
- `TheApplyRequestCarriesTheWholeBinding`
- `TheRevokeRequestCarriesTaskAndTargetOnly`

`Desktop/BrowserTabOperationMappingTests.cs` 覆盖 3 条用例：
- `TabActionsMapSeparately`
- `TabActivateIsNotDowngradedToTabClose`
- `CapabilityLevelMappingStillWorksForCallersThatOnlyHaveACapability`

`Desktop/BrowserTaskBindingTests.cs` 覆盖 3 条用例：
- `ATrustedTaskIdentityIsAccepted`
- `AUserCallerCannotBeTheGrantee`
- `AStructureThatGrantsNothingOrForeverIsRefused`

`Desktop/DesktopCallContextCallerTests.cs` 覆盖 7 条用例：
- `ANonBrowserCallMayOmitTheCaller`
- `AnAgentCallerWithARunIdentityIsAcceptedForBrowserOperations`
- `ASubAgentCallerKeepsItsParentTask`
- `AMissingCallerIsRefusedWithAConcreteReason`
- `AUserCallerCannotBeUsedToBypassTheAutomationGate`
- `ACallerWithoutARunIdentityIsRefused`
- （其余 1 条同类用例略）

`Desktop/DesktopCapabilityOperationsTests.cs` 覆盖 3 条用例：
- `WebViewCapabilitiesMapToTheirAutomationOperation`
- `ShellCapabilitiesAreNotBrowserOperations`
- `EveryWebViewCapabilityInTheCatalogHasAMapping`

`ElementFieldsTests.cs` 覆盖 7 条用例：
- `Default_KeepsEverything`
- `CoreFields_SurviveAnyWhitelist`
- `UnknownField_IsRejectedWithItsName`
- `EmptyOrBlankInput_MeansNoWhitelist`
- `SnapshotFields_ShareTheSameParsingSemantics`
- `OutputFieldSet_IsReusableAcrossTools`
- （其余 1 条同类用例略）

`ErrorTaxonomyTests.cs` 覆盖 10 条用例：
- `WireNames_AreFrozenSnapshot`
- `NameOf_RejectsUnregisteredCode`
- `TryParse_RoundTripsEveryRegisteredCode`
- `DefaultSemantics_AreFrozenSnapshot`
- `CursorExpired_SaysReReadInsteadOfRetrying`
- `IsSafeToRetry_ExcludesSideEffectingErrors`
- （其余 4 条同类用例略）

`InteractContractTests.cs` 覆盖 14 条用例：
- `EmptyFillIsAllowedButEmptyPressIsNot`
- `ActionLineNames_AreFrozenAndUnknownIsRejected`
- `NativeInputCoverage_IsOneFrozenTruth`
- `PointerActionsCarryViewportCoordinatesAndObservationId`
- `PointerActionsRequireBothCoordinates`
- `PointerActionsRefuseALocator`
- （其余 8 条同类用例略）

`LocateContractTests.cs` 覆盖 11 条用例：
- `LocatorKindLineNames_AreFrozenAndUnknownIsRejected`
- `ReferenceLocator_MustCarryThePageVersionItCameFrom`
- `RequestUnion_ExposesTheLocateVersionAndTarget`
- `Locator_AndElementRef_ValidateTheirInputs`
- `LocateResult_DistinguishesEmptyFromTruncated`
- `ReadRequest_RequiresAUsableBudget`
- （其余 5 条同类用例略）

`LocateCursorTests.cs` 覆盖 7 条用例：
- `Cursor_RoundTripsThroughTheOpaqueWireFormat`
- `MalformedCursor_IsRejected_InsteadOfBeingGuessed`
- `Cursor_MatchesOnlyTheSameVersionAndTheSameQuery`
- `TryResolve_ClassifiesExpirySeparatelyFromMalformedInput`
- `Fingerprint_IsStableAcrossProcesses_AndCoversTheLocator`
- `Fingerprint_IgnoresPagingItself`
- （其余 1 条同类用例略）

`PanelRenderContractTests.cs` 覆盖 12 条用例：
- `ForPanelRender_PutsThePayloadOnTheUnion`
- `FromPanelRender_PutsThePayloadOnTheResponse`
- `Response_DistinguishesPanelRenderFromFailure`
- `PanelId_MustBeNonEmpty`
- `PanelId_RejectsCharactersThatWouldBreakTabIdentity`
- `PanelId_AllowsDotsUnderscoresAndDashes`
- （其余 6 条同类用例略）

`ProtocolVersionTests.cs` 覆盖 3 条用例：
- `CurrentVersion_IsTheOnlySupportedVersionToday`
- `TryNegotiate_TakesIntersection`
- `TryNegotiate_FailsWhenRangesDoNotOverlap`

`ReadingRevisionTests.cs` 覆盖 7 条用例：
- `Revision_RoundTripsAndBindsVersionQueryAndContent`
- `MalformedRevision_IsRejected`
- `Comparability_RequiresSameVersionAndSameQuery`
- `ContentHash_IsStableAcrossProcesses`
- `QueryFingerprint_DistinguishesModeScopeAndFields`
- `Create_RejectsNonsenseInputs`
- （其余 1 条同类用例略）

`ScreenshotContractTests.cs` 覆盖 7 条用例：
- `AViewportScreenshotTakesNoLocatorAndNoRegion`
- `AFullPageScreenshotTakesNoLocatorAndNoRegion`
- `ARegionScreenshotRequiresAPositiveRectangle`
- `AScreenshotMustPinThePageVersion`
- `ScopeAndDetailWireNamesAreStableAndUnknownNamesAreRejected`
- `MetadataAndResultRefuseAnEmptyObservationOrMissingParts`
- （其余 1 条同类用例略）

`ShellStatusTests.cs` 覆盖 7 条用例：
- `ShellStatus_ValidatesItsInputs`
- `WindowStateLineNames_AreFrozenAndUnknownFoldsToUnknown`
- `AutomationStateLineNames_AreFrozenAndUnknownFoldsToUnsafeSide`
- `ShellStatus_IsImmutableValueType`
- `RequestUnion_CarriesTheParameterlessShellStatusQuery`
- `ResponseUnion_CarriesTheShellStatusOutcome`
- （其余 1 条同类用例略）

`TabsContractTests.cs` 覆盖 3 条用例：
- `TabActionLineNames_AreFrozenAndUnknownIsRejected`
- `TabOperation_MustPinThePageVersion`
- `TabsResult_CarriesTheActivePageAndTheRemainingList`
