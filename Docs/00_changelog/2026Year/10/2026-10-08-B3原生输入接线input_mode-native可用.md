---
title: 2026-10-08 B3 原生输入接线：input_mode=native 真正走 CDP 真实键鼠
author: hyfree
date: 2026-10-08
last_reviewed: 2026-10-08
description: "把上一轮的 CDP 传输与序列规划接成可用能力：input_mode 原样下传到驱动，驱动按 NativeInputSequence 发真实键鼠事件（click/fill/type/press/check/hover），不支持的动作在工具与能力通道两处都明确 unsupported（不静默回落成 DOM）；inputBackend 如实报告 native，并说明 native 不做动作后回读所以回执通常是 dispatched。"
status: archived
categories: [docs, changelog]
tags: [browser, native-input, cdp, input-mode, b3]
related_docs: [Docs/12_features/浏览器自动化可靠性与渐进阅读设计方案-2026-10-08.md, Docs/00_changelog/2026Year/10/2026-10-08-B3原生输入基础件CDP传输与序列规划.md]
related_files: [Source/PuddingBrowser.WebView2/WebView2BrowserPage.cs, Source/Pudding.DesktopSurface.Browser/BrowserRuntimeDesktopSurface.cs, Source/PuddingBrowser.AgentTools/BrowserInteractTool.cs]
slug: changelog-b3-native-input-wired
draft: false
---

# 2026-10-08 B3 原生输入接线：input_mode=native 真正走 CDP 真实键鼠

## 目标 / 背景

上一轮（见 [B3 原生输入基础件](2026-10-08-B3原生输入基础件CDP传输与序列规划.md)）交付了 CDP 传输与键鼠序列规划，
并如实登记「还没接线：`input_mode=native` 仍被明确拒绝」。本轮接线，让 §7 的真实输入路径可用。

## 改动

| 层 | 文件 | 改动 |
|---|---|---|
| Abstractions | `BrowserTypes.cs` | 新增 `InputMode { Dom, Native }`；`ClickOptions`/`FillOptions`/`TypeOptions`/`KeyOptions`/`PointerOptions` 各带 `InputMode` |
| 驱动 | `WebView2BrowserPage.cs` | 新增 `ExecuteNativeAsync`：按定位符取句柄与**包围盒** → `NativeInputSequence` 规划 → 逐条 `SendCdpAsync`；**取消时 `ReleaseModifiers` 清理按键状态**；click/fill/type/press/hover 五处分派（`o.InputMode == Native` 时才走原生） |
| 能力通道 | `BrowserRuntimeDesktopSurface.cs` | `MapInputMode` 统一映射；**原生不支持的动作在入口就返回能力错误**（`unsupported_capability`，不是抛异常、更不是回落成 DOM）；click/fill/type/press/hover 的选项带上输入路径 |
| 工具 | `BrowserInteractTool.cs` | 删掉"native 一律失败"：改为 native + 支持动作 ⇒ 正常下发；native + 其他动作 ⇒ `browser_input_mode_not_supported` **且附带回执**（未开始/无副作用/不可重试 + 修复提示），一次驱动调用都不发生 |
| 驱动工程 | `PuddingBrowser.WebView2.csproj` | 增加对 `PuddingBrowser.Automation` 的引用（**执行层 → 规则层**，单向无环）：序列规则只能有一份，驱动只负责按序发出去 |

## 验证

| 套件 | 结果 |
|---|---|
| `AgentTools.Tests` | **58 / 58**（native 转发用例改写 + 新增不支持动作用例） |
| `DesktopSurface.BrowserTests` | **64 / 64**（＋1：native 下传 + 不支持动作拒绝） |
| `AutomationTests` · `ContractsTests` · `DesktopServiceTests` · `Host` · `PuddingDesktop` | 217/217 · 152/152 · 229/229 · 258/258 · 318/318 |

新增门禁：

- 工具层：`input_mode=native` + click ⇒ 请求里 `InputMode == Native` **且回执 `inputBackend == native`**（说了 native 就真的走 native）；
- 工具层：`input_mode=native` + select ⇒ `browser_input_mode_not_supported`，消息点明动作，回执 `not_started`/无副作用/不可重试，**驱动一次都没被调用**；
- 能力层：native click ⇒ 驱动收到的选项是 `click:native`；native select ⇒ 能力错误（`unsupported_capability`）**且驱动一次都没被调用**。

**取红实测**：把能力通道里"原生不支持就拒绝"的判断短路（＝允许静默回落）⇒ 对应用例**恰好变红**；复原后 64/64 复绿。

## 已知边界（诚实登记）

- **native 不做动作后回读**：动作后重查旧定位符有操作错位风险（设计明令禁止重查旧 Locator），
  因此 native 路径不回读元素值 ⇒ 回执通常是 `dispatched`（而非 verified）。要一次调用拿到写入结论，
  用 `input_mode=dom`；要确认 native 的效果，用后续 `browser_snapshot`/`browser_locate`/`wait_for`。
- **覆盖范围**：click/fill/type/press/check/hover。`select`（原生下拉需键盘/平台交互）、`drag`、`upload`、
  `scroll`（滚轮需要明确容器几何，本轮未接）在 native 下明确 unsupported。
- **真机未验证**：本环境跑不了真实 WebView2。CDP 命令序列有纯函数与映射验证，但"合成事件被真实站点接受"
  （焦点、iframe、跨域、`isTrusted` 相关行为）必须由外部进程内 smoke 复核。
- `type` 逐字符走 key code，非 ASCII 会跳过（应走 `fill`）；`fill` 用 `Input.insertText`，
  不触发逐键事件（需要逐键语义请用 `type`）。
- 能力层与工具层各自维护一份"支持哪些动作"的清单（两处都拒绝，但确实重复）；后续应收敛成一份共享判定。

## 影响与回滚

- **行为变化（Agent 可见）**：`browser_interact` 的 `input_mode=native` 从"一律明确失败"变成
  **真实键鼠输入**（click/fill/type/press/check/hover）；其它动作仍是明确失败（提示改为
  `input_mode=dom`）。默认值仍是 `dom`，既有调用方不受影响。
- 回滚：还原上表 4 个生产文件与 3 个测试文件、csproj 与 code_map。

## 关联

- 设计：[浏览器自动化可靠性与渐进阅读设计方案](../../../12_features/浏览器自动化可靠性与渐进阅读设计方案-2026-10-08.md)（§7）
- 前序：[B3 原生输入基础件：CDP 传输与键鼠序列规划](2026-10-08-B3原生输入基础件CDP传输与序列规划.md)（本轮接线的对象）
