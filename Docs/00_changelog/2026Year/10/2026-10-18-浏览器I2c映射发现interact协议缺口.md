---
title: 2026-10-18 I2-c 映射发现：page.interact 缺 expectedPageVersion（H3 协议缺口，fail-closed）
author: hyfree
date: 2026-10-18
last_reviewed: 2026-10-18
status: archived
description: "执行器契约要求 interact 钉住页面版本，但 Bridge 线缆没有该字段；映射层显式拒绝并登记补协议字段方案，不猜版本/不回退。"
categories: [docs, changelog]
tags: [browser, i2c, adapter, protocol-gap]
related_docs: [Docs/00_changelog/2026Year/10/2026-10-18-浏览器I2c适配器第七片截图.md, Docs/14_reports/Agent浏览器控制架构与实现审阅-2026-10-18.md]
related_files: [Source/PuddingDesktop.WpfArchive/Browser/BrowserBridgeCommandAdapter.cs, Tests/PuddingDesktop.Tests/Browser/BrowserBridgeCommandAdapterTests.cs]
slug: changelog-2026-10-18-browser-interact-protocol-gap
draft: false
---

# 2026-10-18 I2-c 映射发现：page.interact 缺 expectedPageVersion（H3 协议缺口，fail-closed）

## 发现

`BrowserInteractRequest`（能力契约）**强制**要求 `expectedPageVersion > 0`（写操作必须钉住版本）。但 Bridge 线缆的 `BrowserBridgeCommand` envelope 与 `PageInteractArguments` **都没有 `expectedPageVersion`** 字段（审阅报告 H3 记录过的缺口）。

因此 `page.interact` 在当前协议下**无法**合法映射到执行器：

- 猜"当前版本"= 伪造证据，会让执行器版本门禁形同虚设；
- 回退旧 controller 路径 = 绕过 I2-c 的单一管线，且旧路径本身也不带版本。

## 本轮处置（fail-closed + 登记）

- 适配器对 `page.interact` 显式拒绝，原因写明"H3 protocol gap"（`browser_interact` 在 I2-c 切换前必须补协议字段）；
- 测试新增：断言拒绝原因含 `expectedPageVersion` 与 `H3`；其余"未映射"用例改用 `page.activate`（保持通用 fail-closed 覆盖）；
- 适配器测试 **12/12 通过**。

## 后续方案（下一轮实施口径）

1. 协议加可选字段：`PageInteractArguments.ExpectedPageVersion`（或 envelope 统一字段），Core 侧从工具参数/页面观察填充；
2. Desktop 侧映射：字段缺失或 ≤0 → 拒绝（与截图同一规则），有值则 `DesktopPageVersion.Require`；
3. 两侧契约测试 + bridge JSON 快照（默认 0 表示缺失）；
4. 完成后才能切换 interact 到执行器（I2-c 剩余）。

## 影响

- I2-c 的"八工具全走执行器"目前受 **interact 协议缺口**与 **tabs 映射**两项阻塞，不属于实现偷懒或外部验收缺口。
- 回滚 = revert 本提交（适配器拒绝分支 + 测试）。

## 关联

任务：goal round 24；承接 `b8e2f47`（第七片）；阻塞项来自审阅报告 H3。
