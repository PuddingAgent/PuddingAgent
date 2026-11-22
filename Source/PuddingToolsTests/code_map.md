# PuddingToolsTests

工具门面的离线测试工程：内存工具目录，不加载真实工具也不执行工具。

> 源指纹: Source/PuddingToolsTests/**=pending · 条目数: 3 · 最近整理: 2026-11-22

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `ToolTestHost.cs` | 提供可控工具目录与显式未知可用性的假源 | `FakeToolSource`、`ToolFixtures` | `Source/PuddingTools.Contracts/ToolFacadePorts.cs` | 这些是测试替身而不是产品实现，不得被生产工程引用 |
| `ToolFacadeTests.cs` | 覆盖可见性过滤与查询边界校验 | `ToolFacadeTests` | `Source/PuddingTools/ToolFacade.cs` | 判据分别落在「查询前失败」与「源失败要类型化」，两者不可混为一条断言 |
| `PuddingToolsTests.csproj` | 定义 MSTest 运行器与本工程的唯一引用 | — | `Source/PuddingTools/PuddingTools.csproj` | `MSTest.Sdk/4.0.1` 且 `OutputType=Exe`、`UseVSTest=false`；唯一的 ProjectReference 是被测工程 |
