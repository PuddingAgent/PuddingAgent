---
title: 2026-11-02 Computer Use 感知层调研 —— UIA 覆盖度实测与分层建议
author: 鲸鲸
date: 2026-11-02
last_reviewed: 2026-11-02
status: draft
description: "实测四类 Windows 应用的 UIA 元素树覆盖度，给出可落代码的『空洞判据』；结合 UFO² 的混合控制检测，提出 UIA 优先 + 局部截图兜底的三层感知架构。"
categories: [docs, report]
tags: [computer-use, uia, perception, omni-parser, research]
related_docs: [Docs/12_features/Computer-Use代码设计与分阶段施工方案-2026-10-20.md, Docs/14_reports/2026-11-02-Computer-Use-CU0技术探针.md]
related_files: []
slug: report-2026-11-02-computer-use-perception-research
draft: false
---

# 感知层调研：Agent 怎么「看见」桌面程序

## 问题

用户提出的核心问题：**能不能不靠整屏截图，直接拿到元素坐标和元素树**？
以及：能否对单个程序做状态识别与局部预处理，只取局部图片、只做局部操作？

这个问题决定 token 成本与准确率，是整个 Computer Use 的地基。

## 一、实测：UIA 对四类应用到底能读到什么

在本机（Windows 10.0.26300 / .NET 10 / FlaUI.UIA3 5.0.0）用同一段 BFS 遍历
（上限 600 节点 / 深度 8）实测，**只读、不激活、不输入**：

| 应用 | 窗口类名 | 节点数 | namedRatio | Pane 占比 | 判定 |
|---|---|---:|---:|---:|---|
| 文件资源管理器 | `CabinetWClass` | **166** | **0.91** | 5% | **完整可用** |
| 文件资源管理器（桌面） | `CabinetWClass` | 91 | 0.86 | 8% | 完整可用 |
| 记事本 | `Notepad` | 36 | 0.67 | 22% | 可用 |
| Pudding（本产品） | `WinUIDesktopWin32WindowClass` | 46 | 0.54 | 48% | 中等 |
| ChatGPT | `Chrome_WidgetWin_1` | **15** | 0.40 | **60%** | **空壳** |
| VS Code | `Chrome_WidgetWin_1` | **14** | 0.36 | **71%** | **空壳** |

耗时：资源管理器 567ms（166 节点），Chromium 类 14–178ms（节点极少）。

### 这张表说明了什么

1. **Win32 原生应用（资源管理器、记事本）UIA 树非常完整** —— 166 个节点、91% 有名字、
   有 `Edit`/`TreeItem`/`ListItem` 等语义控件。这类应用**完全不需要截图**就能精确定位并操作。
2. **Chromium / Electron 应用（ChatGPT、VS Code）几乎读不到内容** —— 只有 14~15 个节点，
   且 60~71% 是 `Pane`，只剩窗口最小化/最大化/关闭按钮。
   网页/编辑器正文**完全不在 UIA 树里**。
3. 这不是 FlaUI 的问题，是 Chromium 默认不暴露渲染树——需要 `--force-renderer-accessibility`
   之类的开关才完整；对已运行的第三方应用我们不能改它的启动参数。

### 由此得到一个可落代码的「空洞判据」

```
空洞度 = Pane占比 × (1 − namedRatio)

nodes < 30  且  空洞度 > 0.35   ⇒  该窗口内容未暴露，必须退回视觉
```

代入实测：ChatGPT `0.60 × 0.60 = 0.36` ✓ 命中；VS Code `0.71 × 0.64 = 0.45` ✓ 命中；
文件资源管理器 `0.05 × 0.09 ≈ 0.005` ✓ 不命中。三者区分干净。

这条判据的好处是**只看一次遍历的统计量**，不需要逐个控件试探，成本几乎为零。

## 二、业界成熟做法：UFO² 的混合控制检测

微软 UFO²（*The Desktop AgentOS*, arXiv 2504.14603）的做法与这个问题直接对应：

- **双路融合**：UIA 元数据 + OmniParser-v2 视觉 grounding
  （YOLO-v8 检测器 + 微调 Florence-2 0.23B 编码器），
  视觉侧输出控件类型、置信度、bounding box。
- **去重规则很具体**：视觉检测与任何 UIA 派生控件的 **IoU > 10% 即丢弃**——
  也就是**UIA 优先，视觉只补漏**。
- **剩余视觉检测转成 pseudo-UIA 对象**（`UIAWrapper` 抽象），
  从而让下游管线不必区分「这个控件是结构读到的还是看出来的」。
- **SoM（Set-of-Mark）标注**：把融合后的控件集编号再给模型，
  模型输出「点击第 7 号」而不是自己编坐标——绕开坐标幻觉。
- **PiP（画中画）**：用本机 RDP 循环会话开一个隔离虚拟桌面跑自动化，
  用户真实桌面不受干扰。**这一条直接解决了「抢焦点打断用户」的问题。**

## 三、对 PuddingAgent 的建议：三层感知 + 一层执行

