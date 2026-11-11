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

## 9. 第三轮（同日 12:57）：不是 dll，也不是发布方式

§8.2 留下的假设是「framework-dependent vs 自包含/私有副本」。本轮验证并**证伪**了它。

做法：把同一探针 `dotnet publish -r win-x64 --self-contained`。

| 步骤 | NATIVE_DESC | 说明 |
|---|---|---|
| self-contained 基线 | 2 | 运行时与框架全在 app 目录，仍是 2 |
| 用 Codex 那 4 个 UIA dll 覆盖 publish 目录 | 2 | 仍然读不到 |

**覆盖是否真的生效？** 为了不重犯前两轮的错误，先加了自证：

```
UIA_ASM_LOC=D:\...\.work\winforms-uia-probe\publish\UIAutomationClient.dll
UIA_ASM_VER=10.0.0.0
```

即程序集确实从 app 目录加载——**所以「覆盖无效所以才没变化」这个退路不成立**，
dll 本身不是差异源。

### 9.1 至此被硬性排除的完整清单

环境（§7 误判）、FlaUI 库（§7→§8 两次误判）、STA/MTA 套间、缺消息泵、
运行时补丁版本（8.0.31 / 10.0.12 同结果）、缺程序集文件、app 目录覆盖（框架依赖时不生效）、
**self-contained 发布与 dll 替换（本节）**。

### 9.2 剩下唯一的硬事实

**同一台机、同一窗口句柄 `0x170B26`、同一分钟内**：

- `pwsh` 进程 → 原生 469 / FlaUI 469
- 本仓任何 .NET 进程（控制台 / WinForms / self-contained / 任意 TFM）→ 2

**差异在进程本身，机制未查明。** 本轮不再猜：
连续三个假说（环境、库、dll）全部被证伪，已触发“继续发散无益”的门槛。

### 9.3 可以马上用的东西

既然 `pwsh` 是唯一已知能读到完整 UIA 树的宿主，**它能当现成的 sidecar**：

- 已验证可用的路径：`pwsh -File <script>` 里 `Add-Type -AssemblyName UIAutomationClient`
  （或加载 FlaUI net8.0 程序集），输出 JSON 给主进程
- 与 §8.3 的 sidecar 结论一致，只是宿主选择从“自建 helper”变成“先用 pwsh 验价值”
- 代价：一个进程往返 + 序列化；收益：立刻能做只读观察，不必等根因

### 9.4 下一个真正有判别力的实验（未做）

用 **Process Monitor** 看两边分别加载了哪些 `UIAutomationCore.dll` / `oleacc.dll` / 原生代理，
或对比 `pwsh` 与自己进程的清单（DPI 感知 / 兼容性 shim / AppCompat 注册表项）。
这需要文件/注册表实时监控工具，本会话内没有。

## 10. 第四轮（同日 13:29）：又排除三项，只剩“进程身份”

| 假说 | 做法 | 结果 |
|---|---|---|
| 运行时版本 | 把 pwsh 目录整套 .NET 10.0.14 覆盖到 publish（CLIENT/CoreLib 均为 10.0.1426.52326） | **仍为 2** |
| client-side provider 未加载 | 显式 `Assembly.Load("UIAutomationClientSideProviders")`，成功（10.0.0.0）后再读 | **仍为 2** |
| 窗体抢焦点，实际测到后台窗口 | 输出测量时刻的 `GetForegroundWindow` 与捕获值对比 | `FOCUS_STOLEN=False`，**仍为 2** |

至此已排除九类：环境、FlaUI 库、STA/MTA、缺消息泵、运行时补丁版本、
缺程序集文件、app 目录覆盖、self-contained/dll 替换、client-side provider、焦点状态。

### 10.1 唯一剩下的差异

**进程身份**：`pwsh.exe`（读到 469） vs 自编译 exe（读到 2）。
两者同为 x64、同为 .NET 10（巳对齐到 10.0.14）、同窗口、同套间。

方向猜测（**未验**，列出来是为了下次不重复搜）：

- `pwsh.exe` 的内嵌清单（DPI 感知 / `longPathAware` / 兼容性设置）影响 UIA 行为
- `pwsh.exe` 额外加载了某个我未枚举到的原生 dll（需 Process Monitor）

### 10.2 判断：不再往下挖

本轮又证伪三个假说，累计九个。每轮成本约 30-40 分钟，产出是“排除”而不是“修复”。
按范围纪律，停止发散。若要继续，前置条件只有一个：**拿到 Process Monitor（或等价工具）
对比两边加载的原生 dll 与进程清单**——没有它就只能继续猜。

### 10.3 现在就能用的方案（建议采纳）

**pwsh sidecar**：主进程把一个请求（窗口句柄或标题匹配 + 要什么）交给
`pwsh -File observe.ps1`，脚本读 UIA 树并回 JSON。

- 已实测可行：同一脚本在本轮多次运行稳定返回 469~1622 个元素、569~574 个 Invoke
- 风险可控：只读、无写入；脚本本身可审计
- 代价明确：进程往返 + JSON 序列化（估计十到百毫秒量级，**未测**）
- 与 §8.3 的 sidecar 结论一致——同一个架构决策，只是宿主先借用 pwsh，
  待根因查清后可直接换成自建 helper，**上层接口不变**

