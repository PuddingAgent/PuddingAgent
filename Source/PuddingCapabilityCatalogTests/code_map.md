# Capability catalog tests
> 源指纹: *.cs=1-c5e80dd9b4ed, *.csproj=1-5b7c710e56ee · 条目数: 0 · 最近整理: 2026-11-22

能力目录门面的测试：种类与边界校验先于来源，分页与可用性原样保留。

> 源指纹: Source/PuddingCapabilityCatalogTests/**=pending · 条目数: 2 · 最近整理: 2026-11-22

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `CapabilityCatalogTests.cs` | 覆盖种类与边界校验、禁止页非空成功，以及部分页保留游标 | `CapabilityCatalogTests.List_ForbiddenSourcePage_IsNotAnEmptySuccess` | `CapabilityCatalogService` | 使用文件内假来源，不加载宿主或平台 |
| `PuddingCapabilityCatalogTests.csproj` | 测试可执行工程，唯一工程引用为 `PuddingCapabilityCatalog` | — | `PuddingCapabilityCatalog` | — |
