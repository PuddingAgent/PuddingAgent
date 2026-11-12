---
title: PA-03 live binder 与唯一持久预留
author: hyfree
date: 2026-11-06
last_reviewed: 2026-11-06
status: archived
description: "新增 DurableModelResourcePool（持久 gate 唯一预留/结算/Unknown）与 ReviewEvaluationBinder（route+lease+ModelEvaluationService）；端到端 fake provider 3 条测试；修复 Unknown 未释放 ActiveAttempts。"
categories: [docs, changelog]
tags: [review, binder, budget, p2, pa03]
related_docs: [Docs/12_features/Agent组件化S5架构裁定与接线门禁-2026-11-06.md]
related_files: [Source/PuddingPlatform/Services/Reviews/DurableModelResourcePool.cs, Source/PuddingPlatform/Services/Reviews/ReviewEvaluationBinder.cs, Source/PuddingPlatform/Services/ExecutionBudgets/ExecutionBudgetStore.cs, Source/PuddingPlatformTests/Services/ReviewEvaluationBinderTests.cs]
slug: changelog-pa03-live-binder-20261106
draft: false
---

# PA-03 live binder 与唯一持久预留

实现裁定 §5“模型执行边界统一预留/结算”的离线闭环；未 commit、未部署、未调用真实模型。

## 1 唯一预留

- 新增 `DurableModelResourcePool`（`IModelResourcePool`）：provider 调用边界改为
  报价 → `ModelAttemptBudgetGate` 祖先预留 → 持久派发确认 → provider → 结算/Unknown；
  不再使用内存 scope 预留（scope 仅承载 identity/deadline/cancellation），避免双扣。
- provider 失败/取消/无 usage：`model.failed` / `model.cancelled` / `model.usage_unknown` → 账本 Unknown，不退款。

## 2 live binder

- 新增 `IReviewModelLeaseSource`（host-owned live model acquisition）与 `ReviewEvaluationBinder`：
  `ReviewModelRouter` 解析 actor/Review purpose/冻结 revision → 取得 `ILeasedModel` → `ModelEvaluationService.Bind` 返回只授 review 的 `IModelEvaluationApi`（backed by durable pool）。
- 路由失败、预算拒绝、绑定异常都映射为 typed `ReviewException`，不会静默换成默认模型。

## 3 顺带修复

- `ExecutionBudgetStore.MarkUnknownAsync` 原先只把 Reserved 移到 Unknown，**未释放 ActiveAttempts**；已修正为 Unknown 时递减（仅 Dispatched→Unknown 一次），避免并发额度被永久占用。该缺陷由 live binder 失败路径断言暴露。

## 4 测试与验证

- `ReviewEvaluationBinderTests` 3/3（真实 SQLite + fake provider）：
  - 成功：唯一预留+实际 usage 结算，Cost>0、Active=0、Unknown=0；
  - provider 失败：UnknownCost>0、Active=0、executor typed 失败；
  - 预算不足：provider **未被调用**、Active=0。
- 与预算存储/调和回归合计 **16/16**；Platform S5 切片 **126/126**；全解决方案构建 exit 0（restore + --no-restore）；一键脚本 RunId 20261106-r18 exit 0（组件 274 + Platform 126 + 前端 47 + tsc）。
- 证据：`.work/pa03/live-binder/`、`.work/verification/live-binder-solution/`、`.work/pa08/components-20261106-r18/`。

## 5 未完成

- 真实 `IReviewModelLeaseSource`（provider gateway/租约）与真实 `IAgentModelPolicyStore` 适配未接；`ReviewExecutor`/`ReviewJobWorker` 因此仍未注册 Host DI。
- 流式/子代理预算、Goal 自动续跑、08B/08C（NotRun）保持原状。
