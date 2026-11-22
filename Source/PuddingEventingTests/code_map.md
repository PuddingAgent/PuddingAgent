# PuddingEventingTests
> 源指纹: *.cs=4-0af05c9ea95d, *.csproj=1-f7f07d497c7d · 条目数: 0 · 最近整理: 2026-11-22

事件投递的独立测试工程：用内存适配器覆盖授权过滤、租约重投、checkpoint 原子性与作业栅栏。

> 源指纹: Source/PuddingEventingTests/**=pending · 条目数: 5 · 最近整理: 2026-11-22

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `InMemoryEventingHost.cs` | 单锁内存版全端口适配器，附保留窗口与并发游标测试钩子 | `InMemoryEventingHost` | `ICommittedEventLog`、`ISubscriptionStore`、`IEventDeliveryStore` | 只是测试替身，不是产品实现；它的语义放宽不得反推产品行为 |
| `SubscriptionTests.cs` | 覆盖订阅授权过滤与游标连续性、重投与死信重放 | `SubscriptionTests` | `SubscriptionDispatcher`、`InMemoryEventingHost` | 两个 dispatcher 并发场景必须断言无重复投递，否则重投逻辑回归不可见 |
| `DeliveryRecoveryTests.cs` | 覆盖作业提交幂等与 generation 栅栏下的取消、状态迁移 | `DeliveryRecoveryTests` | `DeliveryCoordinator`、`HostPluginBinding` | 陈旧 revision 或已取消订阅上的 checkpoint 必须被拒绝 |
| `EventingTestFixtures.cs` | 提供默认插件绑定与允许 kind 的夹具 | `EventingTestFixtures` | `HostPluginBinding` | 默认 kind 为 `review.completed`，改夹具会同时影响两个测试类 |
| `PuddingEventingTests.csproj` | MSTest.Sdk 的可执行测试工程，只引用 Eventing 实现 | — | `../PuddingEventing` | `UseVSTest=false`，产物是自宿主测试可执行文件，不能按 vstest 方式收集 |
