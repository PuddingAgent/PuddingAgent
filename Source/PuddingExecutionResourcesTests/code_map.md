# PuddingExecutionResourcesTests
> 源指纹: *.cs=3-01bffad045ab, *.csproj=1-b53e1bab1971 · 条目数: 0 · 最近整理: 2026-11-22

执行资源的独立测试工程：覆盖并发槽位、预派发退款与持久预算的准入、结算、重启对账不变量。

> 源指纹: Source/PuddingExecutionResourcesTests/**=pending · 条目数: 4 · 最近整理: 2026-11-22

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `ResourceTests.cs` | 覆盖槽位争用与预派发退款的资源不变量 | `ResourceTests` | `PuddingExecutionResources` | 只引用独立组件，不经过宿主；不配合的提供方必须仍保留槽位直至结算 |
| `DurableBudgetTests.cs` | 覆盖持久预算的祖先原子预留与一次结算不变量 | `DurableBudgetTests` | `PuddingExecutionResources` | 「未知」不等于零额度；伪造路径必须被拒绝，不能只靠调用方自律 |
| `DurableBudgetCoordinatorTests.cs` | 覆盖预算耗尽的短路、重启对账与迟到结算 | `DurableBudgetCoordinatorTests` | `PuddingExecutionResources` | 预算耗尽时提供方不得被调用；已派发但结果未知的句柄不得自动重试 |
| `PuddingExecutionResourcesTests.csproj` | MSTest.Sdk 的可执行测试工程，只引用执行资源组件 | — | `../PuddingExecutionResources` | `UseVSTest=false`，产物是自宿主测试可执行文件 |
