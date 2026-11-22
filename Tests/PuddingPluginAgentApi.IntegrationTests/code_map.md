# Plugin Agent API Integration Tests
> 源指纹: *.cs=1-40b11a77e286, *.csproj=1-396bfc5ac950 · 条目数: 2 · 最近整理: 2026-11-09

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `PluginAgentApiTests.cs` | 真实 Worker 与规范清单写入方的配置读写验收。 | `PluginAgentApiTests` | `Tests/PuddingToolPlugins.Fixture/AgentConfigurationPlugin.cs` | 陈旧后台回调必须被拒绝。 |
| `TestScripts/test-plugin-agent-api.ps1` | 隔离的组件与真实 Windows Worker 验收入口。 | — | `Tests/PuddingPluginAgentApi.IntegrationTests/PuddingPluginAgentApi.IntegrationTests.csproj` | 不接产品 Core 与生产数据。 |
