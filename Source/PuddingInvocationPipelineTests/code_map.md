# PuddingInvocationPipelineTests

扩展加载与排序的离线测试工程：只挂本工程与假扩展源，不碰宿主。

> 源指纹: Source/PuddingInvocationPipelineTests/**=pending · 条目数: 2 · 最近整理: 2026-11-22

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `PuddingInvocationPipelineTests.cs` | 覆盖注册表装载与扩展执行顺序，含代际与受保护字段拒绝 | `ExtensionRegistryTests`、`ExtensionPipelineTests`、`FakeExtensionSource`、`ExtensionFixtures` | `Source/PuddingInvocationPipeline/ExtensionPipeline.cs` | 断言按精确原因码比对；改错误码或排序规则必须同步改本文件 |
| `PuddingInvocationPipelineTests.csproj` | 定义 MSTest 运行器与本工程的唯一引用 | — | `Source/PuddingInvocationPipeline/PuddingInvocationPipeline.csproj` | `MSTest.Sdk/4.0.1` 且 `OutputType=Exe`、`UseVSTest=false`；唯一的 ProjectReference 是被测工程 |
