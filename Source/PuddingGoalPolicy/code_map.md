# PuddingGoalPolicy

Goal 续跑决策的纯函数实现（PA-04）：只从冻结局实推导建议，不写状态、不调模型、不跑工具。

> 源指纹: Source/PuddingGoalPolicy/**=pending · 条目数: 2 · 最近整理: 2026-11-22

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `GoalContinuationPolicy.cs` | 从冻结事实推导续跑建议并给出稳定的下一轮键 | `GoalContinuationPolicy`、`Decide`、`NextRoundKey` | `Source/PuddingGoalPolicy.Contracts/GoalPolicyContracts.cs` | 取消与暂停优先于其余判断；版本过期不得沿用旧通过结论；预算或轮次耗尽只能 Stop，不得自行放宽 |
| `PuddingGoalPolicy.csproj` | 构建期锁死本工程只依赖自己的端口工程 | — | `Source/PuddingGoalPolicy.Contracts/PuddingGoalPolicy.Contracts.csproj` | `EnforceGoalPolicyBoundary` 目标会失败构建：引用非 Contracts 工程或任何 PackageReference 都报错 |