```
L0 应用识别   进程 + 窗口类名（+ 可选签名/版本）
L1 结构感知   UIA 树（默认路径，实测 Win32 类足够）
L2 空洞检测   判据命中 → 只对**该窗口区域**截图，不截全屏
L3 视觉增强   局部裁剪 → 对比度/锐化/通道（image_reader 已具备）→ 模型理解
L4 动作执行   UIA Pattern 优先（Invoke/SetValue）；无 Pattern 才 SendInput 坐标
```

四条配套原则：

1. **默认走 L1**，不截图。截图是兜底，不是常态。
2. **L2 的触发是「空洞判据」而不是「UIA 报错」** —— 空壳窗口遍历不报错，只是没内容。
3. **L3 必须支持局部放大**：今天实测过 10px 汉字在 2048 宽的图里读不出，
   局部裁剪 + 增强是唯一可行路径（该能力已在 `image_reader` 落地）。
4. **L4 优先用控件模式**：`InvokePattern` 比坐标点击稳，且不依赖窗口位置；
   坐标点击只在无 Pattern（自绘控件）时使用。

## 四、对现有设计文档的补充建议

设计文档 §9.1 已写「从指定授权窗口根开始 BFS、有界读取」，方向一致。建议补三点：

1. **补「空洞判据」**：把上面那条启发式写进 `ReadAsync` 的返回值，让调用方知道
   「这棵树是完整还是空壳」，而不是自己去数节点。
2. **补伪 UIA 对象层**：视觉补检出的元素应以**同一套 id/坐标/动作接口**暴露，
   否则上层要写两套逻辑（UFO² 的 `UIAWrapper` 就是干这个）。
3. **补「局部截图预算」**：截图应按窗口或元素裁剪，并复用已有工件（同一画面多次询问不必重拍）。

## 五、未验证 / 待定

- 本报告的数据来自**本机单次样本**（各应用只有 1~2 个窗口），
  样本量小，判据阈值（30 节点 / 0.35 空洞度）需要用更多应用回归校准。
- 一个独立调研子代理正在并行做方案调研（含 MSAA/IA2/Win32 消息的取舍、更多开源实现），
  结论回来后应与之交叉验证，冲突处以实测为准。
- **PiP 隔离**（RDP 会话）未验证，涉及权限与部署形态，属于 CU7 之后的议题。

## 六、实测修正（探针 #5/#5b）：两个反直觉发现

第一版判据出来后，我用更大的深度/上限重测了一遍，**发现原判据会误伤**。两条修正如下。

### 反直觉 1：Chromium 不是「空壳」，是「深」

| 读取参数 | ChatGPT 节点数 | 是否触顶 |
|---|---:|---|
| maxDepth=8（第一版） | 15 | 否 |
| maxDepth=10 | **23** | **capped=True（深度触顶）** |
| maxDepth=12 | **47** | 否 |

同一窗口，深度每加一次节点就涨一截。⇒ **Chromium 的内容是存在的，只是嵌得很深**
（其 a11y 树对 Shadow DOM 会多插几层）。而 LM Studio / VS Code 在 depth 6~7 就稳定在 14 节点。

**因此「节点数少 ⇒ 空壳」是错的**，必须区分「读完了但少」与「压根没读完」。

### 反直觉 2：同名窗口可能在「未就绪」状态，树只有骨架

两个标题**完全相同**的「2026-10 - 文件资源管理器」，是两个不同进程：

```
pid=7744   nodes=8    interactable=4    childFails=0   ← 骨架
pid=82160  nodes=217  interactable=140  childFails=0   ← 完整
```

注意 `childFails=0`：**遍历没有报错**，所以这不是读取失败，是那个窗口当时确实只有 8 个节点
（很可能正在创建或正在关闭）。

这会让「nodes<30 ⇒ 空壳」把**正在初始化的正常窗口**判成空壳，白白退回视觉。

### 判据修正（替代第一版）

```
① 先看 capped：深度或节点触顶 ⇒ 树没读完，先加深再判断（不是空壳）
② 再看就绪：同 pid 隔 200~400ms 重读，节点数明显变化 ⇒ 窗口未稳定，等它稳定
③ 都不成立时才用：nodes < 30 且 Pane占比 × (1 − namedRatio) > 0.35 ⇒ 空壳
```

### 与独立调研的交叉验证

子代理调研称「Chromium 惰性无障碍：首个请求可能失败、后续才成功」。
本机实测：同一 ChatGPT 窗口**连读 5 次，节点数稳定不变**（depth=12 时恒为 47），**没有惰性增长**。

⇒ 惰性增长在本机未复现（该窗口当时已 warm）。这条不能当作稳定判据，
只能作为「首次读取失败时重试一次」的保险措施。

### 另一组对照数据（同一轮实测）

| 窗口 | 节点 | 可交互元素 | 耗时 |
|---|---:|---:|---:|
| 文件资源管理器（活跃） | 217 | **140** | 249ms |
| 文件资源管理器（桌面） | 127 | 74 | 168ms |
| 记事本 Ndd | 116 | 71 | 39ms |
| 原生 Notepad | 53 | 24 | 56ms |
| LM Studio（Chromium） | 14 | 3 | 23ms |

可见 Win32 原生应用的**可交互元素密度**（140/217）远高于 Chromium 骨架（3/14），
这也是一个比「节点总数」更稳的判据候选：**可交互元素数 < 10 ⇒ 该层不可信**。

