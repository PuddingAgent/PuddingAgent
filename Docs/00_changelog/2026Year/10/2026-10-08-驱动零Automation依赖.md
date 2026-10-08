---
title: 2026-10-08 驱动共享源码零 Automation 依赖：原生输入/截图/事件上报也走注入
author: hyfree
date: 2026-10-08
last_reviewed: 2026-10-08
description: "把上一轮登记的'两个例外'清零：原生输入的调用序列、截图参数语义、页面事件日志都搬进 IBrowserDriverPolicy（返回 DriverCall 序列 / 参数 JSON / 事件日志工厂，null 表示该面不支持），驱动彻底不再引用 PuddingBrowser.Automation，所有 WINUI_BROWSER 能力守卫全部删除（只剩文件里既有的那个 WinRT 事件委托守卫）。结果：WinUI 面与桌面路径共用同一份策略、行为一致，边界从'两处例外'变成'零例外'。"
status: archived
categories: [docs, changelog]
tags: [browser, boundary, dependency-injection, native-input, screenshot]
related_docs: [Docs/12_features/浏览器自动化可靠性与渐进阅读设计方案-2026-10-08.md, Docs/00_changelog/2026Year/10/2026-10-08-驱动策略注入接上生产路径.md]
related_files: [Source/PuddingBrowser.Abstractions/DriverPolicy.cs, Source/Pudding.DesktopService/Browser/AutomationDriverPolicy.cs, Source/PuddingBrowser.WebView2/WebView2BrowserPage.cs]
slug: changelog-driver-zero-automation-deps
draft: false
---

# 2026-10-08 驱动共享源码零 Automation 依赖：原生输入/截图/事件上报也走注入

## 目标 / 背景

上一轮把事件策略接上了注入，但登记了两个例外：**原生输入/原生滚动/截图**与**事件事实上报**仍在 seam 之外
（靠 `#if WINUI_BROWSER` 在 WinUI 面标成 unsupported）。本轮清零。

## 改动

| 层 | 改动 |
|---|---|
| Abstractions `IBrowserDriverPolicy` | 增加：`PlanClick/PlanHover/PlanScroll/PlanFill/PlanType/PlanPress/ReleaseModifiers`、`BuildScreenshotParameters`、`CreateEventLog`；新增 `DriverCall(Method, ParametersJson)` 与 `IBrowserPageEventLog`。**`null` 是"该面不支持"的统一表达**（驱动据此 fail closed，不用异常当控制流） |
| `NullBrowserDriverPolicy` | 安全默认照旧（对话框/三类事件）；原生输入与截图**明确返回 null ⇒ 不支持**；事件日志返回 null ⇒ 如实不上报（不编造） |
| DesktopService 适配器 | 全部委托给 `NativeInputSequence`/`ScreenshotPlan`/`ScriptDialogPolicy`/`PageEventPolicy`/`PageEventLog`；日志适配器负责 `(xN)` 渲染（逻辑仍在 Automation） |
| 驱动 | `using PuddingBrowser.Automation` **删除**；原生输入/滚动改调 `Policy.Plan*`（null ⇒ `browser_input_mode_not_supported`）；截图参数改调 `Policy.BuildScreenshotParameters`（null ⇒ unsupported）、内容尺寸读取与 base64 解码/字节预算内联（都是通用逻辑，非策略）；事件日志改为 `Policy.CreateEventLog()`；**三处 `#if WINUI_BROWSER` 能力守卫全部删除** |

结果：共享驱动源码只依赖 Abstractions（唯一剩下的 `#if WINUI_BROWSER` 是文件里**既有**的 WinRT 事件委托差异，
与本议题无关）；**WinUI 面与桌面路径现在共用同一份策略**，原生输入/滚动/截图/事件上报与摘要都一致。

## 验证

| 项 | 结果 |
|---|---|
| `PuddingBrowser.AutomationTests` | **270 / 270** |
| `DesktopServiceTests` | **231 / 231** |
| `AgentTools` · `PuddingDesktop` · `Host` · `DesktopSurface.Browser` · `Contracts` | 63/63 · 320/320 · 258/258 · 65/65 · 152/152 |
| 构建 | `WebView2` · **`WinUI`** · `DesktopService` · `WpfArchive` · `DesktopSurface.Browser` 全部**已成功生成** |

关键点：`WinUI` 在**删掉全部能力守卫之后**仍然编译通过 —— 这正是"零 Automation 依赖"的证据
（此前它编不过就是因为共享源码引用了 Automation）。

## 已知边界（诚实登记）

- **真机未验证**：WinUI 面现在"编译上具备"原生输入/滚动/截图/事件上报，但注入策略在真实运行时被调用、
  以及 CDP/事件在 WinUI WebView2 上的行为，都需要外部 smoke（本环境无 WebView2）。
- **没有策略就没有原生能力**：任何未注入 `DriverPolicy` 的宿主拿到的是 `NullBrowserDriverPolicy` ⇒
  原生输入/截图**明确 unsupported**（不是静默回落）。生产两个宿主都已注入（上一轮）。
- **截图的内容尺寸读取与 base64 解码/字节预算已内联到驱动**（它们不是"策略"而是通用解析/判定）：
  代价是这两段不再有 AutomationTests 级别的单元测试（原来的 `ScreenshotPlan` 测试仍覆盖策略侧构造；
  解析与预算是新内联代码）。若要恢复覆盖，应把"解析 CDP 结果"抽成 Abstractions 的静态辅助并补测试。
- `ScriptDialogCount` 这个诊断计数在新实现下**不再累加**（日志改由注入的日志持有）；若还需要，应挂在
  `IBrowserPageEventLog` 上。已在代码里保留属性但注明其语义变化，避免被当成可靠指标。
- `PuddingDesktop` 整体产物仍未重新生成（本机运行中的 Shell 占用输出文件）。

## 影响与回滚

- **行为变化（WinUI 面）**：原生输入/原生滚动/截图从"显式 unsupported"变为**可用**（策略由宿主注入）；
  页面事件事实上报与桌面路径一致。
- **行为变化（无策略宿主）**：与原 `Null` 策略一致（明确 unsupported + 安全默认接管）。
- 回滚：还原 `DriverPolicy.cs`、适配器、驱动（含守卫与内联解析）。

## 关联

- 设计：[浏览器自动化可靠性与渐进阅读设计方案](../../../12_features/浏览器自动化可靠性与渐进阅读设计方案-2026-10-08.md)（§7、§8）
- 前序：[驱动策略注入接上生产路径](2026-10-08-驱动策略注入接上生产路径.md)（本轮清零它登记的例外）
