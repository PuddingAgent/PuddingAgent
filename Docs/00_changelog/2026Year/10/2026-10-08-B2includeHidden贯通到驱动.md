---
title: 2026-10-08 B2 定位：includeHidden 打通到驱动，隐藏元素默认不参与匹配
author: hyfree
date: 2026-10-08
last_reviewed: 2026-10-08
description: "把 includeHidden 从工具参数一路贯通到驱动脚本（Contracts → Abstractions → 两条传输 → 定位/交互脚本）：默认排除隐藏元素，屏外但有尺寸的仍可定位。顺带查清一个框架级陷阱：工具参数的 snake_case 归一化只作用于顶层，嵌套字段必须用 camelCase。"
status: archived
categories: [docs, changelog]
tags: [browser, locate, include-hidden, webview2, b2]
related_docs: [Docs/12_features/浏览器自动化可靠性与渐进阅读设计方案-2026-10-08.md, Docs/00_changelog/2026Year/10/2026-10-08-B2定位输出规则与拒绝建议.md]
related_files: [Source/Pudding.Contracts/Desktop/LocateContracts.cs, Source/PuddingBrowser.Abstractions/Locator.cs, Source/PuddingBrowser.WebView2/WebView2DomClient.cs, Source/Pudding.DesktopSurface.Browser/BrowserRuntimeDesktopSurface.cs, Source/PuddingHost/BrowserBridge/BridgeBrowserCapabilitySurface.cs]
slug: changelog-b2-include-hidden-end-to-end
draft: false
---

# 2026-10-08 B2 定位：includeHidden 打通到驱动，隐藏元素默认不参与匹配

## 目标 / 背景

设计 §6.2：「locate 默认 `includeHidden=false`、`limit=10`；…`visible` 与 `inViewport` 分开：
屏外但有尺寸的元素仍可定位」。上一轮把 `limit=10` 做完并列出了 `includeHidden` 需要整条链路
（Contracts → Abstractions → 两条传输 → 驱动脚本），本轮把它打通。

## 改动（自下而上贯通）

| 层 | 文件 | 改动 |
|---|---|---|
| 契约 | `Source/Pudding.Contracts/Desktop/LocateContracts.cs` | `DesktopLocator` 新增 `IncludeHidden`（默认 false）；文档写明「只排除隐藏元素，屏外但有尺寸的仍可定位」 |
| 运行时抽象 | `Source/PuddingBrowser.Abstractions/Locator.cs` | `Locator` 新增 `IncludeHidden` |
| 驱动 | `Source/PuddingBrowser.WebView2/WebView2DomClient.cs` | **两份脚本各有一份 `locate`**，都加上 `if (!locator.includeHidden) filter(visible)`；放在 `hasText`/`nth` **之前**，于是 `nth` 索引与歧义判定都基于可见集合（否则会出现"定位不歧义、交互却歧义"） |
| 传输①（能力通道） | `Source/Pudding.DesktopSurface.Browser/BrowserRuntimeDesktopSurface.cs` | `ToRuntimeLocator` 映射 `IncludeHidden` |
| 传输②（桥接，活路径） | `Source/PuddingHost/BrowserBridge/BridgeBrowserCapabilitySurface.cs` | 同上 |
| 工具 | `Source/PuddingBrowser.AgentTools/BrowserLocatorInput.cs` | 新增 `includeHidden` 参数 + 两条映射路径（`ToDesktopLocator`/`ToLocator`） |

## 验证

| 套件 | 结果 |
|---|---|
| `PuddingDesktop.Tests` | **306 / 306**（原 305 ＋ 1） |
| `PuddingHost.Tests` | **253 / 253**（原 252 ＋ 1） |
| `Pudding.DesktopSurface.BrowserTests` | **59 / 59**（原 58 ＋ 1） |
| `PuddingBrowser.AgentTools.Tests` | **37 / 37**（原 36 ＋ 1） |
| `ContractsTests` · `DesktopServiceTests` · `AutomationTests` | 97/97 · 215/215 · 194/194 |

四层各有一条门禁：工具参数 → `DesktopLocator.IncludeHidden`（默认 false / 显式 true）；
桥接映射 → 运行时 `Locator.IncludeHidden`；能力通道映射 → 同上；
驱动脚本 → 两份脚本都含默认过滤（脚本文本断言）。

**取红实测**：从定位脚本里删掉默认过滤 ⇒
`LocateScripts_ExcludeHiddenElementsByDefault` **恰好变红**，其余 305 条仍绿；复原后 306/306 复绿。

## 顺带查清的一个框架级陷阱（重要，已记入 code_map）

测试第一次用 `locator.include_hidden` 传参时**没有绑定**。查因：
`Source/PuddingCore/Tools/PuddingToolContracts.cs` 的 `DeserializeArgs` **只重写根对象**的属性名
（`doc.RootElement.EnumerateObject()` + `ToPascalCase`），而 `BuildParameterSchema` 也是**扁平**的
（只列顶层参数，复杂类型只标类型）。因此：

- **顶层参数**：`include_hidden` 与 `includeHidden` 都能绑定；
- **嵌套字段**：只能用 camelCase/PascalCase（`includeHidden`），snake_case **静默不绑定**。

本轮按框架现状改用 camelCase，并把该约束写进 `BrowserLocatorInput` 的 code_map 条目。
（是否让框架递归归一化属于 `PuddingCore` 的共享行为变更，影响所有工具，未在本轮改动。）

## 已知边界（诚实登记）

- **`cursor` / `inViewportOnly` / `fields` 仍未实现**；`visible` 与 `inViewport` 也仍未分离 ——
  本轮只做了 `includeHidden`，且只有"不可见即不匹配"这一半，§6.2 要求的「写操作先滚入可见区域并复检」
  仍需驱动侧新增 inViewport 事实与滚动复检。
- **驱动侧过滤的真机行为未验证**：本环境跑不了真实 WebView2，脚本行为仍只有**文本断言** +
  明确标注；真实站点的"隐藏元素去噪"效果需外部进程内 smoke 复核。
- `includeHidden=true` 时定位会包含隐藏元素——**写操作仍会在动作前拒绝不可见目标**（交互脚本的
  `browser_element_not_visible`），这是刻意的：允许"看到"不等于允许"操作"。

## 影响与回滚

- **行为变化（Agent 可见）**：`browser_locate`/`browser_interact`/`browser_wait_for` 的
  `locator.includeHidden`（默认 false）现在真的生效——默认不再匹配隐藏元素，
  因此"同一选择器命中一堆隐藏模板节点"的噪音消失；需要时必须显式传 `includeHidden: true`。
- 无数据/配置影响；线缆 DTO 与 proto 未改动（字段随 `Locator` 走内存/桥接命令参数）。
- 回滚：还原上表 6 个生产文件与 4 个测试文件及 code_map。

## 关联

- 设计：[浏览器自动化可靠性与渐进阅读设计方案](../../../12_features/浏览器自动化可靠性与渐进阅读设计方案-2026-10-08.md)（§6.2）
- 前序：[B2 定位输出规则与拒绝建议](2026-10-08-B2定位输出规则与拒绝建议.md)、
  [B1 第五段 ref 页面作用域 token](2026-10-08-B1第五段ref页面作用域接入驱动.md)
