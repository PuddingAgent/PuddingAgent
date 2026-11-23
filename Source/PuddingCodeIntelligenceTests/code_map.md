# PuddingCodeIntelligenceTests
> 源指纹: *.cs=1-26ea30afb60b, *.csproj=1-11ae2807fb6d · 条目数: 0 · 最近整理: 2026-11-23

测试工程，引用 PuddingCodeIndex、PuddingCodeIntelligence；用例全部为进程内单测，不需要真实宿主或外部服务。

`ComponentBoundaryTests.cs` 覆盖 4 条用例：
- `Boundary_Detector_Flags_Forbidden_Names`
- `ComponentCsproj_DeclaresOnlyCodeIndex`
- `TestCsproj_ReferencesComponentExplicitly_AndNotRuntime`
- `ComponentAssembly_DoesNotReferenceUpperLayers`
