---
title: 2026-10-18 H3 tabs 变体修复：activate/close 补齐 expectedPageVersion 并接入映射
author: hyfree
date: 2026-10-18
last_reviewed: 2026-10-18
status: archived
description: "PageActivateArguments/PageCloseArguments 增加可选版本字段；Core 侧填充（close 先读清单取版本），Desktop 映射要求 >0 否则拒绝。"
categories: [docs, changelog]
tags: [browser, i2c, protocol, tabs]
related_docs: [Docs/00_changelog/2026Year/10/2026-10-18-浏览器I2c适配器第八片新建标签页.md]
related_files: [Source/PuddingBrowser.Protocol/BrowserBridgeCommandPayloads.cs, Source/PuddingHost/BrowserBridge/RemoteBrowserPage.cs, Source/PuddingHost/BrowserBridge/RemoteBrowserContext.cs, Source/PuddingDesktop.WpfArchive/Browser/BrowserBridgeCommandAdapter.cs]
slug: changelog-2026-10-18-browser-tabs-expected-version
draft: false
---

# 2026-10-18 H3 tabs 变体修复：activate/close 补齐 expectedPageVersion 并接入映射

## 改动

1. **协议**：`PageActivateArguments` / `PageCloseArguments` 增加 `ExpectedPageVersion`（`long?`，可选；缺失 = null）。
2. **Core 填充**：
   - `RemoteBrowserPage.BringToFrontAsync` 用 `PageVersion > 0 ? PageVersion : null`；
   - `RemoteBrowserContext.ClosePageAsync` 上下文不缓存版本 ⇒ 先 `ListPagesAsync` 读目标页当前版本再发送（读清单失败则留空，由 Desktop 明确拒绝，不阻断错误可解释性）。
3. **Desktop 映射**：`page.activate`/`page.close` → `browser.tabs`（Activate/Close）；版本缺失或 ≤0 fail-closed（原因含 `expectedPageVersion`/`H3`），有值则 `DesktopPageVersion.Require`。
4. **测试**：adapter 用例更新为 activate 成功映射（含版本）+ close 缺版本拒绝。

## 验证

- Desktop **363/363**、Host BrowserBridge **136/136**、DesktopConnection **108/108** 全绿；
- 未运行产品/真实模型；未做 Host 侧"线上带版本"的抓包式断言（单测可补）。

## 未完成与影响

- `close` 多一次 `page.list` 往返（正确性优先）；如需省往返可让上下文缓存页面版本，属优化项；
- 剩余映射：`page.list`、`context.getInfo/close/create` 管理面；
- 派发器接线、controller 收缩、删除旧路径未做；
- 回滚 = revert 本提交。

## 关联

任务：goal round 27；承接 `5a0f9c0`（第八片）与 `c71a5e4`（interact 同法修复）。
