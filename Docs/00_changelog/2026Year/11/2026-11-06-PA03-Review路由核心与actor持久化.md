---
title: PA-03 Review 路由核心与 actor 持久化
author: hyfree
date: 2026-11-06
last_reviewed: 2026-11-06
status: archived
description: "Review Job 增加可信 ActorId 并持久化；ReviewModelRouter 复用 06B Review purpose（仅显式 InheritPrimary 继承）、冻结目录 revision/pricing 版本；新增 5 条测试。"
categories: [docs, changelog]
tags: [review, binder, model-policy, actor, pa03, pa06]
related_docs: [Docs/12_features/Agent组件化S5架构裁定与接线门禁-2026-11-06.md]
related_files: [Source/PuddingReviews.Contracts/ReviewJobPorts.cs, Source/PuddingPlatform/Services/Reviews/ReviewModelRouter.cs, Source/PuddingPlatform/Services/ExecutionBudgets/ExecutionBudgetStore.cs, Source/PuddingPlatformTests/Services/ReviewModelRouterTests.cs]
slug: changelog-pa03-review-model-router-20261106
draft: false
---

# PA-03 Review 路由核心与 actor 持久化

实现裁定 §5 的 binder 路由核心（复用 06B Review purpose、显式继承、可信 actor），未 commit、未部署、未调用真实模型。

## 1 actor 持久化

- `ReviewJobRecord`/`ReviewJobView` 增加可选 `ActorId`（受理记录中的原始委托者），`review_jobs.actor_id` 列持久化；`ReviewJobStore` 写入/映射。
- `ReviewModelRouter` 缺 actor 时返回 `review.actor_unavailable`，**不用 AgentId 冒充调用者**。

## 2 路由核心

- 新增 `ReviewModelRouter`：
  - 从预算账户读取 host-bound `BudgetIdentity`（workspace/agent/session）；
  - 以 `AgentProfileScope(workspace, agent, actor)` 调 `IAgentModelPolicyStore` + `ModelPolicyResolver` 解析 **Review** purpose；
  - 未配置 → typed `model.policy_missing`；只有显式 `InheritPrimary=true` 才借用 Primary；
  - 用 `IModelCatalogSource` 冻结 `ModelRevision` 与 `PricingRuleVersion`（供 P2 预算 claim），目录缺 revision → `model.capability_missing`。
- 输出 `ReviewModelBinding`（model/revision/priceVersion/costUnit/inherited/budget account/identity）。

## 3 测试与验证

- 新增 5 条：actor 缺失 typed、Review purpose 解析并冻结目录版本、显式 InheritPrimary 继承而隐式不继承、目录缺 revision typed、actor 持久化。
- `ReviewModelRouterTests`（4）+ `ReviewJobStoreTests`（11）**15/15**。
- Platform S5 切片 **121/121**；全解决方案构建 exit 0；一键脚本 RunId 20261106-r16 exit 0（组件 274 + Platform 121 + 前端 47 + tsc）。
- 证据：`.work/pa03/model-router/`、`.work/verification/model-router-solution/`、`.work/pa08/components-20261106-r16/`。

## 4 未完成

- 真实 `IAgentModelPolicyStore` 适配仍缺（复用现有配置权威需扩展 Agent manifest/配置 API 的 purpose 字段）。
- `IReviewEvaluationBinder` 的 live API 绑定（`IModelResourcePool`/`IExecutionResourceScope`/`ILeasedModel`）与唯一预留尚未接；Review Worker 仍不注册。
- 流式/子代理预算、Goal 自动续跑、08B/08C（NotRun）保持原状。
