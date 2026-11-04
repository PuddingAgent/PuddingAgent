---
title: Computer Use 观察与输入运行手册
author: Codex
date: 2026-11-03
last_reviewed: 2026-11-03
status: active
description: Windows GUI 的截图候选、物理坐标、原生焦点和输入回执，以及主模型与候选检测器的职责。
categories: [docs, runbooks]
tags: [computer-use, windows, vision, dpi]
related_docs: [Docs/13_runbooks/Desktop-Guardian.md]
related_files: [Source/PuddingHost/Tools/ComputerObserveTool.cs, Source/PuddingRuntime/Tools/BuiltIns/Computer/ComputerInputTool.cs, Source/PuddingComputer.Windows/ComputerDesktopSession.cs, Source/PuddingComputer.Windows/WindowsDesktopInput.cs, Source/PuddingComputer.Windows/IComputerElementDetector.cs]
slug: runbooks-computer-use
draft: false
---

# Computer Use 观察与输入运行手册

生产入口是 `computer_observe` 与 `computer_input`。执行链为：选择窗口 → 获取前台/原生焦点 → 观察原图与候选图 → 点击 → 新观察 → 输入 → 新观察核对结果。低层回执证明 OS 派发情况；主模型根据后续画面判断任务是否完成。

## 最小操作流程

工具 schema 使用 snake_case（如 `observation_id`、`target_id`、`image_x`、`input_mode`），返回值使用 camelCase。下列示例的 camelCase 参数由现有工具反序列化层接受；生产调用优先按 schema 使用 snake_case。参数说明用 `ToolParam` 声明，普通 `Description` 不会进入工具 schema。

1. `computer_input {"action":"windows"}` 枚举顶层窗口，选定明确的十进制 `handle`。同名多个窗口不能猜；`windowTitle` 只在唯一匹配时接受。
2. `computer_input {"action":"focus","handle":123}` 激活窗口。检查 `after.foregroundRoot`、`after.focusRoot` 与 `hasNativeFocus`。失败先诊断窗口状态，不继续发键。
3. `computer_observe {"handle":123}` 返回原图、编号图、`observationId`、候选与图像几何。默认最长边 1600 像素，最多 60 个候选。`images.attached=false` 表示当前模型路由未收到像素；必须用支持的视觉路由或 `image_reader`，不能仅凭元数据宣称看到了界面。
4. 选择本图候选：`computer_input {"action":"click","observationId":"...","targetId":7}`。检测框不准确时用 `imageX/imageY` 替代 `targetId`，坐标必须来自这张输出图。观察 ID 已携带目标窗口，不再混传 `handle`、屏幕 `x/y`。
5. 再次观察。搜索框展开、光标或选中态可作为焦点线索；没有明显光标并不能独立证明失败。需要时输入短测试文本，再核对文本本身。
6. `computer_input {"action":"type","handle":123,"text":"测试文本"}` 默认使用 Unicode 输入；显式 `inputMode:"paste"` 使用剪贴板写入、回读后 Ctrl+V。快捷键用 `action:"key", keys:"Ctrl+A"`。工具不会自动按 Enter。
7. 新截图确认文本/页面/按钮状态变化。`dispatched`、`SendInput` 返回数量、`Success=true` 均不是应用效果验证。

放大局部使用 `computer_observe` 的 `clientX/clientY/width/height`，单位是物理客户区像素，每次裁图签发独立几何和 ID。滚动或布局变化后重新观察；当前输入接口不提供滚轮/拖拽，不用虚构参数代替。

## 坐标与失效规则

图内点到屏幕点按该图的元数据转换：

```text
screenX = sourceRect.x + imageX * sourceRect.width / imageWidth
screenY = sourceRect.y + imageY * sourceRect.height / imageHeight
```

`sourceRect`、窗口与显示器范围都在同步线程的 Per-Monitor V2 上下文获取。主屏左上角是屏幕坐标原点，副屏可能有负坐标。禁止把图内坐标再加一次窗口原点，或按一个全局 DPI 比例缩放双屏坐标。库加载不改变宿主进程的 DPI 设置，线程作用域结束恢复原状态，作用域内不跨 `await`。

观察最多缓存 16 份，45 秒有效，绑定 workspace/session/agent、窗口 PID/进程启动时间、窗口和客户区范围、DPI 与显示器布局。点击前比较目标周围 24×24 像素区域，再检查实际落点根窗口与光标读回。观察 ID 单次消费，输入工具产生副作用后使该窗口旧观察失效。

局部像素比较是过期检测启发式，不能证明控件身份、页面整体未变或语义仍相同。动画、悬停和光标闪烁可能触发拒绝；应重新观察，不静默忽略检查。同一 HWND 在同进程内被系统复用也没有完整生命周期保证。跨进程并发操作桌面与人与 Agent 同时输入仍存在竞态，应串行操作。

