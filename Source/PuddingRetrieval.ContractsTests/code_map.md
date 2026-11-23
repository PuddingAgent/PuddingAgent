# PuddingRetrieval.ContractsTests
> 源指纹: *.cs=14-6924b37f8c42, *.csproj=1-d64473e0d6f8 · 条目数: 0 · 最近整理: 2026-11-23

测试工程，引用 PuddingRetrieval.Contracts、PuddingDomain.Contracts；用例全部为进程内单测，不需要真实宿主或外部服务。

`ComponentBoundaryTests.cs` 覆盖 3 条用例：
- `Contracts_ReferencesOnlyDomainContractsAndBcl`
- `ContractsCsproj_DeclaresNoForbiddenProjectReferences`
- `TestProject_DoesNotReferenceImplementations`

`RetrievalBudgetContractTests.cs` 覆盖 1 条用例：
- `A08_Single_Page_Is_Bounded_By_PageSize_And_Budgets`

`RetrievalCapabilityContractTests.cs` 覆盖 1 条用例：
- `A06_Capability_Matrix_Must_Say_Unsupported_With_Alternative`

`RetrievalClaimContractTests.cs` 覆盖 7 条用例：
- `Auto_ZeroHits_Is_Never_Absence`
- `Anchored_Relation_ZeroHits_Is_NoMatchInScope`
- `Explicit_Intent_NoAnchor_Is_NoCandidates`
- `NonAbsence_Reasons_Are_Never_Negation`
- `Empty_Result_Derives_Claim_When_Not_Given`
- `Hit_Result_Has_No_Claim`
- （其余 1 条同类用例略）

`RetrievalDeduplicationContractTests.cs` 覆盖 2 条用例：
- `A13_Cross_Scope_Dedup_Precedes_Overload_Judgement`
- `A14_Dual_Views_Carry_Hit_Reason`

`RetrievalEmptyReasonContractTests.cs` 覆盖 1 条用例：
- `A12_Filtered_Empty_Is_Not_Absence`

`RetrievalFilterContractTests.cs` 覆盖 1 条用例：
- `A15_Filter_Facets_Are_Orthogonal_And_Combined_With_And`

`RetrievalIntentContractTests.cs` 覆盖 2 条用例：
- `A01_Default_Intent_Is_Auto_And_Occupies_Zero`
- `A07_Explicit_Intent_Keeps_Its_Own_Layer_Order`

`RetrievalOverloadContractTests.cs` 覆盖 2 条用例：
- `A10_Overload_Is_Low_Specificity_Except_Legitimate_Bulk`
- `A11_Narrowing_Suggestions_Are_Limited_To_Four_Kinds`

`RetrievalRankingContractTests.cs` 覆盖 2 条用例：
- `A05_Comparer_Is_A_Deterministic_Total_Order`
- `A05b_Scope_Corroboration_Weight_Is_Deterministic_And_Monotonic`

`RetrievalResultContractTests.cs` 覆盖 4 条用例：
- `A02_No_Empty_Result_Without_Reason_Is_Representable`
- `A03_Degraded_Requires_Reason`
- `A04_At_Most_One_Next_Step`
- `A09_Truncation_Must_Spill_With_Total_Cursor_And_Distribution`

`SymbolAnchorContractTests.cs` 覆盖 9 条用例：
- `Anchor_AllowsEmptyQuery_OnRelationFace`
- `EmptyQuery_WithoutAnchor_IsRejected`
- `Anchor_WithAutoIntent_IsRejected_AndSaysWhichIntentsAreLegal`
- `Anchor_WithNameFaceIntent_IsRejected`
- `Anchor_AcceptsEveryRelationFaceIntent`
- `Anchor_BlankProjectId_IsRejectedAtConstruction`
- （其余 3 条同类用例略）

`SymbolRefV1Tests.cs` 覆盖 12 条用例：
- `Token_RoundTrip_PreservesAllFields`
- `Create_NormalizesPathAndSymbolId`
- `Create_RejectsPathTraversal`
- `Create_RejectsAbsoluteAndRootedPaths`
- `Create_RejectsMissingContentHash`
- `Create_RejectsMissingSymbolIdAndInvertedLines`
- （其余 6 条同类用例略）
