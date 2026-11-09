---
title: S5 裁定落地：投递租约 CAS 与事务 Outbox UoW
author: hyfree
date: 2026-11-06
last_reviewed: 2026-11-06
status: archived
description: "投递改为持久 owner/lease CAS 与 owner-fenced 失败/确认；新增 Platform IOutboxUnitOfWork 让业务状态与 outbox 事实同事务提交；新增 7 条测试。"
categories: [docs, changelog]
tags: [eventing, outbox, uow, lease, s5, decisions]
related_docs: [Docs/12_features/Agent组件化S5架构裁定与接线门禁-2026-11-06.md, Docs/14_reports/2026-11-06-Agent组件化状态阻塞与待决策清单.md]
related_files: [Source/PuddingEventing/SubscriptionDispatcher.cs, Source/PuddingEventing.Contracts/EventSubscriptionPorts.cs, Source/PuddingPlatform/Services/Eventing/EventDeliveryStore.cs, Source/PuddingPlatform/Services/Eventing/EventOutboxUnitOfWork.cs]
slug: changelog-s5-ruling-lease-uow-20261106
draft: false
---

# S5 裁定落地：投递租约 CAS 与事务 Outbox UoW

继续按《Agent 组件化 S5 架构裁定与接线门禁》第 1 阶段，补齐投递竞争与事务原子性；未 commit、未部署、未调用真实模型。

## 1 持久投递租约（owner/lease CAS）

- 契约修订：`IEventDeliveryStore` 拆出 `EnqueueAsync`（只去重入队）与 `ClaimAsync(subscriptionId, maxCount, ownerId, now, leaseUntil)`；`EventDelivery` 增加 `LeaseOwner/LeaseUntil`；`FailAsync`、`IEventCheckpointStore.AckAndAdvanceAsync` 增加 `ownerId` 围栏；新增错误码 `event.lease_stale`。
- Platform：`event_deliveries` 增加 `lease_owner_id`/`lease_until_unix_ms`；`ClaimAsync` 在同一事务内只领取“无 lease / lease 过期 / 同一 owner”的行并写入 owner+到期时间；另一 Worker 的活动 lease 不会再被返回。ACK/失败校验 owner，旧 owner 被拒绝。
- 组件：`InMemoryEventingHost` 同步语义；`SubscriptionDispatcher.DispatchAsync(ownerId, …)` 负责 Enqueue→Claim，`DeliveryCoordinator` 用 plugin+generation 派生 owner（不同 generation 不能接管旧 lease）。死信重放清空 lease，需重新 claim 后再 ACK。
- 测试：两 Worker 竞争（活动 lease 不可被他人领取、过期可回收）、失败/ACK 旧 owner 被拒。

## 2 事务 Outbox UoW

- 新增 `Source/PuddingPlatform/Services/Eventing/EventOutboxUnitOfWork.cs`：
  - `IOutboxUnitOfWork.ExecuteAsync<T>(Func<PlatformDbContext, IOutboxAppendView, CancellationToken, Task<T>>)`；
  - 回调取得**已打开事务**的业务 DbContext 和只允许 `AppendAsync` 的 outbox 视图；参与者不能 Begin/Commit/Rollback；
  - UoW 在 commit 时给待发布事实分配 stream sequence 并写 `event_outbox`，与业务写共享同一 SQLite 事务；异常/重复 EventId 整批回滚。
- schema：新增 `event_outbox` 表与 stream+sequence 唯一索引（幂等建表）。
- 测试：业务写+事件同提交；异常两者皆无；重复 EventId 整批回滚（业务状态不落盘）。

## 3 测试与证据

- 新增 7 条：Platform `EventDeliveryStoreTests` +1（活动/过期 lease 竞争）、`EventOutboxUnitOfWorkTests` 3（同提交/回滚/重复），另调整事件/平台既有 lease 断言。
- 结果：`PuddingEventingTests` 25/25、`PuddingReviewsTests` 23/23、Platform S5 切片 **95/95**；全解决方案构建 exit 0；一键脚本 RunId 20261106-r7 exit 0（组件 274 + Platform 95 + 前端 47 + tsc）。
- 证据：`.work/pa07/lease-cas/`、`.work/verification/lease-uow-solution/`、`.work/pa08/components-20261106-r7/`。

## 4 仍未完成（按裁定顺序）

- 分发 Worker/订阅扫描循环与 outbox→canonical 投递接线（组件端口已齐，宿主 Worker 未注册）。
- PA-01 P2 执行链（按 attempt 预留/结算、Enforced 根不可降级）。
- actor/binder 与 Waiting/证据正文/版本/恢复链路。
- Goal 自动续跑；08B/08C 保持 NotRun。