## 七、目标验证：ChatGPT 客户端（首个目标应用）

用户指定第一个目标是**操作 ChatGPT Windows 客户端**。为此单独实测（只读，不发送任何内容）：

```
TARGET ChatGPT [Chrome_WidgetWin_1] pid=81092
NODES=738  MAXDEPTH=22  CAPPED=False  READFAILS=0  MS=374
TYPES: Button=218, Text=167, Group=102, DataItem=95, ListItem=62, Image=41, List=11, Pane=9
VERDICT edit=True button=True
```

### 推翻上一节的「空壳/深」判断

之前读到 15 / 23 / 47 个节点，**全部是被 MaxDepth 截断的结果**。放到 depth 30 后是
**738 个节点、最大深度 22、零读取失败**。所以：

> Chromium 应用不是「空壳」，也不是「内容读不全」，而是**树很深**（本例 22 层）。
> 判据里的 `capped` 检查因此不是锦上添花，而是**必要条件**。

### 输入框与对话区都已定位

```
FLAUI_NODES=738  EDITLIKE=2

#1  class=(空)          readonly=True   rect=(2560,0,2048,1104)  patterns=[Value,Text,LegacyIA,Scroll]
    name=分析上下文压缩空间
#2  class=ProseMirror   readonly=False  rect=(3396,1000,713,44)  patterns=[Value,Text,LegacyIA]
    name=和 Codex 一起工作
```

- `#2` 是**输入框**：ChatGPT 用 **ProseMirror** 富文本编辑器，`readonly=False`，
  带 `ValuePattern` ⇒ 可以直接 `SetValue`，**不需要模拟键盘**。
- `#1` 是**对话记录区**：2048×1104、只读、带 `Text` + `Scroll` pattern ⇒
  **读 ChatGPT 的回复不需要截图，也就不消耗视觉 token**。
- `#6` 探针里 218 个 Button，发送键在其中（按位置与 Name 可定位）。

### 对首个目标的结论

**纯 UIA 路线成立**，不需要视觉兜底：

| 能力 | 通道 | 是否已验证 |
|---|---|---|
| 读对话内容 | `Text` pattern（#1） | ✅ 能读到完整句子 |
| 写入提问 | `ValuePattern.SetValue`（#2） | ✅ 非只读、pattern 存在，**未实际写入** |
| 触发发送 | `Button` + `InvokePattern` | ⏳ 未逐个确认哪一个是发送键 |
| 观察结果 | 轮询 #1 的 Text 变化 | ⏳ 未验证增量识别 |

### 仍未验证 / 风险

- **实际写入与发送未做**：会真的往用户 ChatGPT 里发消息、消耗额度，必须征得同意后再验。
- **窗口位于副屏**（`x=3396`，主屏宽 2560）：坐标、截图区域、DPI 都要按虚拟屏幕处理，
  这正是探针 #2 里「DPI unaware 危险」的具体场景。
- ProseMirror 的 `SetValue` 是否等价于真实键入（会不会触发 React 状态更新）**未验证**——
  这类富文本编辑器常见「设了 DOM 值但框架状态没变」的坑，若 `SetValue` 无效需回退 `SendInput`。

## 八、输入路径实测：SetValue 与 SendInput 都没有画面效果 —— 根因是 UIA 坐标错位

用户要求验证「填入聊天信息」，并明确用截图辅助判断「是否不需要视觉就能完成输入」。

### 三次尝试与结果

**① `ValuePattern.SetValue`（探针 #8）**

```
ORIGINAL_VALUE_LEN=13
SETVALUE ok=True  ms=48  READBACK="pudding-uia-probe-1540"  MATCH=True
BEFORE/AFTER 截图 hash 相同   CHANGED=False
CLEANED len=13                 ← 清空也回到原值
```

属性被写了、读回也匹配，**但画面从头到尾没变**。ProseMirror 的 `ValuePattern` 是个影子：
改它不影响真实编辑器状态。

**② `SendInput` 真实键盘事件（探针 #9）**

```
ACTIVATE set=True  foregroundMatch=True
CLICK at=(3752,1022)
TYPED chars=7  截图 hash 不变
```

**③ 排查仪器（探针 #10/#11）—— 这才是根因**

- `BitBlt` 抓到的画面是**遮挡在上面的文件管理器**（截出一行 `Source/...HandoffStoreTests.cs`）。
  设计 §7.1 早就写了「ScreenPixels 采集须检查遮挡归属」，是我漏了。
- 改用 `PrintWindow`（抓窗口自身渲染，不受遮挡）后画面正确了，
  但发现：**UIA 报的输入框 `rect=(3396,1000)`，在真实画面里那一块是「已编辑 29 个文件」面板**。
- 窗口里**根本看不到输入框**（Codex 界面此刻显示的是对话 + 文件变更列表，输入区不在可视范围）。

### 结论

> **读得准，写不了 —— 至少不能靠 UIA 的 `BoundingRectangle`。**

