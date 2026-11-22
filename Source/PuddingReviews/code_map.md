# PuddingReviews
> 源指纹: *.cs=3-3347241e4954, *.csproj=1-8785c09bb35a · 条目数: 3 · 最近整理: 2026-11-22

评审（Review）实现：对输入快照与模型结论各设一道 fence，再以幂等持久作业推进到完成。

> 源指纹: Source/PuddingReviews/**=pending · 条目数: 4 · 最近整理: 2026-11-22

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `ReviewInputValidator.cs` | 校验快照是否属于该作业的评审 id 与逻辑键且未超出作业截止 | `ReviewInputValidator`、`ReviewInputValidation` | `PuddingReviews.Contracts` | 证据不完整只上报给结果校验器，绝不在这里修补；越界或键不符直接判无效 |
| `ReviewResultValidator.cs` | 把不可信结论一律降级成 Inconclusive | `ReviewResultValidator` | `ReviewInputValidator` | 调用方自带的证据永远不能得到 CheckVerified；Satisfied 要求每条判据都 Supported |
| `ReviewJobCoordinator.cs` | 幂等受理评审作业并用 owner-epoch 与 revision 栅栏推进执行 | `ReviewJobCoordinator`、`ReviewRunResult` | `IEventOutboxWriter`、`ReviewResultValidator` | 终态作业不得重跑模型；已取消作业拒绝迟到的完成，且不得写 Goal 终态 |
| `PuddingReviews.csproj` | 用构建期目标把可引用面锁到端口与公共 API 契约 | — | `../PuddingReviews.Contracts`、`../Pudding.Api.Contracts` | `EnforceReviewsBoundary` 只允许引用这两个工程，且禁止任何 PackageReference |
