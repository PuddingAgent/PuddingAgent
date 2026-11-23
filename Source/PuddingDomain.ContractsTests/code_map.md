# PuddingDomain.ContractsTests
> 源指纹: *.cs=2-a5869f1f2e62, *.csproj=1-9bb68ab9c364 · 条目数: 0 · 最近整理: 2026-11-23

测试工程，引用 PuddingDomain.Contracts；用例全部为进程内单测，不需要真实宿主或外部服务。

`ComponentBoundaryTests.cs` 覆盖 3 条用例：
- `Contracts_ReferencesNothingFromPudding`
- `TestProject_DoesNotReferenceImplementations`
- `ContractsCsproj_DeclaresNoForbiddenProjectReferences`

`EnumStabilityTests.cs` 覆盖 3 条用例：
- `CodeSymbolKind_NumericValues_ArePinned`
- `CodeRelationKind_NumericValues_ArePinned`
- `MemberCounts_ArePinned`