| 能力 | 结论 | 证据 |
|---|---|---|
| 读对话内容 | ✅ 纯 UIA 可行 | 738 节点，Text 里能读到完整句子 |
| 定位写目标 | ❌ 坐标不可信 | 报的输入框位置实际是文件面板 |
| 写入（Value） | ❌ 影子属性 | 报成功、读回匹配、画面不变 |
| 写入（键盘） | ❌ 未达目标 | 按错位坐标点击后，按键没有进入编辑器 |

### 对架构的直接影响

1. **`BoundingRectangle` 不能作为点击依据**，应改用 `ClickablePoint`，
   且必须配一次「视觉复核」（截图确认该点确实是目标控件）。
2. 这正好印证了子代理调研里那条「WindowsAgentArena 的最佳配置 = SoM + a11y 树 + 像素检测器**三者合成**，
   UIA 树用来生成标记而不直接喂模型」——单靠结构树落点是不可靠的。
3. **先前「纯 UIA 可行，不需要视觉」的结论要收回**：那条只对**读**成立；
   **写**需要视觉参与定位，尽管不需要视觉来"理解界面"。

### 副作用告知

探针 #9/#11 按错位坐标点击并发送了 7 个字符（`测试abc123` 的子集）。
若焦点实际落在某个可输入控件上，可能残留少量文本。已尝试 Ctrl+A + Delete 清理，
但因目标本就不是输入框，清理未必作用在原处。**这是我在未确认坐标正确前就发送输入的代价。**

### 下一步（修正后的路线）

1. 先解决「元素 → 屏幕坐标」的正确映射：验证 `ClickablePoint` 是否与 `BoundingRectangle` 一致，
   若也不一致则说明该 a11y 树的坐标系与屏幕不同源（Chromium 的内容坐标 vs 屏幕坐标）。
2. 输入前必须有一次**视觉确认**：截取目标元素区域，确认是输入框再发按键。
3. 「读」可以完全不用视觉；「写」至少需要一次视觉复核 —— 这是本次实测给出的边界。

## 九、再修正：根因不是坐标错位，是「窗口没到前台」

上一节我判定「UIA 的 BoundingRectangle 与实际渲染错位」。**这个判断是错的**，本节予以更正。

### 决定性读数：输入框里从来没进过东西

只读探针（不点击、不输入）直接读 UIA 的 Value：

```
NAME="和 Codex 一起工作"
VALUE="和 Codex 一起工作\n"     ← 13 字符，就是原始占位符
HAS_CN_TEST=False
HAS_UIA_PROBE=False
```

无论探针 #8 的 `SetValue` 还是 #9/#11 的 `SendInput`，**都没有在这块输入框里留下任何字符**。

### 坐标其实是自洽的

```
#2 Edit ro=False rect=(3396,1000,713,44)  cp=(3752,1001)  cpInsideRect=True
```

`ClickablePoint` 落在 `BoundingRectangle` 内 —— 两者一致。所以「坐标错位」不成立。

### 真正的根因：两路截图的分歧

| 采集方式 | 抓到了什么 | 说明 |
|---|---|---|
| `BitBlt`（屏幕合成） | 压制在上面的文件管理器 | 窗口**被遮挡** |
| `PrintWindow`（窗口自身渲染） | 正常 Codex 界面 | 窗口内容没问题 |
| `SetForegroundWindow` | 返回 **True**、`GetForegroundWindow` 也等于它 | **但窗口并没有真正到前台** |

结合起来只有一个解释：

> **ChatGPT 窗口被压在别的窗口后面。`SetForegroundWindow` 返回成功 ≠ 窗口真的在前台
> （Windows 的前台锁定机制允许它"报告成功"而不真正置顶）。**
> 于是 `SetCursorPos + mouse_event` 的物理点击落在了**遮挡物**上，后续按键自然也没进 ChatGPT。

这也解释了为什么 #9 的 BitBlt 会截出一行 `Source/...HandoffStoreTests.cs`——
那是压在 ChatGPT 上面的文件管理器，不是 ChatGPT 的内容。

### 架构结论修正

| 原结论（第八节） | 修正后 |
|---|---|
| ~~UIA 坐标不可信，落点须视觉复核~~ | **UIA 坐标可信**（cp 与 rect 自洽） |
| ~~写路径需要视觉定位~~ | 写路径**可以用** UIA 坐标 + `SendInput`，但**前提是窗口真的在前台** |
| `ValuePattern.SetValue` 无效 | **仍然成立**（探针 #8 已证：属性变、React 不认） |

因此需要一条新的硬前提，而不是视觉兜底：

> **发送物理输入前，必须验证目标窗口真的在最前**，且验证方式不能依赖 `SetForegroundWindow`
> 的返回值 —— 要用 `GetWindowRect` + 遮挡判定，或先强制置顶再复核前台。

这条正好落在设计 §7.1「执行前检查……前台窗口、目标点命中测试」的要求上：
**命中测试（是否真能点到）比前台标志位更可信。**

### 仍未验证

- 在**确保窗口真在前台**的前提下，`SendInput` 能否成功写入 —— 需要强制置顶（会明显干扰用户），
  因此没有在心跳轮次执行。
- `ValuePattern.SetValue` 与真实键盘事件在 ProseMirror 上的差异已确认：前者是影子，后者待测。

