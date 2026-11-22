# PuddingGoalPolicy.Contracts

Goal 续跑建议的纯契约（PA-04）：只定义事实快照与四类建议，BCL-only，不写库、不调模型、不复制 Goal 状态机。

> 源指纹: Source/PuddingGoalPolicy.Contracts/**=pending · 条目数: 2 · 最近整理: 2026-11-22

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `GoalPolicyContracts.cs` | 定义续跑建议所需的事实快照与四类建议 | `IGoalContinuationPolicy`、`GoalPolicyFacts`、`GoalPolicyDecision`、`GoalPolicyErrorCodes` | `Source/PuddingGoalPolicy/GoalContinuationPolicy.cs` | 建议须再经既有 calculator 与状态机校验才能提交；`ReviewFact.Unknown` 与 `Missing` 都不算已满足 |
| `PuddingGoalPolicy.Contracts.csproj` | 构建期锁死端口工程的零依赖边界 | — | — | `EnforceGoalPolicyPortsBoundary` 目标会失败构建：出现任何 ProjectReference 或 PackageReference 即报错，FrameworkReference 仅允许基础运行时 |
