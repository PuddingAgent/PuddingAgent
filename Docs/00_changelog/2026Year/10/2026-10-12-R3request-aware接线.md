---
title: 2026-10-12 §5.3（④）：request-aware 操作送进准入 —— tabs 的 activate 不再被当作 TabClose
author: hyfree
date: 2026-10-12
last_reviewed: 2026-10-12
description: "把 §5.3 的 request-aware 映射**接进运行时准入判据**（上一轮只改了契约/策略，运行时仍走能力级映射）：① `DesktopService.ValidateAdmission` 新增 `operationOverride` 参数 —— 有覆盖值时**优先**用它决定 `BrowserAutomationRequest.Operation`，否则回退能力级映射；② `RunOnUiAsync` 末尾新增 `operationOverride` 形参（**放在最后**以保持既有调用点不变）并透传；③ `browser.tabs` 入口先做 request-aware 解析，**未登记动作直接** `InvalidRequest`（既不得统一按"最严的 TabClose"蒙过去，也不得放行），再把 `tabsOperation` 作为覆盖值传入。**效果**：`tabs.activate` 现按 **`TabActivate`（Page + Write）** 准入，`tabs.new` 按 **Management**，`tabs.close` 仍按 **Page + Manage\\|Read** —— 旧行为把三者一律当 `TabClose`。**验证**：`Pudding.DesktopService` 编译通过；`Pudding.DesktopServiceTests` **388/388** · `Pudding.ContractsTests` **214/214**（**零回归**）。"
status: archived
categories: [docs, changelog]
tags: [browser, authorization, mapping, review]
related_docs: [Docs/00_changelog/2026Year/10/2026-10-12-R3request-aware映射.md]
related_files: [Source/Pudding.DesktopService/DesktopService.cs]
slug: changelog-r3-request-aware-wired
draft: false
---

# §5.3 ④：request-aware 送进准入

| 动作 | 运行时最小准入（现） | 旧行为 |
|---|---|---|
| `new` | **Management** | TabClose |
| `activate` | **Page + Write** | TabClose（要 Manage\|Read） |
| `close` | Page + `Manage\|Read` | 同 |
| 未登记 | **`InvalidRequest`** | 蒙成 TabClose |

**证据**：Service **388/388** · Contracts **214/214**（零回归）

## 未完成

**⑤ 测试**：新增用例钉住"tabs.activate 按 Write 而非 Manage 准入"与"未登记动作被拒"（本轮只由既有套件证明无回归，**尚无新用例直接钉住新行为**）。

## 诚实边界

- **未真机验证**。