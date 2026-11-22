# PuddingWorkspace.Contracts
> 源指纹: *.cs=1-41bc7b4aa5bd, *.csproj=1-3b3a96d45f16 · 条目数: 0 · 最近整理: 2026-11-22

工作区端口契约（PA-05F）：只暴露受权逻辑信息，真实路径与持久存储由适配器实现。

> 源指纹: Source/PuddingWorkspace.Contracts/**=pending · 条目数: 2 · 最近整理: 2026-11-22

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `WorkspacePorts.cs` | 定义受权工作区目录与文件读写端口 | `IWorkspaceCatalogSource`、`IWorkspaceFileStore`、`WorkspaceWriteReceipt`、`WorkspaceErrorCodes` | `Source/PuddingWorkspace/WorkspaceCatalog.cs` | 环境快照只能是逻辑信息，不得 dump 环境变量或控制令牌；写入按期望内容哈希做 CAS 并由适配器原子落盘 |
| `PuddingWorkspace.Contracts.csproj` | 构建期锁死端口工程的零依赖边界 | — | — | `EnforceWorkspacePortsBoundary` 目标会失败构建：出现任何 ProjectReference 或 PackageReference 即报错 |
