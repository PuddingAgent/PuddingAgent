# Capability catalog
> 源指纹: *.cs=1-69bf5086a055, *.csproj=1-6b231f26d5c9 · 条目数: 1 · 最近整理: 2026-11-22

能力目录的只读门面：在触达来源之前校验种类与边界，并原样保留修订号与完整性。

> 源指纹: Source/PuddingCapabilityCatalog/**=pending · 条目数: 2 · 最近整理: 2026-11-22

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `CapabilityCatalogService.cs` | 目录查询门面：校验 kind 与边界后转发游标与页大小，保留修订/世代/可用性 | `CapabilityCatalogService.ListAsync` | `ICapabilitySource`、`CapabilityPage` | 禁止页或部分页不得折成「没有能力」；来源失败要抛类型化错误 |
| `PuddingCapabilityCatalog.csproj` | 实现工程，引用能力目录契约与 API 契约 | — | `PuddingCapabilityCatalog.Contracts`、`Pudding.Api.Contracts` | — |
