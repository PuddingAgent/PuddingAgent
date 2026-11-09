---
title: PA-03 S5 评审 Worker 与完成事件 Outbox
author: hyfree
date: 2026-11-05
last_reviewed: 2026-11-05
status: archived
description: "PA-03 S5：评审批次 Worker（租约认领/协调器执行）与完成事件持久 outbox；终态不认领、失败不伪造完成、EventId 幂等。"
categories: [docs, changelog]
tags: [review, worker, outbox, pa03, s5]
related_docs: [Docs/12_features/Agent组件化任务书-03-持久验收任务-2026-11-05.md, Docs/12_features/Agent组件化任务书-07-可靠插件扩展-2026-11-05.md]
related_files: [Source/PuddingPlatform/Services/Reviews/ReviewJobWorker.cs, Source/PuddingPlatform/Services/Reviews/ReviewEventOutbox.cs, Source/PuddingPlatformTests/Services/ReviewJobWorkerTests.cs]
slug: changelog-pa03-s5-review-worker-outbox-20261105
draft: false
---

# PA-03 S5 评审 Worker 与完成事件 Outbox

- 新增 `ReviewJobWorker`：单批列出可认领 Job（Pending 或租约过期的 Running），经独立 `ReviewJobCoordinator` 执行 evidence→executor→validate→complete/fail→outbox；终态 Job 不再认领；每次尝试的 owner epoch 单调递增；异常只计失败数，不伪造完成。
- 新增 `ReviewEventOutbox`（`IReviewEventOutbox` + `review_events` 表）：完成事实按 EventId 幂等持久写入，未发布事实不丢弃；分发与 published 标记留给 07A S5。
- `ReviewJobStore` 增加 `ListClaimableAsync`；租约列改为 unix-ms INTEGER（SQLite 能翻译比较），API 语义不变。
- Host：DI 注册 `ReviewEventOutbox`/`IReviewEventOutbox`；Worker 依赖 `IReviewEvidenceReader`/`IReviewExecutor` 适配器，暂不注册（避免半成品 worker 启动）。
- 测试：`ReviewJobWorkerTests` 5/5（真实 SQLite：完成 + outbox、executor 失败→Unknown 不发完成事件、终态/取消不认领、过期租约回收、outbox EventId 幂等），与 ReviewJobStoreTests 合计 13/13 通过。
- 取红：可认领查询放行终态、outbox 去掉幂等 —— 均使对应测试变红（还原哈希已核对）。
- 冻结清单：Host DI 文件属配置 API P0/P1 清单，本次新增 outbox 注册（diff 已核对），主代理审阅后更新基线；三条冻结检查 3/3 PASS。
- 未完成：真实 `IReviewEvidenceReader`/`IReviewExecutor` 适配、Worker HostedService 注册、outbox 分发器（07A S5）、Goal 续跑接入。
- 未 commit；证据 `.work/pa-03/s5-worker/`。
