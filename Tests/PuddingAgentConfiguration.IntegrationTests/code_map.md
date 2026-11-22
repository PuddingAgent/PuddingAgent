# PuddingAgentConfiguration.IntegrationTests
> 源指纹: *.cs=2-be194bca5770, *.csproj=1-a9b409fce347 · 条目数: 2 · 最近整理: 2026-11-12

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `ConfigurationIntegrationTests.cs` | 真实清单持久化、服务重建与 JWT 往返验收。 | `ConfigurationIntegrationTests` | `Source/PuddingAgentConfiguration/AgentConfigurationService.cs` | 使用隔离数据目录，不碰生产 DataRoot。 |
| `WorkspaceAgentRouteTests.cs` | 逐条核对路由表只落到唯一意图动作。 | `WorkspaceAgentRouteTests` | `Source/PuddingPlatform/Controllers/Api/WorkspaceAgentApiController.cs` | 同一路径不得命中两个动作。 |