## 十、决定性实验：前提全满足，`SendInput` 仍进不去

上一节把根因归给「窗口没到前台」。本节用强制置顶把这条前提坐实，再测一次。

```
BOX rect=(3396,1000,713,44) target=(3752,1022)
TOPMOST set=True    foregroundMatch=True    hitTest=True  (hitHwnd=0xCF0FCC)
VALUE_BEFORE_LEN=13
VALUE_AFTER_LEN=13
VALUE_AFTER="和 Codex 一起工作\n"
LANDED=False
RESULT: FAIL still_not_landed
```

三项前置条件**全部通过**：

| 检查 | 结果 | 含义 |
|---|---|---|
| `SetWindowPos(HWND_TOPMOST)` | True | 窗口已强制置顶 |
| `GetForegroundWindow()==hwnd` | True | 系统报告它在最前 |
| `WindowFromPoint(目标点)` 根窗口 == 目标 | True | **这一点确实属于它**（命中测试，比前台标志位可信） |

**但输入没有落进去。**

### 这排除了什么

| 曾被怀疑的原因 | 状态 |
|---|---|
| 窗口被遮挡 | ❌ 排除（TOPMOST + 命中测试通过） |
| 坐标错位 | ❌ 排除（cp 在 rect 内，且命中测试命中该窗口） |
| 前台标志位骗人 | ❌ 排除（用了命中测试而非仅看返回值） |
| `ValuePattern.SetValue` 无效 | ✅ 仍然成立（影子属性） |
| **`SendInput` 的 Unicode 键盘事件进不了 ProseMirror** | ✅ **本节的结论** |

### 为什么

这是 Chromium/Electron 侧的输入注入限制，与我们的调用方式无关：

- `KEYEVENTF_UNICODE` 注入的字符**不经过正常输入法管线**，也不必然触发编辑器监听的
  `beforeinput` / `composition` 事件链；对 ProseMirror 这类依赖编辑事件的富文本控件，
  结果就是「按键被系统接受了、编辑器没反应」。
- 同一现象可以解释为什么 `SetValue` 也无效：它改的是 a11y 层的 Value 属性，
  而真实状态由 React 的编辑器模型持有，二者本就不同源。

### 下一候选：剪贴板粘贴

业界对 Chromium 富文本的常见 workaround 是**用粘贴而不是打字**（`Ctrl+V` 触发 paste 事件，
ProseMirror 会按正常路径处理）。代价是需要临时改写用户剪贴板，必须**保存原内容并在结束后恢复**。

### 对架构的影响

1. **输入通道要分层，且默认顺序不能是"先试打字"**：
   `SetValue`（快但可能无效）→ **剪贴板粘贴**（对 Electron 有效）→ 逐键注入（最后手段）。
2. 每次写入后**必须用 UIA Value 或截图复核**，"SendInput 返回值正常"完全不能证明落进去了。
3. 这条结论应写进 CU3 的动作执行器设计：**写动作必须带后置验证**，否则会静默失败。

## 十一、焦点探针：四项条件全满足，输入仍然进不去 —— 结论是「幽灵元素」

三种写入方式（`SetValue` / `SendInput` Unicode / `Ctrl+V` 粘贴）全部失败后，
最后检查焦点。结果出乎意料：

```
[before_click]    BOX_HAS_FOCUS=True   FOCUSED=Edit class="ProseMirror ProseMirror-…"
[after_click]     BOX_HAS_FOCUS=True   FOCUSED=Edit class="ProseMirror ProseMirror-…"
[after_uia_focus] BOX_HAS_FOCUS=True   FOCUSED=Edit class="ProseMirror ProseMirror-…"
```

**焦点从头到尾都在这个元素上，从未离开。**

### 四项条件同时成立却全部失败

| 条件 | 状态 |
|---|---|
| 窗口真在最前（TOPMOST） | ✅ |
| 命中测试（`WindowFromPoint` 指向该窗口） | ✅ |
| 键盘焦点在该元素上 | ✅ |
| 点击事件已发出（`SendInput` 鼠标） | ✅ |
| 三种写入方式 | ❌ 全部无效 |

按常理，这四项里少一项才会失败；**四项齐全还失败，说明这个元素本身就是"幽灵"**。

### 三条互相印证的证据

1. **画面里它不存在**：`PrintWindow` 显示该位置是「已编辑 29 个文件」面板，不是输入框。
2. **值不像输入框内容**：`VALUE = "和 Codex 一起工作\n"` —— 更像**历史会话标题**或占位符。
3. **焦点"一直在"本身反常**：正常界面里点击别处焦点会移走；这里三次采样一动不动，
   说明这是 a11y 层的**陈旧/未同步状态**，而不是真实焦点。

综合判断：当前这个 Codex 窗口显示的是**历史对话视图**，输入区未激活（或在 DOM 里被隐藏），
但 Chromium 的无障碍树仍保留着那个 ProseMirror 节点及其历史布局坐标。

### 对本轮所有写入结论的影响

**必须降级为"未验证"**，而不是"Chromium 拒绝注入"：

