# PuddingEventing
> 源指纹: *.cs=2-c5201a8da53f, *.csproj=1-29e2be0a15f6 · 条目数: 2 · 最近整理: 2026-11-22

事件投递实现：把端口契约落成服务端的订阅调度与插件侧宿主适配器，只做投递，不含 Goal/Review 业务。

> 源指纹: Source/PuddingEventing/**=pending · 条目数: 3 · 最近整理: 2026-11-22

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `SubscriptionDispatcher.cs` | 服务端投递驱动：按授权过滤后批量领取并至少一次重租约 | `SubscriptionDispatcher`、`EventDispatchResult` | `IEventCheckpointStore`、`IEventDeliveryStore` | ack 必须连续，checkpoint 与游标推进在同一事务内完成；保留期不足时返回 `SnapshotRequired` 而不是跳洞 |
| `DeliveryCoordinator.cs` | 插件侧宿主适配器：绑定身份与预算并校验 generation 栅栏 | `DeliveryCoordinator`、`HostPluginBinding` | `IPluginJobStore`、`SubscriptionDispatcher` | 未授予的 kind 与 capability 一律拒绝；提交作业只回票据，不得同步等待执行 |
| `PuddingEventing.csproj` | 用构建期目标把可引用面锁到端口与公共 API 契约 | — | `../PuddingEventing.Contracts`、`../Pudding.Api.Contracts` | `EnforceEventingBoundary` 只允许引用这两个工程，且禁止任何 PackageReference |
