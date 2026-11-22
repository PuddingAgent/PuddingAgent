# PuddingEventing.Contracts
> 源指纹: *.cs=2-8dd94d3648dd, *.csproj=1-5b47f4d6af37 · 条目数: 0 · 最近整理: 2026-11-22

事件与作业的纯端口契约（PA-07A）：只依赖 BCL，定义信封、订阅、投递与持久作业的稳定边界。

> 源指纹: Source/PuddingEventing.Contracts/**=pending · 条目数: 3 · 最近整理: 2026-11-22

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `EventSubscriptionPorts.cs` | 定义可靠订阅与至少一次投递的端口契约 | `ICommittedEventLog`、`ISubscriptionStore`、`IEventCheckpointStore`、`IEventOutboxWriter` | `PuddingEventing`、`PuddingEventingTests` | 保留期不足以回放时会置 `SnapshotRequired`，调用方必须显式重建而不是跳过缺口；ack 与游标推进必须在同一事务里 |
| `DurableJobPorts.cs` | 定义插件持久作业的端口契约 | `IPluginJobStore`、`PluginJobSnapshot`、`PluginJobBinding` | `PuddingEventing` | 作业状态归作业协议所有，不得复用 Review 或 Goal 状态；取消与状态迁移必须带 plugin generation 栅栏 |
| `PuddingEventing.Contracts.csproj` | 用构建期目标把本工程锁成纯 BCL 契约 | — | `PuddingEventing` | `EnforceEventingPortsBoundary` 禁止任何 ProjectReference 与 PackageReference，只允许 `Microsoft.NETCore.App` |