- 之前说「`SendInput` 进不了 ProseMirror」——在幽灵元素上测试，**不能推出这个结论**。
- 唯一仍然成立的是：`ValuePattern.SetValue` 改的是 a11y 属性、不触发编辑器状态 ——
  这一条有独立证据（值变了、截面没变），但同样需要在实际可输入的界面上复核。

### 下一步（必须先做这一步，否则后面都是空谈）

1. **在一个确实可输入的界面状态下复测**：新开一个会话 / 确认输入框在画面上可见，
   然后再跑一次 `SendInput` + UIA Value 判定。
2. 把「**元素可见性校验**」写进流程：操作前不仅要看 `IsOffscreen`，
   还要用**截图确认该元素区域确实是它**（这正是第八节里"视觉复核"的真正用途 ——
   不是为了找坐标，而是为了确认元素不是幽灵）。
3. 这也回答了一个更根本的问题：**UIA 树的"存在"不等于"可操作"**。
   设计 §7.1 里那条「执行前检查……目标点命中测试」还不够，应再加一条
   「目标区域的视觉归属校验」。

### 本轮 16 个探针带来的净结论

| 结论 | 证据强度 |
|---|---|
| FlaUI.UIA3 5.0.0 在 .NET 10 可用，枚举/读取正常 | 强（探针 #1/#6） |
| Win32 原生应用 UIA 树极完整（217 节点/140 可交互） | 强（探针 #5b） |
| 深度截断会让人误判为"空壳"，必须查 capped | 强（探针 #5/#6 对比） |
| 默认进程 DPI unaware，缩放屏上会错位 | 强（探针 #2） |
| `KILL_ON_JOB_CLOSE` 5ms 杀成员 | 强（探针 #3） |
| `SetValue` 对 ProseMirror 不触发编辑器状态 | 中（需在可见界面复核） |
| `SendInput`/粘贴是否可用 | **未验证**（测到了幽灵元素上） |

## 十二、Codex 裁决与决定性诊断：焦点在窗口，不在编辑器

四种注入方式（SetValue / SendInput / Ctrl+V / TextPattern 读回）全部失败后，按用户指示请 Codex 做技术裁决。
它给出了一条我此前漏掉的诊断路径，直接定位了根因。

### Codex 的关键判断

> `Invoke` 通过 Chromium 的 AX action 直接执行，所以 UIA 点按钮能成；
> 但键盘只投向前台线程。「AX 焦点 ≠ Win32 前台键盘焦点」，`GetForegroundWindow()==root`
> **不能证明输入队列归属**，而 `HasKeyboardFocus` 也只是 AX 语义。

### 实测证实

```
GetForegroundWindow = 0xCF0FCC   target = 0xCF0FCC   match=True
target threadId=12480
  hwndActive = 0xCF0FCC
  hwndFocus  = 0xCF0FCC          ← 焦点在窗口上
  hwndCaret  = 0x0               ← **没有插入符**
TEXT = "随心输入" (占位符，5 字符)
```

甚至用 `AttachThreadInput` + `SetFocus` 强制之后，`hwndFocus` 仍是窗口级、`hwndCaret` 仍是 0。

**结论：点击让窗口拿到了键盘焦点，但从未把焦点交给 DOM 编辑器。**
按键于是投给了窗口壳，而窗口没有把它路由进 ProseMirror —— 所以四种注入方式全部石沉大海。
`hwndCaret == 0` 是最硬的证据：真聚焦在 contenteditable 上时，Chromium 会给出插入符。

### 这也解释了「幽灵元素」的成因

那个 `Edit` 元素的 `BoundingRectangle` 与 `ClickablePoint` 自洽，但**点击它并不会让 DOM 聚焦**。
不是坐标错，是**这个 a11y 元素与真实可编辑区域不是同一层对象**。

### Codex 给出的后继选项（按成功率）

1. **TSF 文本服务**：`ITfTextInputProcessorEx` → `ITfContext::RequestEditSession` → `ITfRange::SetText`。
   这是最强的非按键方案，但需要 C++ COM/TIP 注册，不是普通跨进程 C# 调用。
2. 定位真实 `hwndFocus` 或 `Chrome_RenderWidgetHostHWND`，`PostMessage(WM_CHAR)`。
   **成功率低**，且 `WM_SETTEXT` 对 DOM 无效。
3. 若以上都不行：**Windows 上没有可靠且受支持的桌面注入接口**。

### 我的判断（需用户裁决）

在**不修改目标应用启动参数**的前提下（不能加 `--force-renderer-accessibility` 或调试端口），
桌面 UIA 路线对 Electron 富文本编辑器**不可靠**。

三条可选路线：

| 路线 | 可行性 | 代价 |
|---|---|---|
| 桌面 UIA + TSF 文本服务 | 技术可行，但需 C++ COM/TIP 组件 | 高（跨语言、需注册、与现有 C# 架构割裂） |
| **网页版 + CDP/Playwright** | **已具备**（现有 browser_* 工具链可直接驱动） | 低；但不是"桌面客户端" |
| OpenAI API 直连 | 最可靠 | 完全不是 Computer Use |

