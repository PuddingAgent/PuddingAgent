---
title: S5 裁定落地：Outbox 事件日志与插件分发 Worker
author: hyfree
date: 2026-11-06
last_reviewed: 2026-11-06
status: archived
description: "新增 OutboxCommittedEventLog 与 PluginEventDispatchWorker：按 workspace 订阅扫描、owner 租约投递、顺序 ACK 前缀、失败重试/死信；新增 4 条真实 SQLite 测试。"
categories: [docs, changelog]
tags: [eventing, dispatch, worker, s5, decisions]
related_docs: [Docs/12_features/Agent组件化S5架构裁定与接线门禁-2026-11-06.md, Docs/14_reports/2026-11-06-Agent组件化状态阻塞与待决策清单.md]
related_files: [Source/PuddingPlatform/Services/Eventing/OutboxCommittedEventLog.cs, Source/PuddingPlatform/Services/Eventing/PluginEventDispatchWorker.cs, Source/PuddingPlatformTests/Services/PluginEventDispatchWorkerTests.cs]
slug: changelog-s5-ruling-dispatch-worker-20261106
draft: false
---

# S5 裁定落地：Outbox 事件日志与插件分发 Worker

继续裁定第 1 阶段（事件与原子完成），补上“已提交事件 → 插件投递”的执行循环；未 commit、未部署、未调用真实模型。

## 1 已提交事件日志

- 新增 `OutboxCommittedEventLog`（`ICommittedEventLog`）：从 `event_outbox` 读取已提交事实，workspace 即分发流；`SnapshotRequired` 按 `AfterSequence + 1 < 最小已提交序号` 判定；只暴露已提交行。
- 与 UoW 配对：事实只有在业务事务提交后可见，未提交/回滚不可见。

## 2 插件分发 Worker

- 新增 `PluginEventDispatchWorker`：
  - 按 `IEventDispatchWorkspaceSource` 的受权 workspace 扫描 `ISubscriptionStore.ListAsync`；
  - 用本 Worker 唯一 owner token 调 `SubscriptionDispatcher.DispatchAsync`（Enqueue→Claim，活动 lease 不会被他人拿走）；
  - 按事件顺序调用 `IPluginEventDeliverySink`；成功只累计**连续前缀**，失败立即停止前缀并在该 delivery 上 `FailAsync`（重试，达到阈值死信）；最后只 ACK 已成功前缀；
  - 返回订阅数/投递/确认/失败/死信/需快照计数；ACK 被拒（lease 过期/并发 checkpoint）只记录并留待重试。
- 真实 `IPluginEventDeliverySink` 需要 PluginManager 接线，本 Worker 暂未注册为 HostedService（不半启动）。

## 3 测试

- `PluginEventDispatchWorkerTests` 4/4（真实 SQLite + 真 outbox/订阅/投递 store）：投递并确认、sink 失败重试后成功、连续失败死信且阻塞游标、其他 Worker 活动 lease 不被接管。
- 结果：Platform S5 切片 **99/99**；全解决方案构建 exit 0；一键脚本 RunId 20261106-r8 exit 0（组件 274 + Platform 99 + 前端 47 + tsc）。
- 证据：`.work/pa07/dispatch-worker/`、`.work/verification/dispatch-worker-solution/`、`.work/pa08/components-20261106-r8/`。

## 4 仍未完成

- HostedService 周期/退避与真实 PluginManager sink 注入（Host DI 尚未注册 Worker）。
- Review outbox→canonical 的运行时发布循环已有 dispatcher，但实际 HostedService 仍维持原状。
- PA-01 P2、actor/binder、Goal 自动续跑、08B/08C（NotRun）。
