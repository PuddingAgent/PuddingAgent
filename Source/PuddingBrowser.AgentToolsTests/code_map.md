# PuddingBrowser.AgentToolsTests
> 源指纹: *.cs=7-e2dd4fd3aed1, *.csproj=1-622312225253 · 条目数: 0 · 最近整理: 2026-11-23

测试工程，引用 PuddingBrowser.AgentTools；用例全部为进程内单测，不需要真实宿主或外部服务。

`BrowserCapabilityFailureTests.cs` 覆盖 2 条用例：
- `Unauthorized_IsNotDowngradedToActionFailure`
- `OtherFailures_DoNotBorrowTheAuthorizationHint`

`BrowserInteractArgumentPolicyTests.cs` 覆盖 9 条用例：
- `PointerClickWithLocatorBecomesAnElementClick`
- `PointerMoveWithLocatorBecomesHover`
- `LocatorAndCoordinatesTogetherAreRejected`
- `PointerClickWithCoordinatesKeepsTheRawCoordinateAction`
- `PointerActionWithoutLocatorOrCoordinatesIsRejected`
- `PointerDragWithBothEndpointsIsAccepted`
- （其余 3 条同类用例略）

`BrowserTextInputTests.cs` 覆盖 4 条用例：
- `AsciiPrintable_DoesNotNeedClipboard`
- `NullDoesNotNeedClipboard`
- `CjkAndEmoji_NeedClipboard`
- `PrintableBoundariesAreInclusive`

`PageVersionPolicyTests.cs` 覆盖 6 条用例：
- `SameDocumentNavigationBumpsVersion`
- `RealNavigationDoesNotDoubleCount`
- `UnchangedUrlDoesNotBump`
- `MissingNewUrlDoesNotBump`
- `QueryStringChangeBumpsVersion`
- `UrlComparisonIsOrdinalNotCaseInsensitive`

`PanelInspectToolTests.cs` 覆盖 18 条用例：
- `Read_ReportsAbsentCanvasAsNotAnError`
- `Read_ReturnsMetadataWithoutContentByDefault`
- `Read_CanPreviewAPrefixAndSaysWhetherItWasTruncated`
- `Read_CapsThePreviewSoALargeCanvasCannotFloodTheConversation`
- `Read_PrefersTheStoredCanonicalSpellingOfThePanelId`
- `List_IsEmptyBeforeAnythingIsPublished`
- （其余 12 条同类用例略）

`PreviewDocumentToolTests.cs` 覆盖 4 条用例：
- `HappyPath_NormalizesSeparatorsAndReturnsIdentityOnly`
- `InvalidPaths_FailBeforeAnyDesktopCall`
- `NoDesktop_FailsWithExplicitCode`
- `CapabilityFailure_IsReportedAsFailure`

`RenderPanelToolTests.cs` 覆盖 7 条用例：
- `DefaultsToTheMainCanvas_StoresIt_AndReturnsIdentityOnly`
- `ExplicitPanelIdAndTitleArePassedThrough`
- `RepublishingIncrementsRevision`
- `EmptyHtml_FailsBeforeAnyStoreOrDesktopWrite`
- `IllegalPanelId_FailsBeforeAnyStoreOrDesktopWrite`
- `NoDesktop_StillStoresContentAndReportsStored`
- （其余 1 条同类用例略）
