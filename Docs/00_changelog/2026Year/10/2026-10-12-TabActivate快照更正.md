---
title: 2026-10-12 修复：新增 TabActivate 未同步 §3.2 快照表（AutomationTests 自 round 651 起红，整链暴露）
author: hyfree
date: 2026-10-12
last_reviewed: 2026-10-12
description: "整链复核（round-661）发现 `PuddingBrowser.AutomationTests` **410/411**：`Every_Operation_Is_Covered_By_The_Table` 报 `expected <14> actual <15>`，信息明确 —— *a new operation must be classified in the §3.2 table, not silently defaulted*。**根因**：我在 round-651 给 `BrowserAutomationOperation` 新增 **`TabActivate`**（§5.3 request-aware 映射）时，只更新了 **Contracts** 的两张策略表与 **Automation** 的运行时策略，**没有同步测试侧那张"方案 §3.2 机器可验快照表"** ⇒ 该守卫**从那一刻起就是红的**。**修法**：在 `Source/PuddingBrowser.AutomationTests/BrowserOperationPolicyTests.cs` 的 `Table` 补 `TabActivate` 行；第一次按"不需控制租约"填**猜错**（`Policy_Table_Matches_The_Design` 报 `lease requirement for TabActivate: expected False actual True`）⇒ 实测确认它与其它**写操作**同类：**`(Page, Write, RequiresControlLease: true, BlockedWhileUserControlling: true)`**。**验证**：`PuddingBrowser.AutomationTests` **411/411 通过**。**流程教训（自记）**：我此前"只跑自认为受影响的套件"，导致这个红**存活了 10 轮**才被整链抓到 —— **契约/枚举/策略表的改动必须立刻跑整链（12 套件），而不是挑套件跑**。"
status: archived
categories: [docs, changelog]
tags: [browser, testing, process, correction]
related_docs: [Docs/00_changelog/2026Year/10/2026-10-12-R3request-aware映射.md]
related_files: [Source/PuddingBrowser.AutomationTests/BrowserOperationPolicyTests.cs]
slug: changelog-tabactivate-snapshot-correction
draft: false
---

# 修复：`TabActivate` 未同步 §3.2 快照表

| 项 | 内容 |
|---|---|
| 症状 | `AutomationTests` **410/411**；`Every_Operation_Is_Covered_By_The_Table`（14 vs 15） |
| 根因 | round-651 新增 `TabActivate` 时**只更新了生产侧两张表**，漏了**测试侧 §3.2 快照表** |
| 修法 | 补 `TabActivate` 行；**实测确认**为写操作同类：`(Page, Write, RequiresControlLease: true, BlockedWhileUserControlling: true)` |
| 验证 | **411/411** ✓ |

## 流程教训

**这个红存活了 10 轮** —— 因为我"只跑自认为受影响的套件"。⇒ **契约/枚举/策略表改动必须立刻跑整链**。

## 诚实边界

- 本轮**先提交后核对**的失误已在上一轮自记；本轮的红**由整链复核抓出**（说明整链复核有效）；**未真机验证**。