分析窗口的中心命中不参与评分。输入前检查的是**本次实际落点**；即使另一顶层窗口属于同一进程，也不因此授权向它发送输入。遇到覆盖层先重新观察和明确选择窗口，不把同进程关系当作键盘焦点或输入归属证明。

## Chromium 的焦点链

Windows 前台窗口与 GUI 线程焦点 → Chromium Aura 窗口事件分发/输入法 → RenderWidgetHostView → renderer/Blink 的当前编辑节点。`GetGUIThreadInfo` 只能验证前半段；Chromium 的编辑焦点不是一个独立 Win32 文本框，原生 `hwndCaret=0` 可以正常输入。

`Chrome_RenderWidgetHostHWND` 在现代 Chromium 中包含历史兼容窗口的职责，不应据其名字直接 `SetFocus` 或把发给它的 `WM_CHAR` 当作真实键盘输入。当前工具不伪造 `WM_SETFOCUS/WM_ACTIVATE`，不自动改 TOPMOST，也不使用窗口消息点击作为等价成功的降级。

`focus` 的 `repairNativeFocus:true` 只在目标已经前台且原生焦点缺失时，临时 AttachThreadInput、对顶层窗口 SetFocus、finally 分离。它不能选择 DOM 输入框。先真实鼠标路径点击编辑区域，再用文本变化确认。

