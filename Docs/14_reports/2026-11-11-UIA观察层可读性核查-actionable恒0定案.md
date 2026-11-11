---
title: UIA 观察层可读性核查（actionable 恒 0 定案）
author: 鲸鲸
date: 2026-11-11
last_reviewed: 2026-11-11
status: active
description: "用九轮探针实验定案 Windows CU 观察层 actionable 恒为 0 的原因，并排除后端、唤醒、焦点、异常吞没四类假说。"
categories: [docs, reports]
tags: [computer-use, uia, uiautomation, diagnostics]
related_docs: [Docs/12_features/2026-11-04-Windows-ComputerUse-实现方案.md, Docs/14_reports/2026-11-03-抖音客户端输入通道实测.md]
related_files: [Source/PuddingComputer.UIAutomation/FlaUiInspector.cs, Source/PuddingComputer.UIAutomation/UiaPreprobe.cs]
slug: reports-20261111-uia-actionable-rootcause
draft: false
---

# UIA 观察层可读性核查（actionable 恒 0 定案）

## 0. 一句话结论

`FlaUiInspector` 的 `actionable` 恒为 0，**不是判据太严，也不是探针异常被吞**；
直接原因是**读到的每个节点都是窗口框架级元素（Pane / TitleBar），它们不支持那五个 pattern**。
而更根本的问题在本轮被暴露：**本环境下 UIA2 客户端读不到应用内容树**——
15 个顶层窗口（含当前前台窗口）的后代总数只有 35，且三次连续测量完全不变。

## 1. 要回答的问题

生产代码 `FlaUiInspector.cs:216-220` 的五个探针写成：

```csharp
try { if (element.Patterns.Invoke.IsSupported) patterns.Add("Invoke"); } catch { }
try { if (element.Patterns.Value.IsSupported) patterns.Add("Value"); } catch { }
// SelectionItem / Toggle / Text 同形
```

于是「元素真的不支持」与「探针抛异常被 catch 吞掉」在结果上**不可区分**。
本核查要判定 actionable=0 属于哪一种。

## 2. 实验矩阵

全部为只读探针（枚举与属性读取，不点击、不输入、不抢焦点），位于 `.work/cu-probe/`（已 gitignore）。

| 轮次 | 装置 | 关键结果 |
|---|---|---|
| v1 | 仅 UIA2，枚举桌面 + 统计各 pattern | 15 窗口 / 35 元素 / **0 有 pattern** / **0 探针异常** |
| v2 | UIA2 与 UIA3 并排，同批窗口 | **两边完全一致**：各 15 窗口、总后代 35、pattern 0 |
| v3 | 硬编码类名做 TreeWalker dump | `NO_TARGET`——桌面窗口集合会变，硬编码不可靠 |
| v4 | UIA3 遍历全窗口 + RawView dump | **WINDOWS=0** |
| v5/v6 | 重跑 v4 两次 | 仍是 **WINDOWS=0**（非随机抖动） |
| v7 | 同上但改回 UIA2 | **WINDOWS=15**，子树可展开 |
| v8 | UIA2，同一窗口连测三次（间隔 400ms/200ms） | **35 / 35 / 35，0/15 个窗口变深** |
| v9 | UIA2，只读当前前台窗口 | ChatGPT（前台）descendants=**2**、pattern 0、异常 0 |

v7 的 RawView 展开结果（摘录）：

```
ChatGPT (Chrome_WidgetWin_1)      → Chrome_RenderWidgetHostHWND, Intermediate D3D Window
文件资源管理器 (CabinetWClass)     → TITLE_BAR_SCAFFOLDING_WINDOW, TitleBar
Program Manager (Progman)          → SHELLDLL_DefView
Visual Studio (HwndWrapper)        → TitleBar
Pudding (WinUIDesktopWin32…)       → InputNonClientPointerSource, ReunionCaptionControlsWindow,
                                     Microsoft.UI.Content.Desktop, TitleBar
```

**全部止步于框架层，没有一个进入应用内容。**

## 3. 被排除的假说

| 假说 | 判定 | 证据 |
|---|---|---|
| UIA3 后端更好，UIA2 有问题 | **反了**：UIA3 恒 0，UIA2 能枚举 15 个 | v4/v5/v6 vs v7 |
| 探针异常被 `catch` 吞掉 | **否**：全程 0 异常 | v1 `PROBE_EXCEPTIONS_TOTAL=0`；v9 同 |
| 预唤醒不足（`UiaPreprobe` 那种） | **否**：同窗口连测三次 35/35/35 | v8 `WINDOWS_THAT_GREW=0/15` |
| 窗口未激活所以不暴露子树 | **否**：当前前台窗口同样只有 2 个后代 | v9 |
| 屏幕锁定导致读不到 | **否**：`LogonUI.exe` 未运行，`dwm.exe` 正常 | `tasklist` 实测 |
| 会话断连 | **否**：`qwinsta` 显示 console 会话 ID 1，explorer 在其上运行 | `qwinsta` / `tasklist` |

顺带确立一条可复用事实：**本环境 UIA3 完全不可用（恒 0），UIA2 可用**。
生产选 `FlaUI.UIA2` 是正确的，`temp/uia-probe` 里用 UIA3 的历史探针在本机拿不到数据。

## 4. 未查明（不得据本文件声称）

**为什么内容树读不到**，机制未知。候选方向（**均未验证**）：

- UIA 客户端的完整性级别 / 权限与目标不匹配（探针进程由 Core 的 shell 派生）
- `UIAutomationCore` 的跨进程代理在本机被降级或未加载
- 远程/虚拟会话限制（本机装有 ToDesk，但当前会话为 console，未证实与远程有关）

**下一步的判别性实验**（未做）：读探针进程的完整性级别（`whoami /groups` 的 Mandatory Label）
与 `GetWindowThreadProcessId` 的会话/桌面归属；或换一个已知 UIA 树丰富的原生目标
（如以管理员身份启动的 WinForms 程序）作正对照。

## 5. 对 Windows CU 路线的影响

1. **`actionable` 判据本身不必改**：它算得对——元素确实不支持那些 pattern。
   改判据会掩盖更严重的问题。
2. **「视觉兜底」从可选项变成必需项**：UIA 路由在当前环境下拿不到控件，
   任何依赖 UIA 定位/写值的实现都无法端到端闭环。这与既有设计里
   「不要把通用视觉分割当成主控制面」的判断**相反**，需要重新裁定。
3. **`UiaAvailabilityClassifier` 的 `Sparse` 判定是对的**：35 个节点、0 可操作，
   确实该判稀疏。它一直在如实报告坏消息，只是没人听。
4. **验收脚本必须记录前置条件**：GUI 探针的结论依赖「会话可交互 + UIA 可读」，
   这两条都应写进报告的头部，否则结论不可复现。

## 6. 复现方式

```powershell
cd D:\CodeProject\PuddingAgent\PuddingAgent\.work\cu-probe
dotnet run --project Uia2ActionableProbe.csproj -v q
```

探针源码即上文 v9 版本；v1/v2/v7/v8 的变体保留在同目录日志
`run1.log` / `run2.log` / `run7-uia2.log` / `run8-warmup.log` / `run9-fg.log`。
