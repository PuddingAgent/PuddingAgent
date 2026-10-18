---
title: 2026-10-18 H3 协议缺口修复：page.interact 补齐 expectedPageVersion 并接入映射
author: hyfree
date: 2026-10-18
last_reviewed: 2026-10-18
status: archived
description: "新增 PageInteractArguments.ExpectedPageVersion，Core 从页面版本填充、Desktop 映射要求 >0 否则拒绝；interact 可由执行器执行，不再 fail-closed。"
categories: [docs, changelog]
tags: [browser, i2c, protocol, interact]
related_docs: [Docs/00_changelog/2026Year/10/2026-10-18-浏览器I2c映射发现interact协议缺口.md]
related_files: [Source/PuddingBrowser.Protocol/BrowserBridgeCommandPayloads.cs, Source/PuddingHost/BrowserBridge/RemoteBrowserPage.cs, Source/PuddingDesktop.WpfArchive/Browser/BrowserBridgeCommandAdapter.cs]
slug: changelog-2026-10-18-browser-interact-expected-version
draft: false
---

# 2026-10-18 H3 协议缺口修复：page.interact 补齐 expectedPageVersion 并接入映射

## 背景

上一轮登记：执行器契约 `BrowserInteractRequest` 强制写操作钉住 `expectedPageVersion > 0`，但 Bridge 线缆没有该字段（审阅 H3）⇒ `page.interact` 无法映射。本轮补齐协议并完成映射。

## 改动

1. **协议（Wire）**：`PageInteractArguments.ExpectedPageVersion`（`long?`，JSON 可选；缺省 null）。旧 Core 不填 → null，Desktop 拒绝而不是猜版本。
2. **Core 侧填充**：`RemoteBrowserPage` 的两处 interact 发送（通用 `InteractAsync`、`ScrollAsync`）都带 `ExpectedPageVersion = PageVersion > 0 ? PageVersion : null`（页面版本未知则留空）。
3. **Desktop 映射**：`page.interact` → `browser.interact`：
   - **版本 > 0 才能映射**，否则 fail-closed（原因含 H3）；
   - `action` 用 `DesktopInteractionActionWire.TryParse` 严格解析，`scrollTarget` 用 `DesktopScrollTargetWire.TryParse` 严格解析；
   - locator 与 locate 共用同一映射助手（`TryMapLocator`，避免两份漂移）；
   - 逐字段搬运 text/values/checked/deltaX/deltaY/x/y/toX/toY/observationId；线缆无 `inputMode` ⇒ 如实 `Dom`（旧 Bridge 路径本来就是 DOM 输入）；
   - 失败时不返回半成品 descriptor/request。
4. **测试**：adapter 用例覆盖成功映射（scroll+locator+delta+scrollTarget+Dom）与缺版本拒绝；未能验证 Host 侧线上字段（本轮只保证 Core 构造处填充，快照/抓包证据属后续桥集成测试）。

## 验证

- `Tests/PuddingDesktop.Tests` ⇒ **362/362 通过**（含 adapter 12 条）
- `Tests/PuddingHost.Tests --filter BrowserBridge` ⇒ **136/136**
- `Pudding.ContractsTests` ⇒ **219/219**；`Pudding.DesktopConnectionTests` ⇒ **108/108**
- 未运行产品/真实模型；适配器仍未接管执行。

## 未完成与影响

- Host 侧"线上确实带版本"尚无抓取式断言（RemoteBrowserPage 单测/桥集成可补）；
- `toLocator`/`files` 等字段线缆本就没有，映射保持 null（原有缺口不变）；
- 剩 tabs 映射；派发器接线、controller 收缩、删除旧路径未做；
- 协议新增字段对旧 Core 向后兼容（null 会被拒绝，属安全方向）；
- 回滚 = revert 本提交。

## 关联

任务：goal round 25；承接 `d94b6b4`（缺口登记）。
