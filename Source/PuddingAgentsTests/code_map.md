# Agent component tests
> 源指纹: *.cs=6-840ba0e94de7, *.csproj=1-a5c46a75a827 · 条目数: 0 · 最近整理: 2026-11-22

Agent 组件的端口级测试：用内存假后端验证作用域、分页、取消与租约栅栏语义。

> 源指纹: Source/PuddingAgentsTests/**=pending · 条目数: 7 · 最近整理: 2026-11-22

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `AgentTestHost.cs` | 为每个 Agent 端口提供内存假后端与租约池，供各测试共享 | `AgentTestHost` | `IAgentDirectoryStore`、`IWorkEventSource` | 这是测试替身，不是产品实现 |
| `AgentDirectoryTests.cs` | 目录读取：工作区作用域内分页，截断页不当作不存在 | `AgentDirectoryTests.List_ReportsCursorAndComplete_WithoutClaimingAbsence` | `IAgentDirectoryStore` | — |
| `AgentWorkControlTests.cs` | 工作事件与控制：终态映射与保留期语义，取消幂等且冲突可检 | `AgentWorkControlTests.Cancel_IsRequestedOnce_AndReplayReturnsTheOriginalReceipt` | `IWorkEventSource` | — |
| `DelegationOwnershipTests.cs` | 委派归属：拒绝伪造父身份，获取原子且释放受世代栅栏 | `DelegationOwnershipTests.Acquire_IsAtomic_TwoConcurrentCallersOnlyOneWins` | `AgentBinding` | — |
| `AgentAdapterRegistryTests.cs` | 适配器注册表：按工作区路由并栅栏并发注册，租约可续期 | `AgentAdapterRegistryTests.RegistryRoutesThreeAdaptersByWorkspaceAndLogicalAgent` | `AgentAdapterLease` | — |
| `AgentReadScopeTests.cs` | 只读路径不需要工作或预算授权，异工作区读取必须被拒 | `AgentReadScopeTests.DirectoryRead_DoesNotRequireWorkOrBudget` | `IAgentDirectoryStore` | — |
| `PuddingAgentsTests.csproj` | 测试可执行工程，唯一工程引用为 `PuddingAgents` | — | `PuddingAgents` | — |
