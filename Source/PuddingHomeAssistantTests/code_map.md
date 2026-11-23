# PuddingHomeAssistantTests
> 源指纹: *.cs=6-46800b5f190a, *.csproj=1-a618544355e2 · 条目数: 0 · 最近整理: 2026-11-23

测试工程，引用 PuddingHomeAssistant；用例全部为进程内单测，不需要真实宿主或外部服务。

`ComponentBoundaryTests.cs` 覆盖 4 条用例：
- `Component_DoesNotReferenceAnyPuddingUpperLayer`
- `Component_DoesNotReferenceEfCoreOrAspNetCore`
- `ControlGroup_SystemTextJsonIsActuallyReferenced`
- `ControlGroup_ComponentItselfIsInClosure`

`HaDomainPolicyTests.cs` 覆盖 7 条用例：
- `PermanentlyBlockedDomains_AreBlocked_EvenWhenOptedIn`
- `DefaultAllowedDomains_NeedNoOptIn`
- `OptInDomains_AreRejectedUntilEnabled`
- `UnknownDomain_FailsClosed`
- `MalformedDomain_IsBlocked`
- `Normalize_LowercasesAndTrims`
- （其余 1 条同类用例略）

`HaErrorCodesTests.cs` 覆盖 3 条用例：
- `FromHttpStatus_MapsToStableCodes`
- `FromWebSocketCode_MapsToStableCodes`
- `AllCodes_AreNamespacedAndUnique`

`HaHistoryBudgetTests.cs` 覆盖 7 条用例：
- `UnderBudgetIsReturnedVerbatim`
- `OverBudgetResultStaysParseable`
- `TrimmingKeepsOrderAndReportsDroppedEntities`
- `FirstEntityTooLargeIsTrimmedToStatePrefix`
- `NonArrayRootIsReportedWithoutMutation`
- `TinyBudgetStillProducesValidJson`
- （其余 1 条同类用例略）

`HaOutputBudgetTests.cs` 覆盖 14 条用例：
- `NormalizeLimit_ClampsToBounds`
- `Project_FiltersByDomain_AndReportsTotals`
- `Project_EntityIdTakesPriorityOverDomains`
- `Project_TruncatesWithExplicitFlags`
- `Project_OmitsAttributesUnlessRequested`
- `Project_StaysWithinByteBudget_WhenAttributesAreHuge`
- （其余 8 条同类用例略）

`HaRestClientTests.cs` 覆盖 11 条用例：
- `GetStates_ParsesObjects`
- `AuthHeader_CarriesBearerToken`
- `CallService_BlockedDomain_SendsNoRequest`
- `CallService_OptInDomainWithoutPermission_SendsNoRequest`
- `CallService_OptInDomainWhenEnabled_Succeeds`
- `GetState_NotFound_MapsToStableCode`
- （其余 5 条同类用例略）
