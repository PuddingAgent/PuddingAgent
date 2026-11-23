# PuddingFinanceTests
> 源指纹: *.cs=11-078bc85ca541, *.csproj=1-8cc03ce5783f · 条目数: 0 · 最近整理: 2026-11-23

测试工程，引用 PuddingFinance、PuddingFinance.Documents；用例全部为进程内单测，不需要真实宿主或外部服务。

`AnnouncementSelectorTests.cs` 覆盖 10 条用例：
- `Classify_SemiAnnualBeforeAnnual`
- `Classify_OtherKinds`
- `Select_WhenAShareAndHongKongBothExist_PicksAShare`
- `Select_OnlyHongKongVersion_ReportsExclusionInsteadOfEmpty`
- `Select_MultipleAnnualReports_TakesLatest`
- `Select_SummaryIsNotFullText`
- （其余 4 条同类用例略）

`BiyingClientTests.cs` 覆盖 11 条用例：
- `SecondIdenticalCall_HitsCache_AndDoesNotConsumeQuotaAgain`
- `QuotaExhausted_ThrowsBeforeSendingRequest`
- `LicenceRejection_IsNotRetryable`
- `BusinessErrorInsideHttp200_IsDetected`
- `ServerError_IsRetryable`
- `CacheExpires_AfterRealtimeTtl`
- （其余 5 条同类用例略）

`BiyingEndpointCatalogTests.cs` 覆盖 15 条用例：
- `EmbeddedCatalog_LoadsAllEndpoints`
- `EveryEndpoint_HasFieldDictionary`
- `BalanceSheet_CarriesHundredsOfFields`
- `BuildUrl_PlacesCodeThenLicenceAsPathSegments`
- `BuildUrl_EndpointWithoutCode_HasLicenceAsLastSegment`
- `BuildUrl_ProEndpoint_FillsBothPathArguments`
- （其余 9 条同类用例略）

`BiyingRawToolTests.cs` 覆盖 5 条用例：
- `UnknownEndpointId_IsRejectedWithoutSendingRequest`
- `QueryKeyOutsideEndpointWhitelist_IsRejectedWithoutSendingRequest`
- `ValidEndpoint_RequestsExpectedUrlAndShapesResponse`
- `DefaultLimit_IsConservativeForUnknownShapes`
- `FieldProjection_NarrowsUnknownShapes`

`BiyingSectorFlowTests.cs` 覆盖 10 条用例：
- `StockList_WithoutKeyword_RefusesBeforeAnyRequest`
- `StockList_WithKeyword_FiltersToMatchingRows`
- `StockList_KeywordWithoutMatch_SaysSoInsteadOfEmptyTable`
- `Constituents_RequiresSectorCode`
- `Constituents_CallsSectorEndpointWithCode`
- `LhbStock_PassesDayCountAsPathArgument`
- （其余 4 条同类用例略）

`BiyingShaperTests.cs` 覆盖 12 条用例：
- `BalanceSheet_IsCompressedByOrdersOfMagnitude`
- `RowsAreSelectedByRecency_NotBySourceOrder`
- `LegendOnlyCoversFieldsThatAppeared`
- `UnknownFieldsAreReportedNotSilentlyDropped`
- `UnknownFieldsAppearInPayload_LegendOmitsPlaceholders`
- `ManyUnknownFields_DoNotInflateShapedOutput`
- （其余 6 条同类用例略）

`BiyingToolTests.cs` 覆盖 9 条用例：
- `Quote_HitsRealtimeEndpoint_AndProjectsToValuationFields`
- `Index_UsesIndexEndpoint_NotStockEndpoint`
- `Kline_DefaultsToUnadjustedPrices`
- `Kline_RejectsCodeWithoutMarketSuffix`
- `Quote_WithoutCode_FailsBeforeAnyRequest`
- `Balance_KeepsFullStatement_MinusProjection`
- （其余 3 条同类用例略）

`CninfoAnnouncementTests.cs` 覆盖 12 条用例：
- `Parse_RealShapedPayload_ReadsAnnouncementAndPdfUrl`
- `Parse_Timestamp_IsInterpretedAsChinaStandardTime`
- `Parse_NullAnnouncements_YieldsEmptyPage`
- `Parse_NonJsonBody_ThrowsParseException`
- `BuildForm_PutsPagingAndSearchKeyInPlace`
- `BuildForm_EscapesChineseSearchKey`
- （其余 6 条同类用例略）

`OfficialSourceFetcherTests.cs` 覆盖 12 条用例：
- `NonWhitelistedHost_IsRejectedBeforeAnyRequest`
- `LookalikeHost_IsRejected`
- `EmptyUserInfoInUrl_IsRejected`
- `Post_SendsBodyWithDeclaredContentType`
- `RetryableStatus_RetriesThenSucceeds_AndRecordsStatus`
- `NonRetryableStatus_DoesNotRetry`
- （其余 6 条同类用例略）

`PdfTextSourceTests.cs` 覆盖 6 条用例：
- `Extract_GeneratedPdf_ReadsTextAndPageCount`
- `Extract_MaxPages_LimitsExtractionButKeepsTotalCount`
- `Extract_EmptyBytes_Throws`
- `Extract_NonPdfBytes_ThrowsInsteadOfReturningEmptyDocument`
- `Extract_ZeroMaxPages_Throws`
- `CountCjk_CountsHanOnly`
