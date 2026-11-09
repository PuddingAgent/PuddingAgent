---
title: PA-07A S5 订阅 grant 与 checkpoint 持久接入
author: hyfree
date: 2026-11-05
last_reviewed: 2026-11-05
status: archived
description: "新增 SQLite 订阅/checkpoint 存储：Ack 删除投递与推进游标同事务、revision/单调性/归属校验；Host DI 与启动 bootstrap。"
categories: [docs, changelog]
tags: [events, subscriptions, checkpoint, pa07, s5]
related_docs: [Docs/12_features/Agent组件化任务书-07-可靠插件扩展-2026-11-05.md, Docs/12_features/Agent组件化任务书-总则与依赖-2026-11-05.md]
related_files: [Source/PuddingPlatform/Services/Eventing/SubscriptionStore.cs, Source/PuddingPlatform/Services/Eventing/EventSubscriptionSchemaBootstrapper.cs, Source/PuddingPlatformTests/Services/SubscriptionStoreTests.cs]
slug: changelog-pa07-subscription-checkpoint-store-20261105
draft: false
---

# PA-07A S5 订阅 grant 与 checkpoint 持久接入

- 新增 `Source/PuddingPlatform/Services/Eventing/`：
  - `SubscriptionStore`（`ISubscriptionStore` + `IEventCheckpointStore`）：宿主绑定的 grant 持久创建（cursor 0/revision 0）、取消、workspace 列表；**Ack 与游标推进同事务**，按 revision 围栏、游标单调、delivery 必须属于该订阅；外来/未知 delivery id 使整批失败（不丢事件、不静默跳过）。
  - `EventSubscriptionSchemaBootstrapper`：`event_subscriptions`/`event_deliveries` 幂等建表与索引；`Data/Entities/EventSubscriptionEntities.cs` + DbSets。
- Host：DI 注册 `SubscriptionStore`/`ISubscriptionStore`/`IEventCheckpointStore`；启动 schema 阶段 bootstrap；Host 构建 exit 0。
- 测试：`SubscriptionStoreTests` 4/4（真实 SQLite：创建/查询/列表、Ack 推进与删除、revision/CursorGap/外来 delivery、取消后不可推进）。
- 未完成：事件投递 lease/死信 Store（`IEventDeliveryStore`）、outbox 事务写入、订阅分发 Worker、真实 PluginManager 生命周期。
- 冻结清单：Host DI 文件属 P0/P1 清单，主代理审阅后更新基线；三条冻结检查 3/3 PASS。
- 未 commit；证据 `.work/pa-07/s5-subscriptions/`。
