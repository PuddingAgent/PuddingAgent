---
title: 预算并发/取消/Unknown 语义与 Host 预算端口接线
author: hyfree
date: 2026-11-06
last_reviewed: 2026-11-06
status: archived
description: "补真实持久账本上的并发上限、根取消阻断新派发、Unknown 不退款三条测试；Host 注册 IModelAttemptBudgetPort 与 ModelLeaseRegistry（此前 Runtime 端口未注册，预算绑定调用会 fail-closed）。"
categories: [docs, changelog]
tags: [budget, concurrency, cancellation, di, pa01]
related_docs: [Docs/12_features/基础设施组件化收敛实施计划-2026-11-06.md, Docs/14_reports/2026-11-06-基础设施组件化评估与接入缺口.md]
related_files: [Source/PuddingPlatformTests/Services/BudgetBoundaryClosureTests.cs, Source/PuddingHost/Extensions/PuddingServiceCollectionExtensions.Platform.cs]
slug: changelog-budget-concurrency-wiring-20261106
draft: false
---

# 预算并发/取消/Unknown 语义与 Host 预算端口接线

补 P2 生命周期语义与缺失的 DI 接线；未 commit、未部署、未调用真实模型。

## 1 语义测试（真实持久账本）

`BudgetBoundaryClosureTests` 新增 2 条、合计 3 条：
- `MaxConcurrentAttempts=1` 时第二个活跃 attempt 被拒（`budget.concurrency`，先于成本拒绝）；
- 根预算取消后新派发被拒（`budget.cancelled`）；
- 已派发但未结算的 attempt `MarkUnknown` 后：`ActiveAttempts=0`、`ReservedCost=0`、**`UnknownCost>0`（不退款、不报零）**。

## 2 Host 接线（修复真实缺口）

- Host 此前**未注册** `IModelAttemptBudgetPort`：Runtime 携带预算身份的调用会因端口缺失 fail-closed；现注册 `PlatformModelAttemptBudgetPort(gate)`。
- 同时注册 `ModelLeaseRegistry`（PA-06C 动态租约代际），不再只有独立测试使用。
- 未改受保护的 Work/Plugin 契约文件；Host DI 属有意变更，已审阅重签配置指纹。

## 3 验证

- 预算闭环测试 **3/3**；Platform S5 切片 **168/168**；全解决方案构建 exit 0（restore + --no-restore）；一键脚本 RunId 20261106-r33 exit 0（组件 276 + Platform 168 + 前端 47 + tsc）。
- 冻结：配置 18/18、Work 16/16、Plugin 18/18 PASS。
- 证据：`.work/i1/budget-semantics/`、`.work/verification/budget-semantics-solution/`、`.work/pa08/components-20261106-r33/`。

## 4 未完成

- Runtime 主执行链仍未填 `LlmAttemptBudget.BudgetAttempt`（端口已注册但生产调用尚未携带根身份）；因此预算仍未真正到达主 Agent provider 调用。
- 子代理/工具内/后台 Job 的预算身份未接；`ModelLeaseRegistry` 只有 DI 注册，未见生产 acquire/release 消费者。
- 工具/授权/MCP/上下文/工作区/插件消费者迁移、Goal 自动评审接线、08B/08C（NotRun）保持原状。
