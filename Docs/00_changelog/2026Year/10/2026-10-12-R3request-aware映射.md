---
title: 2026-10-12 §5.3（①②③）：request-aware 管理动作映射 —— tabs 的 new/activate/close 分别映射，新增 TabActivate 策略
author: hyfree
date: 2026-10-12
last_reviewed: 2026-10-12
description: "按方案 §5.3 修"同一能力多动作被合并成最严判据"的缺陷：① `Source/Pudding.Contracts/Desktop/BrowserOperationMapping.cs` 新增 **request-aware 重载** `TryGetBrowserOperation(DesktopCapabilityRequest, out operation)` —— 载荷是 `Tabs` 时按 `Tabs.Action` **分别**映射（`New` ⇒ `TabNew`、`Activate` ⇒ **新增的 `TabActivate`**、`Close` ⇒ `TabClose`；未来未登记动作 ⇒ **返回 false 明确不支持**）；② `Source/Pudding.Contracts/Desktop/BrowserControlContracts.cs` 的 `BrowserAutomationOperation` **新增 `TabActivate`**，并在两张策略表登记（**grant kind = Page**、**scope = Write**，方案 §5.3「目标页 Write，并按 UI 焦点副作用走控制许可」）；③ 顺带**纠正一个规格误读**：契约侧 `DesktopTabAction` **只有 New/Activate/Close（没有 list）** —— "列出标签页"在 gRPC 侧走 `browser.contexts`（`ContextsList`），因此我一度新增的 `TabsList` 枚举与策略条目**已撤掉**（不留无人使用的规格）。**验证**：`Pudding.Contracts` 编译通过；`Pudding.ContractsTests` **214/214** · `Pudding.DesktopServiceTests` **388/388** · `Pudding.DesktopConnectionTests` **108/108**（**零回归**）。"
status: archived
categories: [docs, changelog]
tags: [browser, authorization, mapping, review]
related_docs: [Docs/00_changelog/2026Year/10/2026-10-12-R3标签映射定位.md]
related_files: [Source/Pudding.Contracts/Desktop/BrowserOperationMapping.cs, Source/Pudding.Contracts/Desktop/BrowserControlContracts.cs]
slug: changelog-r3-request-aware-mapping
draft: false
---

# §5.3 ①②③：tabs 动作分别映射

| 动作 | 操作 | 最小准入 |
|---|---|---|
| `New` | `TabNew` | Management |
| `Activate` | **`TabActivate`（新增）** | **Page + Write** |
| `Close` | `TabClose` | Page + `Manage\|Read` |
| 未登记动作 | — | **返回 false（明确不支持）** |

**撤掉**误加的 `TabsList`：契约侧 `DesktopTabAction` 无 list；"列标签"走 `browser.contexts`。

**证据**：Contracts **214/214** · Service **388/388** · Connection **108/108**（零回归）

## 未完成（下一轮）

**④ 接线**：`RunOnUiAsync`/`ValidateAdmission` 增 `operationOverride`，由 `TabsAsync` 传入 request-aware 结果（目前准入仍走**能力级**映射 ⇒ 新映射**尚未影响运行时判据**）。
**⑤ 测试**：`tabs.activate` 不再被当作 TabClose、未知动作被拒。

## 诚实边界

- 本轮是**契约/策略层**修复；**运行时判据尚未切换**（④）；**未真机验证**。