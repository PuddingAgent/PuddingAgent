# Capability catalog ports
> 源指纹: *.cs=1-c5af111486e2, *.csproj=1-d7901d8c5793 · 条目数: 0 · 最近整理: 2026-11-22

能力目录端口的 BCL-only 契约：只读列出的条目与授权分页；权限缺口用 Partial 表达而非空目录。

> 源指纹: Source/PuddingCapabilityCatalog.Contracts/**=pending · 条目数: 2 · 最近整理: 2026-11-22

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `CapabilityCatalogPorts.cs` | 定义能力条目与分页页面，以及来源端口和类型化错误 | `CapabilityRecord`、`CapabilityPage`、`ICapabilitySource`、`CapabilityErrorCodes` | `CapabilityException` | 列出的条目永远不是执行授权；权限不足必须表现为 Partial 或 Forbidden |
| `PuddingCapabilityCatalog.Contracts.csproj` | 纯契约工程，无工程引用与包依赖 | — | `PuddingCapabilityCatalog.Contracts` | 保持 BCL-only |
