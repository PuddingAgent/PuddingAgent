---
title: Review 完成判定改为精确 Objective 版本查询
author: hyfree
date: 2026-11-06
last_reviewed: 2026-11-06
status: archived
description: "ReviewJobStore 增加 GetLatestByGoalObjectiveAsync（按 Goal+ObjectiveVersion 取最新 accepted），GoalReviewCompletionGate 改用它；避免更新版本评审遮蔽当前候选匹配评审；+1 测试。"
categories: [docs, changelog]
tags: [review, goal, version, i5]
related_docs: [Docs/12_features/基础设施组件化收敛实施计划-2026-11-06.md]
related_files: [Source/PuddingPlatform/Services/Reviews/ReviewJobStore.cs, Source/PuddingPlatform/Services/Reviews/ReviewJobByGoalObjectiveReader.cs, Source/PuddingPlatform/Services/Goals/GoalReviewCompletionGate.cs]
slug: changelog-review-exact-objective-20261106
draft: false
---

# Review 完成判定改为精确 Objective 版本查询

按 I5“完成判定查询精确版本 key，Latest 仅适合展示”收敛；未 commit、未部署、未调用真实模型。

## 1 改动

- `ReviewJobStore.GetLatestByGoalObjectiveAsync(goalRunId, objectiveVersion)`：按 Goal + ObjectiveVersion 取 AcceptedSequence 最新的一条；`GetLatestByGoalAsync` 仍保留给展示。
- `GoalReviewCompletionGate` 改用精确 Objective 版本查询：不再先取“最新评审”再拒绝版本不匹配；同一 Goal 下更新版本的评审不会遮蔽当前候选的匹配评审，其他版本也不会被用来完成当前版本。
- 新接口 `IReviewJobByGoalObjectiveReader`（Reviews 命名空间），Store 实现；门禁依赖接口便于替换。

## 2 测试与验证

- `ReviewJobStoreTests.ExactObjectiveLookup_IgnoresNewerOtherVersion`：v2 更新时 Latest 返回 v2，但精确 v1 查询返回 v1，不存在版本返回 null。
- Review/verifier 相关 **43/43**；Platform S5 切片 **171/171**；全解决方案构建 exit 0（restore + --no-restore）；一键脚本 RunId 20261106-r36 exit 0（组件 276 + Platform 171 + 前端 47 + tsc）。
- 证据：`.work/i5/exact-review/`、`.work/verification/exact-review-solution/`、`.work/pa08/components-20261106-r36/`。

## 3 未完成

- Goal 完成候选与 Required Review 的**事务/outbox 关系**仍未建立（提交 seam 未接结算事务；无 Required 策略时仍可无评审完成）。
- Review 完成后的 continuation 触发未接；旧评审不得推动新一轮的完整闭环未验证。
- 主执行链预算身份、子代理/工具/插件消费者迁移、08B/08C（NotRun）保持原状。