**建议：第一个目标（操作 ChatGPT）改用网页版走 CDP。**
桌面客户端这条线保留给 Win32 原生应用（实测 UIA 完整：217 节点 / 140 个可交互元素），
把"Electron 富文本"明确列为桌面 UIA 的能力盲区，写进 CU1 的能力矩阵。

## 十三、UIA 坐标与真实渲染不一致的直接证据

把窗口强制置顶后（`FOREGROUND_MATCH=True`），`BitBlt` 截屏终于正确了（此前抓到的都是遮挡物）。
对照 UIA 报的输入框位置，得到一个**直接反证**：

```
UIA:  Edit class=ProseMirror  rect=(3396,1000,713,44)
截图: 同一区域 (相对窗口 (840,996) 起 1200x104)  → **纯色空白，文件仅 830 字节**
```

也就是说：**UIA 报告的那个「输入框」在实际画面上是空的。**

这不再是"我猜它是幽灵"，而是截图与 a11y 坐标的直接对撞 —— 两者必有一个错，而截图画面上
连输入框的边框都没有。

### 另一个需要澄清的事实

本机只有一个标题含 `ChatGPT` 的 Chromium 窗口（pid=81092），但它的界面是 **Codex**
（左侧会话列表、顶部 Codex 标题、欢迎语带项目名 PuddingAgent）。
也就是说：**这个桌面应用的主体是 Codex，不是 ChatGPT 对话界面**。

因此「操作 ChatGPT 客户端」这个目标，首先要明确操作的是哪一个界面：
若是 Codex 的任务输入区，需先让该区域出现在可视范围内（当前新任务页右侧主区是空白）；
若是 ChatGPT 对话，需要确认它在这个应用中的入口。

**在界面状态明确之前，继续调试输入注入没有意义** —— 目标控件在画面上不存在时，
任何注入方式都会"失败"，而这个失败不携带信息。

## 十四、像素扫描定位法，以及它在本次问题上的结论

UIA 坐标与截图对不上之后，我改用**纯像素扫描**定位界面元素，不再依赖任何 a11y 坐标。

### 方法（可复用）

```csharp
// 1. 以窗口左上角采样为背景色（本例 R241 G243 B249，不是纯白）
// 2. 逐行统计「与背景色差异 > 12」的像素数
// 3. 连续非背景行 = 一条横向内容带；输出 y 范围与 x 范围
```

实际输出：

```
BG = R241 G243 B249
y=  15..30    maxNonWhite= 117  x=18..380      ← 顶部标签栏
y=  55..1103  maxNonWhite=1822  atY=1100 x=64..2047
```

`atY=1100` 那一行横跨 x=64..2047 —— 说明**窗口最底部确实有一条贯穿整宽的内容带**（输入框所在）。
而 UIA 报的输入框是 `y=1000..1044`，**与画面相差约 60px**。

### 但换成像素坐标仍然注入失败

把点击位置从 UIA 的 `(3752, 1022)` 改到像素扫描推出的 `(3752, 1059)`：

```
UIA_RECT=(3396,1000,713,44)  用像素位置 target=(3752,1059)
TOPMOST=True  foregroundMatch=True  hitTest=True
VALUE_BEFORE_LEN=5  VALUE_AFTER_LEN=5  VALUE_AFTER="随心输入"
LANDED=False
```

**结论：坐标偏差不是根因。** 换到真实像素位置后，`SendInput` 依然进不去编辑器。

### 本次问题的最终判定

在本机当前状态下，以下组合**已被穷尽**：

| 手段 | 结果 |
|---|---|
| `ValuePattern.SetValue` | 影子属性，界面无变化 |
| `SendInput` + `KEYEVENTF_UNICODE`（ASCII 与中文） | 无反应 |
| 剪贴板 + `Ctrl+V` | 无反应 |
| UIA `SetFocus` / `AttachThreadInput` + `SetFocus` | 焦点只到窗口层，`hwndCaret=0` |
| 点击 UIA 坐标 / 点击像素坐标 | 均无反应 |

而同一台机器上 `UIA InvokePattern` 点按钮**是成功的**（「新聊天」切换生效）——
说明 UIA 动作通道本身没问题，**只有文本注入这一环失效**。

这与 Codex 的裁决一致：Chromium 的 AX 焦点与 Win32 键盘焦点不是一回事，
`Invoke` 走 AX action 能成，键盘却投不到 DOM 编辑层。

### 剩下只有两条路

1. **改变目标应用的启动参数**（`--force-renderer-accessibility` 等）——需要重启该应用并由用户授权。
2. **写 TSF 文本服务**（C++ COM/TIP，`ITfRange::SetText`）——最终手段，可用但工程代价大。

两条都不能在心跳轮次里擅自执行（前者要动用户的运行中应用，后者要引入 C++ 组件）。

## 十五、最简路线的第一个验证点：视觉定位 + Win32 点击 = 有效

按用户指定的路线（视觉理解 + Windows API 取窗口信息 + 模拟鼠标键盘），放弃 UIA 坐标，改用**看图定坐标**。

### 验证过程