参考：[Chromium 输入事件链](https://chromium.googlesource.com/chromium/src/+/main/docs/ui/input_event/index.md)、[SendInput](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput)、[GetGUIThreadInfo](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getguithreadinfo)。

## 回执与诊断

| 结果 | 解释与下一步 |
|---|---|
| `blocked` | 原生输入前拒绝。修复焦点、坐标或刷新观察后才重试 |
| `dispatched` | OS 接受输入，应用效果未验证。先观察 |
| `verified` | 仅验证本动作承诺的条件：focus 是原生焦点，mouse_move 是光标位置；不是整个 GUI 任务完成 |
| `failed` / `unknown` | 可能已经有副作用。部分接受时记录数量，并尝试释放已接受前缀的按键；不重放整段 |

检查 `before/after`、`receipt.submittedInputCount/expectedInputCount`、`nativeError` 与 `detail`。成功的 SendInput 不读取残留 LastError；派发后窗口消失保留接受数量并返回 unknown。人为已按住修饰键时拒绝快捷键，避免改变用户按键状态。剪贴板只对获取锁做短暂有界重试，不自动重新粘贴。

实体输入有效而合成输入无效时，依次核对同一个窗口/编辑区域、实体与合成输入下的前台/原生焦点、窗口权限完整性、所在桌面/会话、光标读回和实际画面。只有排除这些后才研究输入过滤。这里没有安装或要求虚拟 HID/过滤驱动，也不声称所有 Chromium 客户端都已验证。

## 主模型与小模型的分工

`IComputerElementDetector` 接收本次截图像素与物理区域，产出 `RawDetection(Rect, Kind, Source, Label?, Confidence?)`。默认 OpenCV 只有轮廓/背景候选，不能解释为已识别的按钮、文本框或可点击控件。当前上层借助原图作语义选择，编号图只提供位置参考。

未来 OCR、YOLO 或视觉语言小模型作为候选提供者接入；提供者内部把自己的缩放/letterbox 坐标还原到约定的物理屏幕坐标，再交统一标注器。文字、视觉类别、可交互性是三个不同结论；置信度也必须保留来源。不能跨截图沿用裸元素 ID。

主模型保留选择、纠偏、动作和结果判断职责；候选没有命中目标时仍可选原图坐标或裁图重看。原图与标注图必须来自同一帧，标注不能成为唯一证据。后续评测应分别衡量定位误差、编辑焦点成功率、文本完全一致率和任务后置条件，不能用派发成功率替代。

当前未接入 OCR/YOLO/VLM、UIA 语义定位、驱动输入、滚轮/拖拽或自动任务规划。旧的 `WindowsInputSimulator`/`WindowsKeyboardSimulator` 保留给已有实验调用；生产工具使用新的 `WindowsDesktopInput`。

交互式回归入口：[test-computer-use.ps1](../../TestScripts/test-computer-use.ps1)。它打开自己的 WebView2 窗口，在各显示器上验证实际输入；会移动鼠标、短暂占用焦点和写入测试剪贴板。只在空闲的交互桌面运行。脚本将构建、截图和断言日志放入 `.work/computer-use-smoke/`。页面脚本仅用于读取验收结果，不参与点击或填值。

部署由 [Desktop Guardian](Desktop-Guardian.md) 执行，外部控制器验证 `healthy` 与新 Desktop/Core 进程，不能只报告 202 已受理。

## 焦点探针（ComputerUseFocusProbe，2026-11-03）

`TestScripts/ComputerUseFocusProbe` 把「点击是否取得编辑焦点」与「文字是否真的进入控件」拆开观测。
判据只用原生可观测状态：前台窗口、目标 GUI 线程的 `hwndFocus` / `hwndCaret`、目标控件文本长度与哈希；
任一项拿不到记 `n/a`，不推断。

用法：

```
dotnet run --project TestScripts/ComputerUseFocusProbe -- --self --out .work/<任务>/<运行>/self.txt
dotnet run --project TestScripts/ComputerUseFocusProbe -- --hwnd <n> --x <客户区x> --y <客户区y>
```

正对照（`--self`，六阶段，实测于 2026-11-03）：

| 阶段 | 观测 | 判读 |
|---|---|---|
| A 基线 | focus=根窗口，caret=0 | 尚未建立编辑焦点 |
| B 窗口消息点击 | **focus=Edit，caret=Edit** | 窗口消息能建立原生焦点 |
| C SendInput 点击 | 保持 Edit | 与 B 结果一致 |
| D SendInput Unicode | text_len 0 → 9 | 按键注入生效 |
| E 剪贴板 + Ctrl+V | 9 → 13 | 中文经剪贴板进入 |
| F WM_CHAR 直投根窗口 | 13 → 13 | 无效；焦点在子控件，投给根窗口不生效 |

`--self` 是取红用的：任何通道在自有窗口上失效，先怀疑探针本身，再怀疑目标应用。

## Chromium 系应用：采集前必须先唤醒（2026-11-04）

**症状**：对 Chromium 内核的桌面应用（Electron / WebView2 / 内置 CEF）调用 `FlaUiInspector.Capture`，
恒得到 `nodes=1`、`availability=Sparse`，无论等多久、无论新建还是复用 inspector 实例。

**原因**：这类应用的无障碍树是**按需构建**的，而 **UIA3 通道唤不醒它**。
同进程对照实测：

```
uia3 首次 = 1 节点
uia2 首次 = 4 节点        ← MSAA / IAccessible 通道能唤醒
uia3 再来 = 4 节点        ← 被唤醒后 UIA3 立即可读
```

**当前实现（`FlaUiInspector`）**：`FromHandle` **之前**先用 `FlaUI.UIA2` 做一次廉价读取（读根节点子数），
既探测也唤醒。顺序不能颠倒——UIA3 根元素若在唤醒前取得，会缓存一个空子树，这一次采集就白跑。

**排障要点**：
- 看到 Chromium 应用恒 `Sparse` 时，先确认是不是「没唤醒」，不要急着判「该应用不支持无障碍」。
- 换页后节点数会变（推荐页 78、直播间页 4），**这是页面内容差异，不是采集缺陷**；实测唤醒后 8 秒内不再增长。
- `Sparse` 的含义是「可操作节点 < 阈值 3」，不等于「读不到」，两者在 `reason` 里是分开写的。

### 已知代价与调优方向（2026-11-04 02:55）

唤醒探测对**本来就没有子节点的窗口**会白等满预热窗口。实测：

| 目标 | 结果 |
|---|---|
| explorer 的隐藏 shell 窗口（hwnd 3541866） | `nodes=1`，单次采集 **933ms**（等满 800ms + 开销） |
| VS Code（Electron） | `nodes=3`，单次采集 6~7ms |
| douyin（Chromium） | 唤醒后 4 节点，采集 4~6ms |

也就是说：**唤醒本身是即时生效的**（MSAA 一探就醒），800ms 的等待只对「真的没有子节点」的窗口是纯损耗。

调优方向（未实施）：把预热从「等满窗口」改为「最多重试 2~3 次、总计 ≤300ms」；
或用「同一 hwnd 已知无子节点」的短期记忆跳过等待。改动前需先测：
MSAA 唤醒后是否存在「首探 0、再探有」的窗口——若没有，预热循环本身可退化为单次探测。

### 唤醒改动对原生应用无回归（2026-11-04 16:30）

| 目标 | 类型 | nodes | 采集耗时 |
|---|---|---|---|
| AMD Bug Report Tool | 原生 WinForms | 13 | 13~14ms |
| DeepSeek Harness | Electron | 4 | 6~8ms |

两类应用都能读到节点，MSAA 唤醒没有把原生应用拖慢或读空。

**但两者的 `actionable` 都是 0**，说明 `IsActionable`（要求 `IsEnabled && !IsOffscreen && Patterns.Count > 0`）
在这些应用上过于严格——原生 WinForms 控件理论上应有模式。这是**待查项**：
要么模式集合读不到，要么判据把有用控件排除了。它会让可用性分类长期偏向 `Sparse`，
值得单独验证，但本次不动。
