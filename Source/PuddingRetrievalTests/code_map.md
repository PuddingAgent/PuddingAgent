# PuddingRetrievalTests
> 源指纹: *.cs=5-5d65ac321dad, *.csproj=1-4a20e8da3093 · 条目数: 0 · 最近整理: 2026-11-23

测试工程，引用 PuddingRetrieval；用例全部为进程内单测，不需要真实宿主或外部服务。

`ComponentBoundaryTests.cs` 覆盖 4 条用例：
- `Retrieval_ReferencesNoStoreOrHostAssembly`
- `RetrievalCsproj_DeclaresOnlyAllowedProjectReferences`
- `RetrievalTextMatch_IsValueEqual`
- `RetrievalTextSourceUnavailableException_CarriesInnerException`

`DefaultLanguageCapabilityMatrixTests.cs` 覆盖 9 条用例：
- `EveryUnsupportedEntry_CarriesAnAlternative`
- `UnknownLanguage_NeverReportsSupported`
- `EmptyLanguageId_IsTreatedAsUnknown`
- `LanguageId_IsNormalizedToLowerCase`
- `CSharp_DeclaresSemanticOnTheRoslynBackedFaces`
- `CSharp_DocComments_IsDeclaredUnsupported_BecauseRetrievalIsBroken`
- （其余 3 条同类用例略）

`RetrievalCoverageMatrixTests.cs` 覆盖 8 条用例：
- `Build_ProducesOneCellPerLanguageAndFace`
- `Build_NormalizesAndDeduplicatesLanguages`
- `Build_UndeclaredLanguage_IsUnsupportedEverywhere_NotSilentlyDropped`
- `Build_SkipsProjectsWithoutId`
- `Build_EmptyInput_YieldsNoRows`
- `SupportedLanguages_OnlyCountsLanguagesWithAtLeastOneSupportedFace`
- （其余 2 条同类用例略）

`RetrievalPortV1Tests.cs` 覆盖 26 条用例：
- `TypeIntent_RequestsTypeKindsAndReturnsDeclarations`
- `Impact_Depth1_ReturnsDirectUpstreamOnly`
- `Impact_Depth2_CrossesIntermediateSymbol`
- `Impact_CycleBackToRoot_DoesNotReEmitRoot`
- `Impact_DepthLimitReached_MarksDegraded`
- `NoProjects_ReportsNotIndexed_AndSuggestsRebuild`
- （其余 20 条同类用例略）

`SymbolRefFreshnessTests.cs` 覆盖 8 条用例：
- `EqualHash_IsFresh_AndCarriesNoRelocation`
- `ChangedHash_UniqueCandidate_IsStaleButRelocatable`
- `ChangedHash_NoCandidate_SaysCannotLocate_NotSymbolMissing`
- `ChangedHash_MultipleCandidates_IsAmbiguous`
- `PositionDependentId_IsNeverRelocatable_EvenWithCandidate`
- `UnknownSymbolIdForm_IsTreatedAsNotRelocatable`
- （其余 2 条同类用例略）
