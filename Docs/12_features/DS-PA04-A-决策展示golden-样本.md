---
title: DS-PA04-A 决策展示 golden fixture
author: hyfree
date: 2026-11-05
last_reviewed: 2026-11-05
status: proposed
description: "主代理为 DS-PA04-A 冻结的 GoalDecisionPresenter.Format 精确输出格式与 golden 样本；施工者不得修改本文件。"
categories: [docs, features]
tags: [goals, presenter, golden-fixture]
related_docs: [Docs/12_features/Agent组件化任务书-04-Goal续跑控制-2026-11-05.md]
related_files: [Source/Pudding.Api.Contracts/GoalDecision.cs]
slug: features-dspa04a-presenter-golden-20261105
draft: false
---

# DS-PA04-A 决策展示 golden fixture

本文件由主代理冻结，定义 `GoalDecisionPresenter.Format(GoalDecisionSnapshot)` 的精确输出。施工者**不得修改本文件**，也不得为了迁就输出修改期望值。

## 1 输出格式（严格按行，UTF-8，`Environment.NewLine` 分隔，无首尾空行）

```text
Phase=<Phase>
ReasonCode=<ReasonCode，空/空白时为"未提供">
UnmetCriteria=<无 | 逐项以 "; " 连接的 "<CriterionId>:<Outcome>">（顺序与输入 UnmetCriteria 相同）
NextAction=<NextAction 原值，缺失/空白时为"未提供下一步">
EvidenceRefs=<无 | 按输入顺序以 ", " 连接的引用>
```

硬性约束：

- 不出现"已验收"、"完成"等新增结论；不解析任何自然语言；不推导 Phase/下一状态。
- 未知 `ReasonCode` 原样保留（含大小写、点号、中文），不得映射成通用值。
- `NextAction` 缺失或空白时逐字输出 `未提供下一步`，不生成建议文本。
- 空 `UnmetCriteria` 输出 `无`；空 `EvidenceRefs` 输出 `无`。
- 输入对象不变（Format 不修改 snapshot 或其中集合）。

## 2 golden 样本

### 2.1 completed-with-evidence

输入：

```json
{
  "decisionId": "decision-1",
  "phase": "Completed",
  "reasonCode": "goal.completed",
  "unmetCriteria": [],
  "nextAction": null,
  "evidenceRefs": ["check://build/exit-0", "review://review-7"],
  "objectiveVersion": "objective-v1",
  "acceptanceContractVersion": "acceptance-v1",
  "rootBudgetId": "budget-root-1",
  "budgetRevision": 12,
  "activationEpoch": 3,
  "iterationNo": 2,
  "decidedAt": "2026-11-05T12:00:00+00:00"
}
```

期望输出（逐字）：

```text
Phase=Completed
ReasonCode=goal.completed
UnmetCriteria=无
NextAction=未提供下一步
EvidenceRefs=check://build/exit-0, review://review-7
```

### 2.2 blocked-with-unmet-criteria

输入：

```json
{
  "decisionId": "decision-2",
  "phase": "Blocked",
  "reasonCode": "goal.review_needs_user",
  "unmetCriteria": [
    { "criterionId": "criterion-1", "outcome": "NotSupported", "evidenceRefs": ["review://review-9"] },
    { "criterionId": "criterion-2", "outcome": "Unknown", "evidenceRefs": [] }
  ],
  "nextAction": "await-user-decision",
  "evidenceRefs": ["review://review-9"],
  "objectiveVersion": "objective-v1",
  "acceptanceContractVersion": "acceptance-v1",
  "rootBudgetId": "budget-root-1",
  "budgetRevision": 12,
  "activationEpoch": 3,
  "iterationNo": 2,
  "decidedAt": "2026-11-05T12:00:00+00:00"
}
```

期望输出（逐字）：

```text
Phase=Blocked
ReasonCode=goal.review_needs_user
UnmetCriteria=criterion-1:NotSupported; criterion-2:Unknown
NextAction=await-user-decision
EvidenceRefs=review://review-9
```

### 2.3 budget-exhausted-empty-reason

输入：

```json
{
  "decisionId": "decision-3",
  "phase": "BudgetExhausted",
  "reasonCode": "goal.budget_exhausted",
  "unmetCriteria": [],
  "nextAction": "   ",
  "evidenceRefs": [],
  "objectiveVersion": "objective-v1",
  "acceptanceContractVersion": "acceptance-v1",
  "rootBudgetId": "budget-root-1",
  "budgetRevision": 12,
  "activationEpoch": 3,
  "iterationNo": 2,
  "decidedAt": "2026-11-05T12:00:00+00:00"
}
```

期望输出（逐字）：

```text
Phase=BudgetExhausted
ReasonCode=goal.budget_exhausted
UnmetCriteria=无
NextAction=未提供下一步
EvidenceRefs=无
```

### 2.4 unknown-reason-preserved

`ReasonCode` 为 `goal.custom_unknown.reason` 时，`ReasonCode=` 行必须原样输出该值；`NextAction` 缺失输出 `未提供下一步`；不因未知值改 Phase 或补建议。
