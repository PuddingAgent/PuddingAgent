---
title: Windows Computer Use 实现方案（基于 2026-11-03/04 实测）
author: 鲸鲸
date: 2026-11-04
last_reviewed: 2026-11-04
status: proposed
description: 用本机实测证据给出 Windows 桌面自动化的能力阶梯、各层可靠性边界与组件划分，明确哪些是已证、哪些待验。
categories: [docs, features]
tags: [computer-use, windows, uia, accessibility, design]
related_docs:
  - Docs/14_reports/2026-11-03-抖音客户端输入通道实测.md
  - Docs/14_reports/2026-11-04-Codex-ComputerUse实现调研.md
  - Docs/14_reports/2026-11-04-DeepSeekHarness-ComputerUse调研.md
  - Docs/13_runbooks/Computer-Use.md
related_files: []
slug: features-windows-computer-use-2026-11-04
draft: false
---

# Windows Computer Use 实现方案

> 本文只写**本机实测过**的东西，或明确标注为「未验证」。凡未验证的，不写成设计前提。

## 一、结论先行

Windows CU 的难点不在 API 稀缺，而在**语义隐式**：同一段代码在不同应用、不同页面、
不同时序下结果不同，且失败常常是静默的。因此设计的第一原则是：

**每一步都必须能产出「可核对的证据」，而不是「已派发」的回执。**

## 二、能力阶梯（按可靠性排序）

| 层 | 手段 | 本机证据 | 结论 |
|---|---|---|---|
| L1 观察 | UIA 树（UIA3）+ **先 MSAA 唤醒** | Chromium 首采 1 → 唤醒后 4；VS Code 3 | 可用，但顺序敏感 |
| L1 观察 | 截图（WGC 优先于 BitBlt） | 未实测 | 待验，Codex 用 WGC 可截遮挡窗口 |
| L2 定位 | 元素选择器（AutomationId/Name/Type/Class） | 已有组件与 41 用例 | 可用；多命中必须报歧义 |
| L2 定位 | 截图坐标 | 曾因坐标换算错而连错多次 | **仅在元素不可用时降级**，且必须带截图凭据 |
| L3 输入 | 无障碍写值（ValuePattern） | douyin 搜索框写值 6/6 读回一致，且推荐词重算 | **首选**，不依赖键盘 |
| L3 输入 | 无障碍调用（InvokePattern） | 组件已实现；douyin 当前页无按钮可验 | 待验（需页面配合） |
| L3 输入 | 窗口消息点击（WM_LBUTTON*） | douyin 导航切页有效、搜索框下拉开合有效 | 可用，但**不建立编辑焦点** |
| L3 输入 | SendInput 鼠标 | douyin 播放页无效、ChatGPT 客户端无效 | **不可依赖** |
| L3 输入 | SendInput 键盘 | douyin 全通道无反应（含空格、Unicode、Ctrl+V） | **不可依赖** |
| L3 输入 | 驱动级 HID | 未实测 | 最后手段，见 §五 |

**一条贯穿证据**：输入"被系统接受"（`SendInput` 返回 n/n）与"应用真的收到"是两件事。
本机所有键盘失败案例，`SendInput` 都返回全部接受。

## 三、三个已证伪的错误假设（不要重犯）

1. **「树会长大，等久一点就有」** —— 唤醒后 8 秒内节点数恒为 4，不再增长；78 与 4 是**不同页面**。
2. **「换个 UIA 库就能解决」** —— WPF / FlaUI / 裸 COM 在稳定态下完全一致（18/18 采样全为 78）；
   差别只在**谁能唤醒树**。
3. **「点击不生效就是被应用过滤」** —— 首次点击常常只用于**激活窗口**；先证明窗口已激活，再谈是否被过滤。

## 四、组件划分建议

```
Pudding.Contracts            ← 契约（ElementSelector / 快照 / 写值结果）
PuddingComputer.UIAutomation ← 观察 + 写值 + 调用（含 MSAA 唤醒），只读树，不含输入注入
PuddingComputer.Windows      ← 窗口枚举 / 坐标换算 / SendInput（注入通道）
PuddingComputer.Automation   ← 纯规则（可用性分类、快照标注），不碰 IO
Pudding.CuHelper（拟）       ← 独立进程：承载 UIAutomation，JSON-RPC 暴露给宿主
```

