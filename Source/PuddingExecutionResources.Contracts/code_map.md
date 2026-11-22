# Execution resource ports

执行资源端口的契约：宿主持有的执行作用域、层级预算、模型描述与单次派发。

> 源指纹: Source/PuddingExecutionResources.Contracts/**=pending · 条目数: 4 · 最近整理: 2026-11-22

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `ExecutionResources.cs` | 定义执行作用域与层级预算、模型价格描述与资源池端口 | `ExecutionResourceScope`、`ModelResourceDescriptor`、`ModelUsage` | `ExecutionResourceLimits` | 单次派发语义：同一模型槽不得并发复用 |
| `DurableBudgetContracts.cs` | 跨轮持久预算端口：账户身份与单调快照、祖先路径与增删命令 | `BudgetIdentity`、`IExecutionBudgetLedger`、`BudgetLimits` | `BudgetReservation`、`DispatchPermit` | 产品必须只装配一个持久账本；内存实现仅供独立测试 |
| `ModelAttemptBudgetPort.cs` | 模型尝试级预算端口：按成本申领并返回授予或拒绝 | `IModelAttemptBudgetPort`、`ModelAttemptBudgetClaim`、`ModelAttemptBudgetGrant` | `ModelAttemptBudgetException` | 成本由调用方给出，端口不做价格推导 |
| `PuddingExecutionResources.Contracts.csproj` | 纯契约工程，无工程引用与包依赖 | — | `PuddingExecutionResources.Contracts` | 保持 BCL-only |
