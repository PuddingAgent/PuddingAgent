---
title: PA-01 P2 预算账户 canonical 映射
author: hyfree
date: 2026-11-06
last_reviewed: 2026-11-06
status: archived
description: "新增 ExecutionBudgetAccountMapper：Goal/Work/Task 确定性根账户、Work 挂 Goal 根、重复解析幂等且不扩额/不降级；新增 3 条测试。"
categories: [docs, changelog]
tags: [budget, p2, accounts, pa01]
related_docs: [Docs/12_features/Agent组件化S5架构裁定与接线门禁-2026-11-06.md, Docs/14_reports/2026-11-06-Agent组件化状态阻塞与待决策清单.md]
related_files: [Source/PuddingPlatform/Services/ExecutionBudgets/ExecutionBudgetAccountMapper.cs, Source/PuddingPlatformTests/Services/ExecutionBudgetAccountMapperTests.cs]
slug: changelog-pa01-p2-budget-account-mapper-20261106
draft: false
---

# PA-01 P2 预算账户 canonical 映射

补齐裁定要求的“根账户由 canonical 受理/Goal 生命周期创建并保存映射”：新增确定性账户映射与测试；未 commit、未部署、未调用真实模型。

## 1 映射规则

- 新增 `ExecutionBudgetAccountMapper`：
  - GoalRun → `goal:<GoalRunId>`（根）；Work → `work:<WorkId>`，父级为 `goal:<ParentWorkId>`（由 descriptor 的 `ParentWorkId` 指定）；Task → `task:<TaskId>`；
  - `ResolveAsync` 返回 `BudgetAccountScope(account, ancestors, identity, mode, costUnit)`，供 `ModelAttemptBudgetGate` 的 `ReserveBudget.AccountPath` 直接使用；
  - 重复解析幂等：已存在账户直接复用，**不会**用后续 descriptor 扩大限额或降级 Enforced 模式；
  - 非法 descriptor → `budget.account_invalid`。
- 契约面：`BudgetAccountDescriptor` / `BudgetAccountScope` 均为 Platform 内部类型，不扩大公共 API。

## 2 测试与验证

- `ExecutionBudgetAccountMapperTests` 3/3：Goal/Work 确定性账号与父子路径；重复解析幂等且限额/模式冻结；非法 descriptor 拒绝。
- Platform S5 切片 **110/110**；全解决方案构建 exit 0；一键脚本 RunId 20261106-r11 exit 0（组件 274 + Platform 110 + 前端 47 + tsc）。
- 证据：`.work/pa01/account-mapper/`、`.work/verification/account-mapper-solution/`、`.work/pa08/components-20261106-r11/`。

## 3 环境处置

- 本轮全解决方案构建导致 `.work/**/artifacts` 累积到磁盘耗尽；已清理 404 个生成 artifacts 目录（释放约 255 GB），未触碰 `temp/build` 运行目录与源码 `bin/obj`。

## 4 未完成

- 映射尚未在 `ExecutionRunCoordinator` 调用：执行链仍未填 `BudgetAttempt`；缺“模型 revision/price version 冻结来源”（06B binder）与根账户创建时机（canonical 受理/Goal 生命周期）的接线。
- 流式/子代理/工具内/评审预算接入、actor/binder、Goal 自动续跑、08B/08C（NotRun）保持原状。
