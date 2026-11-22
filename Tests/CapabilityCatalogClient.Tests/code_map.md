# Tests/CapabilityCatalogClient.Tests

能力目录样例的离线测试工程：假目录 API，不接真实网络、不调用工具、不开 MCP 会话。

> 源指纹: Tests/CapabilityCatalogClient.Tests/**=pending · 条目数: 2 · 最近整理: 2026-11-22

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `CatalogDemoTests.cs` | 覆盖能力列表分页字段与拒绝原因保留 | `CatalogDemoTests` | `Samples/CapabilityCatalogClient/CapabilityCatalogClient.csproj` | 不完整页必须保留 `partial` 与 `missingReason`；kind 区分大小写，非法或空值走 exit 2 |
| `CapabilityCatalogClient.Tests.csproj` | 定义 MSTest 运行器与本工程的唯一引用 | — | `Samples/CapabilityCatalogClient/CapabilityCatalogClient.csproj` | `MSTest.Sdk/4.0.1` 且 `OutputType=Exe`、`UseVSTest=false`；唯一 ProjectReference 是样例工程 |
