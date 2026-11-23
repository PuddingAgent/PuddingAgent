# PuddingTokenizationTests
> 源指纹: *.cs=7-8521b673a8e8, *.csproj=1-5990324f798f · 条目数: 0 · 最近整理: 2026-11-23

测试工程，引用 PuddingTokenization；用例全部为进程内单测，不需要真实宿主或外部服务。

`ComponentBoundaryTests.cs` 覆盖 2 条用例：
- `Component_HasNoProjectReferences`
- `Component_DoesNotReferenceCoreOrRuntimeTypes`

`DeepSeekTokenizerTests.cs` 覆盖 7 条用例：
- `Load_ReportsExpectedStructure`
- `SpecialTokens_MapToDeclaredIds`
- `Chinese_EncodesToNonEmptyTokens`
- `Digits_AreSplitByTheConfiguredRule`
- `MixedSamples_RoundTrip`
- `Load_RejectsNonBpeModelType`
- （其余 1 条同类用例略）

`GoldenVectorTests.cs` 覆盖 1 条用例：
- `EncodeMatchesOfficialTokenizerPerTokenId`

`IsolatedSplitPreTokenizerTests.cs` 覆盖 4 条用例：
- `SequenceSplitsMatchesAgain_AndPreservesUnmatchedGapsAndTail`
- `SpecialTokensAreLongestFirst_AndBypassAllSplitStages`
- `ScalarCategoriesAndNegation_DoNotSplitSupplementaryCharacters`
- `SpanEnumerationDoesNotRetainBorrowedStorage`

`TokenizerAssetDirectoryTests.cs` 覆盖 10 条用例：
- `CreateSandbox`
- `RemoveSandbox`
- `EnvironmentValue_WinsAndIsTrimmed`
- `WhitespaceEnvironmentValue_IsTreatedAsUnset`
- `MissingBesideDirectory_ReportsNone`
- `EmptyBesideDirectory_IsNotConfigured`
- （其余 4 条同类用例略）

`TokenizerProviderTests.cs` 覆盖 8 条用例：
- `UnregisteredModel_FallsBackWithReason`
- `NullOrEmptyModel_FallsBack`
- `RegisteredModelWithoutDirectory_ReportsNotConfigured`
- `RegisteredModelWithMissingAsset_ReportsAssetMissing`
- `RegisteredModelWithCorruptAsset_ReportsAssetInvalid`
- `FallbackHandle_RefusesToProduceATokenCount`
- （其余 2 条同类用例略）

`UnicodeRegressionTests.cs` 覆盖 3 条用例：
- `CountAndEncode_MatchIndependentUnicodeVectors`
- `PreTokenization_AlwaysPartitionsInput_AndOwnsSpanSnapshot`
- `LongSharedTokenizer_IsStableUnderConcurrentCounting`
