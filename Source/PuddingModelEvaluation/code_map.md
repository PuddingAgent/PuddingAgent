# PuddingModelEvaluation

模型评估的受限门面（PA-03/06）：把宿主资源池绑定成分类与验收评审 API，纯类库。

> 源指纹: Source/PuddingModelEvaluation/**=pending · 条目数: 2 · 最近整理: 2026-11-22

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `ModelEvaluationService.cs` | 把宿主资源池绑定成受限的模型分类与验收评审 API | `ModelEvaluationService`、`Bind`、`ClassifyAsync`、`ReviewAsync` | `Source/PuddingExecutionResources.Contracts/ExecutionResources.cs` | 未授予的能力抛 `permission.denied` 而不是返回空目录；模型输出不完整或不合契约一律降级为 `Degraded`，绝不算通过 |
| `PuddingModelEvaluation.csproj` | 构建期锁定只依赖公开契约与资源端口 | — | `Source/Pudding.Api.Contracts/Pudding.Api.Contracts.csproj` | `EnforceModelEvaluationBoundary` 目标会失败构建：引用其它工程、PackageReference 或宿主框架都报错 |
