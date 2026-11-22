# PuddingAgentConfiguration.Contracts
> 源指纹: *.cs=1-c8d60b0bbd40, *.csproj=1-4ef2de6a8643 · 条目数: 2 · 最近整理: 2026-11-09

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `ConfigurationPorts.cs` | 受信调用方、授权端口与原子存储事务契约。 | `IAgentConfigurationAuthorizer`、`IAgentConfigurationStore`、`ConfigurationTransaction` | `Source/Pudding.Api.Contracts` | — |
| `PuddingAgentConfiguration.Contracts.csproj` | 配置端口工程，编译期限定只引用公共值契约。 | `EnforceConfigurationPortBoundary` | — | BCL-only，只允许引用 Pudding.Api.Contracts。 |
