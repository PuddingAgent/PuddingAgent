# PuddingAgentConfiguration
> 源指纹: *.cs=1-868cfa1f0265, *.csproj=1-20e1284ee8b5 · 条目数: 2 · 最近整理: 2026-11-09

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `AgentConfigurationService.cs` | 配置的授权校验与 CAS 幂等写入实现。 | `AgentConfigurationService` | `Source/PuddingAgentConfiguration.Contracts/ConfigurationPorts.cs` | 写路径必须走原子变更记录，不做部分写入。 |
| `PuddingAgentConfiguration.csproj` | 配置实现工程，编译期限定只引用端口与值契约。 | `EnforceConfigurationBoundary` | — | BCL 加端口，不得有 PackageReference。 |
