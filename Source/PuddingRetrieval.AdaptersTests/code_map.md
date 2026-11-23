# PuddingRetrieval.AdaptersTests
> 源指纹: *.cs=2-b6274f895cbd, *.csproj=1-e9b79af7997b · 条目数: 0 · 最近整理: 2026-11-23

测试工程，引用 PuddingRetrieval.Adapters；用例全部为进程内单测，不需要真实宿主或外部服务。

`CodeIndexStoreSymbolSourceTests.cs` 覆盖 5 条用例：
- `NullStore_Throws`
- `ListProjects_ForwardsScopeId`
- `GetSymbol_ForwardsAllThreeIdentifiers`
- `IncomingRelations_ForcesLegacyRelationKindToNull`
- `OutgoingRelations_RoutesToStoreListRelations`

`ComponentBoundaryTests.cs` 覆盖 3 条用例：
- `Boundary_Detector_Flags_Forbidden_Names`
- `AdaptersCsproj_DeclaresExactlyTheTwoAllowedProjectReferences`
- `Dependency_Closure_ContainsBothSides_AndNoUpperLayers`
