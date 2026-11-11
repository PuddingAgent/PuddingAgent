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

> ⚠️ **本节结论已于同日 11:55 校正**——见 §7。
> 原文说「本环境下 UIA2 客户端读不到应用内容树」，**这是错的**。
> 同一台机器上 .NET 原生 `System.Windows.Automation` 读到 **1615 个后代 / 569 个 Invoke**，
> 而 FlaUI.UIA2 只读到 35 / 0。问题在 **FlaUI 这一层**，不在环境，也不在 UIA 本身。
> 因此 §5 的「视觉兜底变必需」**不再成立**。

`FlaUiInspector` 的 `actionable` 恒为 0，**不是判据太严，也不是探针异常被吞**；
直接原因是**读到的每个节点都是窗口框架级元素（Pane / TitleBar），它们不支持那五个 pattern**。
而更根本的问题在本轮被暴露：**通过 FlaUI 读不到应用内容树**——
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
2. **「视觉兜底」仍是可选**（校正于 11:55）：原生 UIA 能读到 1615 个元素 / 569 个 Invoke，
   说明 UIA 路由本身可用，阻碍在 FlaUI 库。换实现或升级即可，
   不需要推翻既有「不要把通用视觉分割当成主控制面」的设计判断。
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

## 7. 校正记录（同日 11:55）——上一轮的「环境」结论是错的

### 7.1 新证据

用**完全不同的 UIA 客户端实现**（Windows PowerShell + .NET 原生
`System.Windows.Automation`，脚本 `.work/uia-ps-probe.ps1`）读同一批窗口：

```
ROOT_CHILDREN=15
Shell_TrayWnd          DESC=37   INVOKE=13
Chrome_WidgetWin_1     DESC=469  INVOKE=191   ← ChatGPT
Chrome_WidgetWin_1     DESC=250  INVOKE=35
Window (VS)            DESC=333  INVOKE=60
Tauri Window (Clash)   DESC=310  INVOKE=217
Chrome_WidgetWin_1     DESC=121  INVOKE=41
TOTAL_DESCENDANTS=1615  TOTAL_INVOKE_PATTERN=569  TOTAL_VALUE_PATTERN=208  ERRORS=0
```

对照 FlaUI.UIA2 的同一批窗口：**35 个后代 / 0 个 pattern**。

### 7.2 因此必须改正的两条

| 原结论 | 校正后 |
|---|---|
| 本环境下 UIA 客户端读不到内容树 | **错**。原生客户端读到 1615/569；阻碍在 **FlaUI 这一层** |
| 视觉兜底从可选变必需 | **不成立**。UIA 路由可用，换实现即可 |

### 7.3 仍然成立的结论（未受影响的）

- `actionable` 判据本身没写错，不需要放宽（§5.1）
- 探针异常未被吞：FlaUI 与原生两侧都零异常（§2/§3）
- 预唤醒不足、前台/非前台、屏幕锁定、会话断连四个假说仍为已证伪（§3）
- 本机 **UIA3（FlaUI.UIA3）恒为 0** 这一观测本身仍成立，但已不能归因于「环境勿支持 UIA」

### 7.4 本轮新增的未查明项

**FlaUI 为什么读不到**：同一个 `AutomationElement` 在原生侧能返回 469 个后代，
在 FlaUI 5.0.0 侧只返回 2 个，且 `RawViewWalker` 逐层走也停在第一层。
候选（**均未验证**）：

- FlaUI 5.0.0 在 `net10.0` 下的 COM 互操作缺陷（UIA2 包装 `System.Windows.Automation`，
  底层与原生客户端相同，差 46 倍不该是语义差异）
- 某个 `AutomationOptions` / `ConnectionTimeout` / `TransactionTimeout` 默认值导致静默截断

**判别性下一步**（未做）：查 FlaUI 5.0.0 是否有更新版本或已知 issue；
或在同一 C# 进程里分别调原生 `AutomationElement` 与 FlaUI，确认差异可在进程内复现。

### 7.5 教训

