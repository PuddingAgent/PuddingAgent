# PuddingWorkspaceTests
> 源指纹: *.cs=2-94ad5e61ddde, *.csproj=1-125374178020 · 条目数: 0 · 最近整理: 2026-11-22

工作区目录与文件策略的离线测试工程：假目录源与假文件存储，不触真实磁盘。

> 源指纹: Source/PuddingWorkspaceTests/**=pending · 条目数: 3 · 最近整理: 2026-11-22

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `WorkspaceTestHost.cs` | 提供假目录源与假文件存储 | `FakeWorkspaceSource`、`FakeFileStore`、`WorkspaceFixtures` | `Source/PuddingWorkspace.Contracts/WorkspacePorts.cs` | 这些是测试替身而不是产品实现，不得被生产工程引用 |
| `WorkspaceTests.cs` | 覆盖路径策略与工作区读写约束 | `WorkspacePathPolicyTests`、`WorkspaceCatalogTests` | `Source/PuddingWorkspace/WorkspaceFilePolicy.cs`、`Source/PuddingWorkspace/WorkspaceCatalog.cs` | 中文嵌套路径必须被接受；含分隔符或回溯的工作区 id 必须在校验阶段被拒 |
| `PuddingWorkspaceTests.csproj` | 定义 MSTest 运行器与本工程的唯一引用 | — | `Source/PuddingWorkspace/PuddingWorkspace.csproj` | `MSTest.Sdk/4.0.1` 且 `OutputType=Exe`、`UseVSTest=false`；唯一的 ProjectReference 是被测工程 |