1. 截全窗图（2048x1104）→ 缩放到 1600x862 供视觉判读。
2. 目视定位左侧「新聊天」按钮：截图上约 (82,107) → 换算原图 (105,137) → 绝对坐标 `(2560+105, 0+137) = (2665,137)`。
3. `EnumWindows` 按类名 `Chrome_WidgetWin_1` + 标题含 `ChatGPT` 找到窗口，`GetWindowRect` 取几何 `(2560,0)-(4608,1104)`。
4. `SetWindowPos(HWND_TOPMOST)` + `SetForegroundWindow`，`WindowFromPoint` 命中测试通过。
5. `SetCursorPos` + `mouse_event(LEFTDOWN/LEFTUP)` 点击。

### 结果：界面真的响应了

点击后画面从「你想让我们在 PuddingAgent 中构建什么？」切换为 **「创建你的 dot」** 页面，
左侧「你的 Dot」条目高亮选中。

**这证明：视觉定位 + Win32 点击这条链路对 Electron 应用是有效的。**

（顺带说明：我点的位置偏了一行，实际选中了「你的 Dot」而不是「新聊天」——
说明视觉换算还需要校正缩放比例，但不影响结论本身。）

### 与 UIA 路线的对比

| 环节 | UIA 路线 | 视觉 + Win32 路线 |
|---|---|---|
| 读取界面 | 738 节点，能读到文本 | 截图判读，信息更贴近人眼所见 |
| 点击按钮 | 可用（InvokePattern） | **可用（本次验证）** |
| 定位坐标 | **与实际渲染不符**（y 差约 60px） | 依赖换算精度，但可迭代校正 |
| 文本注入 | 四种方式全部失效 | 尚未验证（待输入框出现后测试） |

关键差异：**视觉路线拿到的是"画面上真实存在的东西"**，因此不会出现"UIA 说有、画面没有"的空转。

### 下一步（待继续）

1. 校正缩放换算，精确点中「新聊天」，让任务输入框出现。
2. 视觉定位输入框 → 点击 → `SendInput` 输入文本 → 截图验证。
3. 若文本仍进不去，再考虑"点击后先确认输入框获得焦点"的中间步骤。

## 十六、SoM（Set-of-Mark）最小原型：把"附坐标"这件事量化了

用户提出：软件先分析窗口、给 UI 元素编号并返回坐标系（全局/局部/相对 + 元素 id），
让模型只需"点 #N"，是否更省力？

### 先验证一个前提：UIA 报的元素有多少是真的

用 UIA 枚举可交互元素（Button/MenuItem/Edit/TreeItem/ListItem/Hyperlink/TabItem…），
再对每个元素的矩形区域做**像素验证**（与背景色的差异占比 < 2% 判为空白）：

```
CANDIDATES = 60
PIXEL_SUMMARY  ok=39  empty=21
```

**35% 的元素在画面上根本不存在。**

这一条数字解释了此前十几轮为什么反复失败：模型从 UIA 列表里挑一个"看起来对"的元素，
有三分之一概率挑到幽灵；挑中幽灵时，点击和输入都会"无声失败"——不报错，只是没反应。

### 元素表的形状（本原型输出）

```
ID   TYPE     NAME              GLOBAL(全局)          LOCAL(窗口内)   PIXEL  RATIO
#51  ListItem 修复问题          (2620,1021,262,31)   ( 60,1021)     OK     10%
#54  ListItem 修复编辑界面配置… (2620,1114,262,32)   ( 60,1114)     EMPTY   0%
```

三套坐标 + 一个真实性标记，一次调用即可给模型。

### 设计（建议落地形态）

```
① 采集    UIA 树（结构/名称/可做的动作） + 截图像素（真实性）
② 融合    每个元素做像素验证；EMPTY 的一律不进入候选集（或标为不可点）
③ 编号    对存活元素分配稳定 id
④ 三套坐标 global（屏幕绝对像素，副屏可为负）
           local （相对窗口左上角）
           clickable（已做 WindowFromPoint 命中的可点中心）
⑤ 输出    标注图（编号叠加在截图上） + 元素表（id → 名称/动作/三套坐标）
```

模型只需要回答「点 #7」；坐标查表得来，**模型不参与任何坐标计算**。

### 为什么这确实更省力

| | 现在（纯视觉） | SoM 之后 |
|---|---|---|
| 模型要做的事 | 目测像素位置 → 换算两次 → 输出坐标 | 只说一个编号 |
| 出错点 | 缩放换算、边界判断、元素认错 | 只剩"选错编号" |
| 坐标系 | 模型自己维护 | 程序给出，且已验证真实性 |
| token | 整图 + 推理算坐标 | 整图略增（编号叠加），输出只需几个 token |

### 顺带的收获：EMPTY 本身就是信号

`EMPTY` 不是噪声，它是**能力边界的探针**：
某个元素的 EMPTY 比例持续偏高，说明该应用的可访问性实现有问题，
这正是"该退回视觉"的判据 —— 比任何静态阈值都可靠。

### 下一步

1. 把编号**画到图上**（SkiaSharp 叠加，主工程已有该依赖），形成真正的 SoM 图。
2. 把 EMPTY 元素从候选集中剔除后，再验证一次"点 #N"是否一次命中。
3. 稳定后把这套逻辑固化成 `PuddingComputer.Automation` 的一个组件（它是纯规则，可单测）。