上一轮的「环境级」结论建立在**单一客户端实现**的观测上。
当时我已排除四个假说，却没想到最该排除的那个：**换一个不依赖 FlaUI 的客户端**。
“环境不行”是一个强结论，它应当需要比“库不行”更强的证据——因为前者会直接改变路线判断。

## 8. 第二轮定位（同日 12:25）：FlaUI 无罪，差异在**宿主进程**

### 8.1 实验矩阵（同一台机、同一窗口、前后十几分钟内）

| # | 宿主 | 加载的 UIAutomationClient | 原生 UIA 后代数 | FlaUI 后代数 |
|---|---|---|---|---|
| 1 | `powershell.exe` 5.1（.NET Framework） | GAC v4 | **1615** | 未测 |
| 2 | `pwsh` 7（Codex 私有 WindowsDesktop 10.0.14） | 私有副本 | **1622 / 469** | **469** |
| 3 | C# `net10.0-windows` 控制台（UseWPF） | SDK 10.0.12 | 2 | 2 |
| 4 | C# `net10.0-windows` WinForms（有消息泵） | SDK 10.0.12 | 2 | 2 |
| 5 | C# `net10.0-windows` 仅 STA 线程，主线程不碰 UIA | SDK 10.0.12 | 2 | — |
| 6 | C# `net8.0-windows` WinForms | SDK 8.0.31 | 2 | 2 |

注：第 2 行是定案证据——**FlaUI.UIA2 在 pwsh 里同样读到 469**。
由此可以硬性排除：

- ❌ **FlaUI 库问题**（§7 的推断错了：FlaUI 只传递宿主的行为）
- ❌ **STA/MTA 套间**（第 5 行）
- ❌ **缺消息泵**（第 4 行：WinForms 宿主同样 2）
- ❌ **运行时补丁版本**（第 3 行 10.0.12 与第 6 行 8.0.31 都是 2）
- ❌ **缺程序集文件**：标准框架目录与 Codex 副本都有 UIAutomationClient /
  ClientSideProviders / Provider / Types 四个 dll
- ❌ **改 app 目录的同名 dll**：框架程序集不从 app 目录加载，拷了也不生效

### 8.2 剩下的事实，不包含因果

- **Codex 自带的那套 WindowsDesktop（10.0.14、私有目录）能读到完整树**；
  标准安装的 shared framework（10.0.12 / 8.0.31）读不到。
- **本机只有 10.0.12 与 8.0.31**（`dotnet --list-runtimes` 实测），没有 10.0.14。
  所以凡是 framework-dependent 的 net8/net10 Windows 应用，在本机都读不到。
- 生产的 Core 是 framework-dependent 的 net10.0-windows ⇒ **它落在读不到的那一侧**。

**为什么读不到：仍未查明**。下一步的判别实验（未做）：
把 Codex 那套私有 WindowsDesktop 作为 `DOTNET_ROOT`，或用 self-contained 发布，
验证「framework-dependent vs 自包含/私有副本」是不是真正的分界。

### 8.3 对生产的直接含义

1. **`actionable` 恒 0 不是判据错**（§5.1 仍成立），而是上游只拿到壳层元素。
2. **FlaUI 不需要换**——同一套代码在能读到的那侧完全正常。
3. **最稳妥的修正不是碰运行时，而是把 UIA 观察放 sidecar 进程**：
   一个能读到完整树的宿主进程负责观察，主进程通过 IPC 取结果。
   这同时解决我之前倾向的「TFM 边界与 provider 阻塞隔离」——同一个决策。
   代价：多一层进程协议与超时语义；收益：不依赖宿主运行时版本、
   不把 COM/UIA 的故障面扩散到主进程。

### 8.4 本轮新增的自我校正

§7 把阻碍归因于 FlaUI 库，同样是**基于不完整对照的归因**——当时只换了客户端实现，
却没在同一宿主里分别测原生与 FlaUI。两个轮次都犯了同一个错误：
**看到两组数字不同，就把它归给最近的那个变量**。
可靠的归因需要：同一宿主内分行测（本轮 §8.1 第 2、3 行才是合格对照）。


