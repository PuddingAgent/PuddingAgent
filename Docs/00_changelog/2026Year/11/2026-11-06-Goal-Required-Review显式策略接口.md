---
title: Goal Required Review 显式策略接口（默认不强制）
author: hyfree
date: 2026-11-06
last_reviewed: 2026-11-06
status: archived
description: "新增 IGoalReviewRequirementPolicy/NoGoalReviewRequirementPolicy：默认不强制评审；显式开启后无评审 Job 时完成门禁返回 Missing（review_required）并阻止完成；3 条测试。是否开启留待产品决策。"
categories: [docs, changelog]
tags: [goal, review, policy, i5]
related_docs: [Docs/12_features/基础设施组件化收敛实施计划-2026-11-06.md, Docs/12_features/Agent组件化S5架构裁定与接线门禁-2026-11-06.md]
related_files: [Source/PuddingPlatform/Services/Goals/GoalReviewCompletionGate.cs, Source/PuddingPlatformTests/Services/GoalReviewCompletionGateTests.cs]
slug: changelog-goal-required-review-policy-20261106
draft: false
---

# Goal Required Review 显式策略接口（默认不强制）

为 I5“完成候选/Required Review 关系”提供显式策略缝，但**不擅自改变产品默认**；未 commit、未部署、未调用真实模型。

## 1 改动

- `IGoalReviewRequirementPolicy` + `NoGoalReviewRequirementPolicy`（默认 `false`）：
  - 未配置时保持原行为：无评审 Job → `NotRequired`，Goal 可按既有证据完成；
  - 显式配置为 required 时：无同版本评审 Job → 门禁返回 `Missing`，verifier 以 `review_required` 阻止完成。
- `GoalReviewCompletionGate` 可选注入策略；Host 注册默认 `NoGoalReviewRequirementPolicy`（未开启）。
- `ConservativeGoalIterationVerifier` 增加 `ReviewFact.Missing → "review_required"` 分支。

## 2 测试与验证

- `GoalReviewCompletionGateTests` 3/3：默认无 Job=NotRequired；Required 无 Job=Missing；Required + 同版本 Satisfied=完成。
- Review/verifier 相关 **32/32**；Platform S5 切片 **174/174**；全解决方案构建 exit 0（restore + --no-restore）；一键脚本 RunId 20261106-r37 exit 0（组件 276 + Platform 174 + 前端 47 + tsc）。
- 冻结：配置 18/18、Work 16/16、Plugin 18/18 PASS。
- 证据：`.work/i5/review-policy/`、`.work/verification/review-policy-solution/`、`.work/pa08/components-20261106-r37/`。

## 3 待决策（未擅自开启）

- **产品何时要求 Goal 评审**（所有 Goal / 高风险 Goal / 用户显式选择）尚无产品字段与策略来源；因此默认仍不强制，开启需要你的裁定。
- 即便开启，**提交 seam 仍未接结算事务/outbox**：完成候选处不会自动提交 Required Review，缺少自动提交会阻断完成（这是显式策略下的预期 fail-closed，但尚不能自动补评审）。
- 主执行链预算身份、子代理/工具/插件消费者迁移、08B/08C（NotRun）保持原状。