**为什么需要独立进程**：`PuddingRuntime` 是 `net10.0`，`UIAutomation` 必须 `net10.0-windows`
（FlaUI 只提供 `-windows` 资产），二者无法在同一程序集图里共存。独立进程同时解决
**TFM 边界**与**provider 阻塞隔离**两个问题，代价是超时与生命周期管理。

## 五、驱动级 HID 的准入条件

只有同时满足才考虑：①应用层路径（写值/调用/菜单命令）全部无效 ②有交叉对照证明
「实体输入可行、合成输入被拒」 ③用户明确授权安装内核或 UMDF 驱动。

代价：需管理员、可能要重启、与 Secure Boot/内存完整性存在兼容风险；
且**装完驱动后原来的 `SendInput` 不会自动改走 HID**，必须换发送 API。

## 六、验收判据（每个动作都要能取红）

| 动作 | 成功判据 | 失败时该说什么 |
|---|---|---|
| 观察 | 节点数 + 可操作数 + 可用性分类，且锚点（根类名）可读 | 「读到 N 个，可能是页面本身如此」 |
| 写值 | **重新解析后读回一致**；更强：页面据此重算（如推荐词） | 「写了但读回不符」或「页面无变化」 |
| 调用 | 调用成功 **≠** 生效；必须另有观察证据 | 「已调用，效果未确认」 |
| 点击 | 落点命中测试 + 目标窗口已激活 + 之后状态变化 | 「已派发，未观察到位移」 |

## 七、未解问题（按优先级）

1. **提交类动作**：按钮在树上无名字时如何定位并触发（douyin 当前页 `Edit=0`，无法推进）
2. **独立 helper 的协议与超时**：JSON-RPC over pipe，需定请求预算与取消语义
3. **截图通道**：当前实现 (`WindowsScreenCapturer`) 用的是**屏幕 DC 的 BitBlt**
   （源码注释写明“不向 Chromium 窗口请求后台渲染”）。这意味着**被遮挡或最小化时拿不到内容**。
   Codex 走的是 WGC（`GraphicsCaptureItem` + `Direct3D11CaptureFramePool`，可截被遮挡窗口），
   本机未实测 WGC 的可用性与性能。若后续需要“后台可截”，这是必选项。
4. **多显示器 / DPI**：已有组件，但跨屏负坐标场景未实测

## 七之二、新增：`actionable` 恒为 0 的两种可能（2026-11-11 02:30，代码核实，未验行为）

### 现象

2026-11-04 实测两个真实目标，`nodes` 能读到但可操作数均为 0：

| 目标 | 类型 | nodes | actionable |
|---|---|---|---|
| AMD Bug Report Tool | 原生 WinForms | 13 | 0 |
| DeepSeek Harness | Electron | 4 | 0 |

### 判据与填充逻辑

`UiaContracts.cs:74`：

```csharp
public bool IsActionable => IsEnabled && !IsOffscreen && Patterns.Count > 0;
```

`FlaUiInspector.cs:198-219`：`enabled` 默认 **true**、`offscreen` 默认 **false**（异常时保持默认值，偏向“算可操作”），
所以 actionable=0 只可能是 **所有节点的 `Patterns` 都为空**。

而 `Patterns` 是五个独立探针，**每一个都用 `catch { }` 吞掉异常**：

```csharp
try { if (element.Patterns.Invoke.IsSupported) patterns.Add("Invoke"); } catch { }
try { if (element.Patterns.Value.IsSupported) patterns.Add("Value"); } catch { }
// SelectionItem / Toggle / Text 同理
```

### 两种解释，现有代码区分不了

1. 那些元素**真的**不支持这五个模式；
2. 五个探针**都抛了异常**，被 `catch { }` 吃成“不支持”。

**这一点比现象本身严重**：静默吞异常使「读不到」与「真的没有」输出完全一致的信号。
它与本仓另一条已确认的缺陷（`search_grep` 省略 directory 时的静默错位）同属一类：
**失败不可见**。

