# PuddingDesktop.GuardianTests 代码地图
> 源指纹: TestResults/**=3-f8537aad6009, *.cs=3-7d99e00be20e, *.csproj=1-e06ea3aab72d · 条目数: 2 · 最近整理: 2026-11-22

Guardian 的独立 xUnit 测试工程：只引用 Guardian，用假时钟与隔离目录验证状态机、部署保护与工作区回收。

> 源指纹: Source/PuddingDesktop.GuardianTests/**=pending · 条目数: 4 · 最近整理: 2026-11-22

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `GuardianTests.cs` | 状态机与包保护：覆盖持久退出、部署排队与请求去重；失败即回滚 | `GuardianTests.FailedHealthRollsBack` | `PuddingDesktop.Guardian` | 用假时钟与隔离目录，不启动产品；唯一产品引用为 Guardian |
| `GuardianHttpServerStatusTests.cs` | 状态端点暴露受监管的 Desktop 与 Core 进程号 | `GuardianHttpServerStatusTests.Status_ExposesSupervisedDesktopAndCorePid` | `PuddingDesktop.Guardian` | — |
| `GuardianWorkspaceRetentionTests.cs` | 工作区回收：保留最新两个并保护时间窗内目录，根缺失时不动 | `GuardianWorkspaceRetentionTests.Prune_DeletesOldestAndKeepsTheTwoNewest` | `PuddingDesktop.Guardian` | — |
| `PuddingDesktop.GuardianTests.csproj` | xUnit 测试工程，唯一工程引用为 Guardian | — | `PuddingDesktop.Guardian` | — |
