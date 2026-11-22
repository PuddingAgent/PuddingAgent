# Agents
> 源指纹: *.cs=5-b76ecdc55dc9, *.csproj=1-7f0e3b108c4e · 条目数: 6 · 最近整理: 2026-11-21

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `AgentOwnershipPolicy.cs` | 宿主绑定身份校验，子级限额只能收紧。 | `AgentOwnershipPolicy` | `Source/Pudding.Api.Contracts/AgentDelegation.cs` | 调用方字符串不得放大已绑定作用域。 |
| `AgentDirectoryService.cs` | 借用句柄的目录读取与分页边界。 | `AgentDirectoryService` | `Source/Pudding.Api.Contracts/AgentDirectory.cs` | 缺席只能由 Get 判定，截断页不等于不存在。 |
| `AgentWorkControlService.cs` | 观测绑定操作与事件游标，取消为持久幂等命令。 | `AgentWorkControlService` | `Source/Pudding.Api.Contracts/AgentWorkControl.cs` | Requested 不得声明已终止；游标被清理返回 snapshot_required。 |
| `AgentDelegationService.cs` | 委派注入父工作并校验父等值与子限额。 | `AgentDelegationService` | `Source/Pudding.Api.Contracts/AgentDelegation.cs` | 租约获取与释放走原子池，按 owner 与 generation 围栏。 |
| `AgentAdapterRegistry.cs` | 按工作区 Agent 解析活跃外部适配器。 | `AgentAdapterRegistry` | `Docs/12_features/外部Agent接入与集中消息总线-2026-11-21.md` | 注册、替换、续租与释放在宿主侧串行并围栏。 |
| `PuddingAgents.csproj` | Agents 工程，编译期限定只引用端口与公共契约。 | `EnforceAgentsBoundary` | — | 只允许 PuddingAgents.Contracts 与 Pudding.Api.Contracts，无包引用。 |