### 10.4 写这段时的自我提醒

四轮里我错了三次（环境、FlaUI、dll/发布方式），每次都是“归因过快”。
本轮的三个假说之所以可信，是因为每一步都先输出自证字段（程序集路径、
加载版本、焦点句柄），而不是只看最终那个数字。

## 11. sidecar 原型已验证可用（同日 14:15）

不再追根因，改为把 §10.3 的建议做成可跑的东西，并补上之前标为「未测」的延迟数据。

脚本：`Tools/UiaSidecar/observe.ps1`（只读；不点击、不输入、不抢焦点、不写注册表）。

### 11.1 实测结果

```
pwsh -NoProfile -File observe.ps1 -OutPath out.json -TitleLike ChatGPT

WALL_MS=924      端到端（含进程启动）
SCRIPT_MS=568    脚本内 UIA 读取
ELEMS=469
ACTIONABLE=142
OK=True
```

对比同一台机、几分钟前生产的口径：**`actionable` 恒为 0**。
sidecar 立刻给出 **142** 个可操作元素。

返回的 actionableSample 是真实按钮，举例如下（来自 ChatGPT 窗口的会话列表）：
「评估 Chromium DPI 坐标处理」「置顶聊天」「归档聊天」「规划 PuddingAgent 记忆小模型」——
这些就是生产层一直看不到的东西。

### 11.2 延迟量级说明

- **924 ms 是冷启动**（新 pwsh 进程 + `Add-Type` 编译内嵌 C#）
- 适合「观察一次」，**不适合高频轮询**！若要高频，需常驻 sidecar（stdin/stdout 协议）
- 优化空间（未做）：预编译 Add-Type 类型、或直接用 FlaUI net8.0 程序集（§8 已验证在 pwsh 里同样读到 469）

### 11.3 未验证 / 不得据本节声称

- 并发调用（多个请求同时拉起 pwsh）的行为
- 目标窗口在观察过程中关闭 / 变成无响应时的降级行为
- 跨会话（非当前 console 会话）的目标窗口
- 该脚本**未接入任何生产链路**，仅作可跑原型；是否采纳为架构组成部分需用户裁定

### 11.4 冷启动优化（同日 15:02）

§11.2 把 924 ms 归因于冷启动。定位后发现：真正的开销是脚本里那句
**无条件执行的 `Add-Type -TypeDefinition`**（编译内嵌 C# 拿 `GetForegroundWindow`），
而不是 pwsh 进程启动本身。

修法：新增 `-Hwnd` 参数（调用方本来就知道句柄），并把那段 `Add-Type`
**推迟到只有“真的需要前台窗口”时才编译**。

实测对比（同一窗口、同一分钟）：

| 路径 | 优化前 | 优化后 |
|---|---|---|
| `-TitleLike ChatGPT` | 924 ms | **182 ms** |
| `-Hwnd <十进制>` | — | **161 ms** |
| 无参数（前台窗口，仍会编译） | 924 ms | 246 ms |

读数完全不变：`elems=469`、`actionable=142`。

**结论修正**：延迟从「近 1 秒」降到 **160~250 ms**，单次观察已可接受；
高频轮询仍需常驻 sidecar（stdin/stdout），因为剩下的成本主要是进程启动本身。

### 11.5 冒烟测试当场抓到一个真缺陷（同日 15:48）

为 sidecar 写冒烟测试（`TestScripts/test-uia-sidecar.ps1`）时，**首次运行就抓到
被测物的一个真缺陷**：

```
observe.ps1 -TitleLike NONEXISTENT_WINDOW_XYZ_123
  → ok=true, source=foreground      ❌
```

即：**指定了标题但没匹配到时，会静默回退到前台窗口**。调用方看到 `ok=true`，
会以为拿到了目标窗口，实际读的是一个无关窗口——正是我一直批评的「静默错位」写法，
而且是我自己写下的。

修法：未匹配时显式返回 `ok=false, error=title_not_found` 并立即返回。

**取红验证**：

| 状态 | 结果 |
|---|---|
| 修复版 | `RESULT: OK (3/3)` |
| 变异（把该分支改成 `if ($false)`） | **C 失败**：`got ok=true source=foreground (silent fallback to foreground!)` |
| 复原 | `RESULT: OK (3/3)`，且 `findstr MUTANT` 无残留 |

### 11.6 测试自身的第一个 bug 也当场暴露

首跑时 C 用例报的是 `bad_hwnd: ToInt64 ... Could not find any recognizable digits`——
原因不在被测物，而在**测试传参方式**：用**数组** `@('-TitleLike','xxx')` 做 splat 时，
PowerShell 按**位置**绑定，`'-TitleLike'` 变成了 `$TitleLike`、`'xxx'` 变成了 `$Hwnd`。
于是这个用例测的根本不是它声称的东西。

改用 **hashtable splatting** 后才真测到该测的路径。这条写进测试注释：
“数组 splat 会按位置绑定，使用例测错目标”。

**两条一起看**：写测试的过程本身发现了两个缺陷——一个在被测物，一个在测试；
如果只靠“跑一遍看起来全绿”是发现不了第二个的，因为它会红在一个**看起来合理**的
错误信息上。







