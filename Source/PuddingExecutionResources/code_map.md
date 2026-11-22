# Execution resources
> 源指纹: *.cs=4-18f41890b270, *.csproj=1-b86b9f1a4519 · 条目数: 4 · 最近整理: 2026-11-22

执行资源的实现：层级预算作用域、模型槽资源池、持久预算协调与独立测试账本。

> 源指纹: Source/PuddingExecutionResources/**=pending · 条目数: 5 · 最近整理: 2026-11-22

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `ExecutionResourceScope.cs` | 原子层级预留与结算，含取消、关闭准入与不确定用量记账 | `ExecutionResourceScope.Reserve`、`ExecutionResourceScope.Settle` | `ExecutionResourceLimits` | 主循环外部用量属派发后记账，不能当作预留 |
| `ModelResourcePool.cs` | 模型槽池：恰好一次派发且排队可取消，迟到结果不提前释放资源 | `ModelResourcePool.InvokeAsync` | `ModelResourceDescriptor` | 同一槽未结算前不得二次派发 |
| `InMemoryExecutionBudgetLedger.cs` | 仅用于独立测试的持久预算账本实现 | `InMemoryExecutionBudgetLedger` | `IExecutionBudgetLedger` | 不是第二个生产账本；产品只装配一个持久实现 |
| `DurableBudgetCoordinator.cs` | 尝试协议：预留→派发→结算或置未知，另有纯恢复规划器 | `DurableBudgetCoordinator.BeginAsync`、`BudgetRecoveryPlanner` | `IExecutionBudgetLedger`、`IExecutionBudgetRecoveryLedger` | 已派发但结果不明必须保持 unknown，重启恢复不得退款 |
| `PuddingExecutionResources.csproj` | 实现工程，唯一工程引用为执行资源契约 | — | `PuddingExecutionResources.Contracts` | — |
