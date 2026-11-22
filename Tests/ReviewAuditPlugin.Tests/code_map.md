# Tests/ReviewAuditPlugin.Tests
> 源指纹: *.cs=1-c784d752a0ab, *.csproj=1-048729f3d14e · 条目数: 3 · 最近整理: 2026-11-22

评审审计插件的离线测试工程：内存事件上下文与审计假源，不起插件进程、不接宿主。

> 源指纹: Tests/ReviewAuditPlugin.Tests/**=pending · 条目数: 2 · 最近整理: 2026-11-22

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `ReviewAuditPluginTests.cs` | 覆盖 review.completed 到审计记录的字段映射 | `ReviewAuditPluginTests` | `Samples/Plugins/ReviewAudit/ReviewAudit.csproj` | 重放同一事件必须保持同一 OperationId，插件不自行去重也不另生成 GUID；只有规范化审计文本可逐字断言 |
| `ReviewAuditPlugin.Tests.csproj` | 定义 MSTest 运行器与本工程的唯一引用 | — | `Samples/Plugins/ReviewAudit/ReviewAudit.csproj` | `MSTest.Sdk/4.0.1` 且 `OutputType=Exe`、`UseVSTest=false`；唯一 ProjectReference 是样例插件工程 |
