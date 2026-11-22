# PuddingReviews.Contracts
> 源指纹: *.cs=1-c1e31bb0c3c9, *.csproj=1-fb427c7337d2 · 条目数: 0 · 最近整理: 2026-11-22

评审任务端口契约（PA-03）：定义持久评审任务的键与结论建议，BCL-only，不映射成 GoalPhase。

> 源指纹: Source/PuddingReviews.Contracts/**=pending · 条目数: 2 · 最近整理: 2026-11-22

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `ReviewJobPorts.cs` | 定义评审任务的逻辑键、输入快照与建议式结论 | `IReviewJobStore`、`IReviewExecutor`、`IReviewEventOutbox`、`ReviewInputSnapshot`、`ReviewOutcome` | `Source/PuddingReviews/ReviewJobCoordinator.cs` | 终态与 `review.completed` 事实须在同一事务提交且重放不产生第二条事件；模型给的 Accept 不得越过证据与版本校验 |
| `PuddingReviews.Contracts.csproj` | 构建期锁死端口工程的零依赖边界 | — | — | `EnforceReviewsPortsBoundary` 目标会失败构建：出现任何 ProjectReference 或 PackageReference 即报错 |