### 测试侧的证据

`PuddingComputer.UIAutomationTests` 里 `Actionable` 相关用例**全部用合成节点**构造
（`Node(enabled, offscreen, patterns: "Invoke")`），即只验证了 `IsActionable` 这个布尔表达式。
**没有任何用例证明 `FlaUiInspector` 能从真实元素上读出至少一个 pattern**。
因此“全零”可以长期存在而不被任何测试拦下。

### 要定案需要的最小实验

对一个已知可点的原生按钮（如 WinForms 的 `Button`）：
- 若 `Patterns` 能读到 `Invoke` → 解释 1；说明应用差异，不是 bug
- 若 `Patterns` 为空 → 解释 2；说明探针或前置（UIA2 预探测）有缺陷

**未做**：本会话无 shell，无法构建探针程序；也无直接暴露 UIA 的工具。
**不得据本节声称**：actionable=0 是 bug。它现在只是「两种解释之一，尚未区分」。

## 后台截图：PrintWindow 初步结果（未定论，2026-11-04 09:30）

对 douyin 窗口（2304×1315）试 `PrintWindow(hwnd, dc, PW_RENDERFULLCONTENT=2)`：

```
PRINTWINDOW ok=True  samples=10608  nonBlackRatio=1
```

**这个结果不足以定论**：`nonBlackRatio=1` 既可能是“真的截到了内容”，
也可能是“截到的是一张全非黑的垃圾图”（噪声图的非黑比例同样接近 1）。
判据本身不成立——我用的指标区分不了这两种情况。

要做成结论，至少需要：
1. 同一时刻再用 BitBlt 截一张，**逐像素比对两张图的相似度**（而不是各看各的非黑比例）
2. 把窗口遮挡住（或用另一窗口覆盖）后再截一次，看是否仍一致

在此之前，文档里“被遮挡时拿不到内容”的结论**只对当前 BitBlt 实现成立**，
不能推广成“Windows 做不到后台截图”。
---

## 补充证据：向 Electron 应用（ChatGPT 桌面客户端）发送消息，2026-11-04 16:56

**目标动作**：把一个 1421 字的方案请求写进 Composer 并提交。

**通道（已验证成功）**

1. `ValuePattern.SetValue` 写入编辑器
2. **重新解析元素并读回**，与实际文本比对一致（1420 vs 1421，差一个尾部换行）
3. `SetFocus()`
4. `keybd_event(0x0D)` 按下 + 抬起

**成功证据**：消息全文出现在会话记录里，GPT 随后开始回复。

### 三条可复用的经验

**① 用 `ClassNameProperty='ProseMirror'` 精确匹配会失配**
实际 class 是 `ProseMirror ProseMirror-focused`（带状态后缀）。
可靠定位方式是 `ControlType.Edit` 找候选，再校验 class 前缀。

**② 「编辑器被清空」不是有效的提交判据**
编辑器为空时，`ValuePattern.Value` 返回的是**占位提示文本**（本例 13 字），
不是空串。若以 `Length == 0` 判提交，会把成功判成失败。
有效判据是：**发送的文本出现在会话正文中**。

**③ 这个应用接受 `keybd_event`**
对照测试：`SetFocus` 后发 `VK_A`，编辑器内容变为 `a`（1 字）。
而同期的抖音客户端，`SendInput` 键盘、Unicode、Ctrl+V、ESC 全部无效。
⇒ **「键盘是否可达」必须按应用分别记录，不能写成平台级结论。**

### 一个操作陷阱

`SetValue('')` 对清空有效，但读回仍是占位文本长度，
所以「清空是否成功」同样不能用 `Length == 0` 判断，要看正文是否消失。

### 本轮未完成

提交后约 30 分钟，应用界面被一个 Slack OAuth 的 Edge 错误页（`ERR_CONNECTION_CLOSED`）遮挡，
会话视图也被切到了 `dot` 页面，因此**尚未取回 GPT 的完整答复**。
会话仍在左侧「最近」列表中，需要先切回该会话才能继续读取。