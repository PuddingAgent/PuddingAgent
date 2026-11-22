# PuddingTools.Contracts

工具门面端口契约（PA-05B）：只给搜索、检查与状态读取，不含执行入口。

> 源指纹: Source/PuddingTools.Contracts/**=pending · 条目数: 2 · 最近整理: 2026-11-22

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `ToolFacadePorts.cs` | 定义工具描述、查询与状态读取的统一端口 | `IToolCatalogSource`、`ToolDescriptor`、`ToolQuery`、`ToolState`、`ToolErrorCodes` | `Source/PuddingTools/ToolFacade.cs` | 不可见工具不得在搜索或状态里泄露存在性；未知可用性用字符串原样保留，不伪造可用 |
| `PuddingTools.Contracts.csproj` | 构建期锁死端口工程的零依赖边界 | — | — | `EnforceToolFacadePortsBoundary` 目标会失败构建：出现任何 ProjectReference 或 PackageReference 即报错 |
