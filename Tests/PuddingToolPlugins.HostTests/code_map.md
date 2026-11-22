# Plugin Host Tests
> 源指纹: *.cs=1-be4e775aaff5, *.csproj=1-e19a1a4f0542 · 条目数: 2 · 最近整理: 2026-11-09

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `PluginHostLoopTests.cs` | 真实 Agent 循环消费代次作用域内的插件工具。 | `PluginHostLoopTests` | `Source/PuddingRuntime/PuddingRuntime.csproj` | 能力与写效果闸门按作用域描述符判定。 |
| `TestScripts/test-pudding-tool-plugins.ps1` | 可复现的隔离构建测试入口。 | — | `Tests/PuddingToolPlugins.HostTests/PuddingToolPlugins.HostTests.csproj` | 不接生产 DataRoot 与外部 LLM。 |
