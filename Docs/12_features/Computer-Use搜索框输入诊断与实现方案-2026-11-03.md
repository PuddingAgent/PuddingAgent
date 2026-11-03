---
title: Computer Use 搜索框输入诊断与实现方案
author: Codex
date: 2026-11-03
last_reviewed: 2026-11-03
status: proposed
description: 用焦点与输入来源的交叉对照区分搜索框故障，优先菜单粘贴和无障碍写值，限制 IME 与驱动方案的范围。
categories: [docs, features]
tags: [computer-use, chromium, focus, accessibility, input]
related_docs: [Docs/13_runbooks/Computer-Use.md, Docs/14_reports/2026-11-03-Computer-Use验收.md]
related_files: [Source/PuddingComputer.Windows/WindowsDesktopInput.cs, Source/PuddingComputer.Windows/Win32InputPlatform.cs, Source/PuddingComputer.UIAutomation/FlaUiInspector.cs, Source/PuddingRuntime/Tools/BuiltIns/Computer/ComputerInputTool.cs, TestScripts/ComputerUseSmoke/Program.cs]
slug: features-computer-use-searchbox-input-2026-11-03
draft: false
---

# Computer Use 搜索框输入诊断与实现方案

目标只到「抖音客户端搜索框显示完整中文关键词，并提交得到对应查询的结果态」。这是待实现方案，不声称抖音已经验收。输入依据为 `.work/cu-searchbox/20261103-2150/problem.md` 及其后补充。通用组件现状见 [运行手册](../13_runbooks/Computer-Use.md)，已有测试与权限限制见 [验收报告](../14_reports/2026-11-03-Computer-Use验收.md)。

## 判断及需要撤回的推断

现有证据不足以二选一判定「焦点问题」或「应用过滤注入」。先做一个可以分离鼠标和键盘失败的探针，并同时试验不经过键盘的菜单粘贴。

- 接受 SendInput 数量是 OS 插入事件数，不是目标窗口收到或编辑框消耗事件数；SendInput 也是合成输入，不能称为实体鼠标。
- SendInput 全部接受后读取到非零 GetLastError，不应当作本次失败原因；成功调用不保证把线程先前的 last-error 清零。错误码只按相应 API 的失败约定解释。
- WindowFromPoint 在整个客户区都返回同一个 render HWND，不能用它证明命中了某个 DOM 搜索框。导航切页只证明导航位置的点击有效。
- 曾经 GetForegroundWindow 正确不能代表随后每次输入时都正确；原生焦点也不等于 Blink 编辑节点焦点。
- keybd_event 空格曾改变视频状态，说明「所有键盘在所有状态下完全被拒」过强。页面快捷键有效而编辑无效与编辑焦点缺失相容，但还没有根因证明。
- 键盘低层钩子标记是 `LLKHF_INJECTED`；`LLMHF_INJECTED` 属于鼠标。改扫描码、dwExtraInfo 或换 keybd_event 不会提供清除 OS 注入来源的通用能力。
- 用户提供的 Medium→Medium 证据与 Codex 外部探针的 Low integrity 是两个运行上下文；不能用后者推翻前者，也不能把后者失败记作抖音过滤实证。
- 类名不能单独区分 CEF、WebView2 或其他 Chromium 嵌入方式。记录实际浏览器进程/运行库版本；启动参数是否被产品读取也依赖嵌入方。
- CDP 连接拒绝只证明那个地址当时没有服务，不能单凭它断言应用主动关闭了端口；需新浏览器进程身份、实际参数与监听 PID 的同次记录。

## 焦点链与消息入口

至少要区分三个状态：Windows 前台/线程原生焦点 → Chromium 内部 Aura 内容焦点与 InputMethod → Blink 的编辑节点焦点。键盘按内部焦点路由，鼠标按命中目标路由；内容事件经 RenderWidgetHostViewAura 送往渲染端。页面快捷键被处理不能证明编辑节点收到文字。[Chromium 输入事件链](https://chromium.googlesource.com/chromium/src/+/main/docs/ui/input_event/index.md)。

当前 Chromium 的 Chrome_RenderWidgetHostHWND 是兼容子窗口；OnMouseRange 会转换坐标后转发父窗口事件目标，OnKeyboardRange 也转发父窗口的键盘处理器。因此不能简化成“子窗口只接鼠标、顶层只接键盘”，更不能把该 HWND 当 DOM 文本框。手发鼠标窗口消息可能触发导航，却没有复现操作系统激活和全部焦点状态转换。抖音的具体内核版本仍需核验，以上是上游实现依据而非其私有改动的证明。[Chromium legacy HWND](https://chromium.googlesource.com/chromium/src/+/HEAD/content/browser/renderer_host/legacy_render_widget_host_win.cc)。

AttachThreadInput + SetFocus 只作为原生焦点缺失时的可观测分支：目标已处于前台，临时附加线程输入队列、对顶层设置焦点、读回 GetGUIThreadInfo，并在 finally 中解除附加。它不能选择搜索 DOM 节点；输入队列附加还会重置键状态，不能在组合键中途调用。若原生焦点已经正确，不应重复附加。WM_SETFOCUS/WM_ACTIVATE 是通知，手发通知不等于改变系统维护的焦点/激活状态。[AttachThreadInput](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-attachthreadinput)、[SetFocus](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setfocus)。

## 探针：把焦点建立与文字写入分开

每次 trial 固定同一进程实例、同一搜索框和观察坐标映射，先取得该轮实体点击+实体输入的正对照。不要在准备输入后切回终端或聊天窗口操作。探针预先启动，用不改变前台的定时触发或全局热键触发，等待热键修饰键全部松开。

记录每阶段的 monotonic 时间、PID/启动时间、顶层/子 HWND、前台根、目标 GUI 线程的 active/focus/capture/caret、最小化状态、DPI/客户区/源图尺寸、目标点与实际光标、两侧完整性和会话、键盘布局、修饰键状态、派发数量。保留搜索框局部原图前后帧，不把光标/颜色变化单独作为输入成功。

前台/原生焦点稳定条件可采用 200 ms 连续匹配、总等待 2 s；这些是探针初始参数而非系统保证。Chromium 的 `hwndFocus` 可以是顶层，`hwndCaret=0` 不能判失败。不用伪造 WM_ACTIVATE/WM_SETFOCUS 代替状态检查。

| 试验 | 建立焦点 | 写入 | 必须观察的结果及意义 |
|---|---|---|---|
| P/P | 实体鼠标点搜索框 | 实体键盘输入 `m` | `m` 在搜索框出现；失败则本轮其他试验无有效基线 |
| P/S/P | 保持 P/P 状态和前台 | 合成 `x`，再实体 `n` | `mxn`：合成键盘在已知编辑焦点下可用；`mn`：合成键盘链路特异失败；也没有 `n`：状态已变，不能归因为过滤 |
| S/P | 程序激活并稳定，合成鼠标点击 | 实体输入 `m` | 成功：合成点击能取得编辑焦点；失败且 P/P 成功：优先定位点击/编辑焦点路径 |
| F/S/S | 显式激活并稳定 → 合成点击 | 合成输入 | 成功：三段式可实现；失败须结合上两行，不凭这一行猜根因 |
| W/P | 已知有效的窗口消息点击搜索框 | 实体输入 | 成功：消息点击确实取得编辑焦点；失败：导航有效不能推广到输入框 |

已知编辑焦点下分别试 VK 单个字母、扫描码单个字母、Unicode、最后剪贴板 Ctrl+V。每种单独重置、单独取证；不要在一个 trial 连发所有编码。空格仅作页面快捷键旁证，不是文本编辑验收。

若只想判定“第一次点击被激活吃掉”，另做 inactive→一次点击→实体字符，与 inactive→一次点击→等待超过系统双击时间→第二次点击→实体字符的配对。再比较明确激活后的一次点击。第二次点击也可能改变命中/布局/文本选择，故只有重复一致且其他条件稳定时，才归类为 activation-dependent；不能直接宣称抓到了 WM_MOUSEACTIVATE 返回值。

Windows 允许 `MA_ACTIVATEANDEAT`，但并非第一次点击必然被吃；当前 Chromium legacy HWND 的常见返回是 `MA_ACTIVATE`。这是待检验假设，不是 Chromium 的通则。[Windows 激活说明](https://learn.microsoft.com/en-gb/windows/win32/inputdev/about-mouse-input)、[Chromium 对应处理](https://chromium.googlesource.com/chromium/src/+/HEAD/content/browser/renderer_host/legacy_render_widget_host_win.cc)。

## 让探针自身先取红

扩展自有 WinForms/WebView2 测试夹具，而非修改抖音：

1. `eat-first-activation-click` 模式在自有顶层窗口返回 MA_ACTIVATEANDEAT；裸一次点击路径必须红，显式激活后点击恢复。
2. `page-focus-only` 模式只响应页面空格、输入框不焦点；测试必须拒绝把视频状态变化判为文字输入成功。
3. `drop-injected-keyboard` 模式仅针对该夹具前台、带本轮标记的输入，用 LL 键盘钩子拒绝合成事件；实体输入继续有效。分类器必须报告合成链路失败，不能误报坐标问题。
4. 输入前抢焦点、移动窗口、移动光标、让搜索框只读，均应返回 blocked/unknown 或效果验证失败；不能把 API accepted 当作绿。

真实应用的探针可安装**只读**低层钩子，记录 `vkCode/scanCode/flags/time/dwExtraInfo`；回调立即 CallNextHookEx。看到 injected 标记只证明事件被观察到，不证明哪个模块丢弃。没有目标侧日志或定位到的过滤代码，结论最多是 `synthetic-keyboard-path-failed`，不要生成 `application-filter-proven`。

建议分类结果：`baseline-invalid`、`native-focus-failed`、`editor-focus-failed`、`activation-dependent`、`synthetic-keyboard-path-failed`、`text-verified`、`query-verified`、`unknown`。OCR 读不清不能当作“文本未变化”的确定证据。

## 首选非键盘通路：可见菜单的粘贴命令

现有窗口消息鼠标已能触发应用操作，因此先测试：剪贴板写入唯一中文串并回读 → 对搜索框打开实际上下文菜单 → 确认可用的“粘贴”项 → 点击该项 → 核对搜索框全文 → 点击实际搜索按钮 → 核对查询与结果态。

这是调用应用自身的编辑命令，不必让它认可 Ctrl+V。菜单弹出不算成功；粘贴灰色、菜单不是编辑菜单、文本不一致都取红。菜单可能有独立 HWND，必须重新观察/定位，不能沿用 render 子窗口的坐标或猜 WM_COMMAND ID。首次验证使用已空的测试输入框；重复执行有追加风险，不能盲重放。

`WM_PASTE` 是控件需要实现的消息，不是系统“把剪贴板填进任意 DOM 节点”的服务。Chrome 顶层或 legacy HWND 不是 Win32 Edit 控件；没有实现证据时停止盲发 WM_PASTE/WM_SETTEXT。WM_COPYDATA 同样需要接收方已有协议。

## 第二条非键盘通路：按需无障碍和 SetValue

两空 Pane 只说明本次 UIA 观察贫乏。先同时采集 UIA Raw/Control View 与 MSAA：用 `AccessibleObjectFromWindow(render, OBJID_CLIENT, IID_IAccessible, ...)` 请求真实客户端对象，再在时间预算内重枚举；检查目标 PID、节点范围、编辑角色、可写 Value 模式和读取的值。

Chromium 的 WM_GETOBJECT 路径能按请求建立 UIA/MSAA 根对象，但 UIA Provider 是否启用仍有条件。没有一个通用 UIA 调用可以强迫任意第三方应用实现完整 provider。不要把只回一个根对象当作成功。[Chromium OnGetObject](https://chromium.googlesource.com/chromium/src/+/HEAD/content/browser/renderer_host/legacy_render_widget_host_win.cc)。

支持时优先 `ValuePattern.SetValue`；如果仅 MSAA 可用，再检查 provider 是否支持 `IAccessible::put_accValue`。不能把 TextPattern 的可读能力等同于可写。Chromium 的 Value provider 将写值转为 `kSetValue` 无障碍动作，因而不依赖 SendInput；应用是否真正支持，仍要现测。[Chromium Value provider](https://chromium.googlesource.com/chromium/src/+/c9b7d1eaffddbb766bb331938b76d57bae9ca285/ui/accessibility/platform/ax_platform_node_win.cc)、[ValuePattern API](https://learn.microsoft.com/en-us/dotnet/api/system.windows.automation.valuepattern.setvalue)。

概念流程（ReResolve/Verify 是待实现的边界，非现成 API）：

```csharp
var element = ReResolveAndCheckOwner(observationRef, expectedProcessInstance);
var value = element.Patterns.Value.PatternOrDefault;
if (value is null || value.IsReadOnly.Value) return Unsupported();
value.SetValue(keyword);
// 不返回“SetValue 调用没抛异常，所以成功”。
var fresh = ReResolveAndCheckOwner(observationRef, expectedProcessInstance);
if (fresh.Patterns.Value.Pattern.Value.Value != keyword) return VerificationFailed();
return TextVerifiedWithScreenshot(); // 下一步仍要验证提交后查询结果
```

`--force-renderer-accessibility=complete` 是 Chromium 支持的启动选项，但不保证嵌入应用转发、版本支持或启用其 UIA provider。若产品命令行固定，此分支当前不可用；不改二进制、不绕过单实例/启动器，不把一个无法进入实际浏览器进程的参数当方案。若未来开放启动配置，要求新浏览器 PID/启动时间和实际参数证据，再做有/无开关的树比较；按内核版本评估 UIA provider feature，不能盲套一个 UiaProvider 开关。[Chromium 无障碍启动选项](https://github.com/chromium/chromium/blob/main/docs/accessibility/overview.md)。

UIA COM 调用可能卡在 provider；现有 FlaUiInspector 的循环预算无法打断单次阻塞 COM 调用。新的动作先在独立可终止的 UIA 工作进程测试，超时写 unknown；迟到的 SetValue 可能已经生效，必须读回，不能自动再写。

## IME 判断

不采用“激活 HIMC + 投递 WM_IME_COMPOSITION/WM_IME_SETCONTEXT”的伪提交路径。

WM_IME_COMPOSITION 通知组成状态变化，GCS_RESULTSTR 只是标记；接收方从实际输入上下文读取结果串，不是从 lParam 读取你提供的 Unicode 指针。WM_IME_SETCONTEXT 也不是选择 DOM 编辑节点的命令。没有有效编辑焦点、文本存储和组成数据，投递这些消息不能构造一次合法中文提交。[WM_IME_COMPOSITION](https://learn.microsoft.com/en-us/windows/win32/intl/wm-ime-composition)、[WM_IME_SETCONTEXT](https://learn.microsoft.com/en-us/windows/win32/intl/wm-ime-setcontext)。

真正实现 TSF 文本服务可请求编辑会话，由目标文本存储授予锁并提交文字；这是注册/安装文本服务、适配生命周期与焦点的另一个组件，不是外部进程拿到 HWND 即可写入的接口。它也不能自动补上缺失的编辑焦点，不作为本次最小路径。[TSF 编辑会话](https://github.com/MicrosoftDocs/win32/blob/docs/desktop-src/TSF/edit-sessions.md)。

## 驱动与 HID 的准入条件

只在 P/P 稳定、P/S/P 持续证明合成键盘特异失败，且菜单粘贴/无障碍路径均无效后评估。

FakerInput 不是和 Interception 等价的内核过滤驱动：其上游项目使用 `WindowsUserModeDriver10.0`、UMDF 2，是需要安装的虚拟 HID 驱动包。Interception 是设备过滤/注入路线。两者改变的是输入进入系统的位置，不会替你选择 DOM 搜索框；安装后原 SendInput 调用不会自动改走 HID。[FakerInput 项目文件](https://raw.githubusercontent.com/Ryochan7/FakerInput/master/FakerInput/FakerInput.vcxproj)、[Interception 发送实现](https://raw.githubusercontent.com/oblitum/Interception/master/library/interception.c)。

客户端必须改用 HID 报告通道，或 `interception_send` 的设备/扫描码接口。单独测设备事件的钩子标记与目标行为，不保证所有应用接纳虚拟设备。HID 报告传按键，不是 Unicode 字符串；中文的最低复杂度组合通常仍是剪贴板全文 + HID Ctrl+V，然后用读取/视觉验收全文并点击搜索。

若仅需生成输入，优先评估独立虚拟 HID，减少对现有物理键鼠链的影响；不是承诺 FakerInput 可直接用于本机。其上游已归档；Interception 官方只声明测试至 Windows 10。本机 build 26300 的签名、安装/卸载、Secure Boot/内存完整性兼容性必须独立验收，不能用“NT 10.0”代替产品版本兼容证明。Interception 的商用许可另有条件。[FakerInput 上游](https://github.com/Ryochan7/FakerInput)、[Interception README](https://github.com/oblitum/Interception)。

不为这个实验关闭系统安全设置。驱动包安装和卸载需单独明确授权、恢复路径和真实按键释放测试；当前任务未安装任何驱动。

## 代码施工顺序

| 顺序 | 文件/组件 | 具体工作与交付门槛 |
|---|---|---|
| 1 | 新 `TestScripts/ComputerUseFocusProbe/Program.cs` 与夹具测试 | 实现上述矩阵、统一 trial 记录和故障注入；先在自有窗口取红，再测抖音；日志仅记录本轮测试标记，避免变成键盘记录器 |
| 1 | `Source/PuddingComputer.Windows/Win32InputPlatform.cs` | 增加可选诊断状态：调用者/目标完整性、会话、键盘布局、实际落点；扫描码仅作为诊断编码分支，仍标明合成来源 |
| 1 | `Source/PuddingComputer.Windows/WindowsDesktopInput.cs` | 复用 Inspect/Focus/Click/Key/TypeText；必要时增加激活稳定等待，继续区分 native-focus 和 editor-focus，不给生产工具增加无限重试 |
| 2 | 新 `Source/PuddingComputer.UIAutomation/FlaUiValueWriter.cs` | 独立可写能力检查、重新解析/归属核验、SetValue 后新值读回；不把写动作塞入只读 FlaUiInspector |
| 2 | 新独立 UIA probe/worker，及 `Source/PuddingComputer.UIAutomationTests/` | Raw/Control/MSAA 能力探测，处理 provider 超时；只读/不支持/值不变/元素失效用例取红；通过后再接工具 |
| 2 | 上层搜索适配器 | 选择菜单粘贴或 Value 写值；将 TextVerified 与 QueryVerified 分开。输入后的 Enter 可失败，允许已验证的搜索按钮点击 |
| 3 | 独立 HID adapter（暂不建） | 只替换输入发送后端，保留窗口、焦点、坐标与后置验收；先单独设备验收，再接宿主 |

不能把 API→UIA→键盘→视觉写成一次性的固定降级链。视觉既负责定位也负责前后验证；键盘路径失败之后可以转到**应用菜单命令**或**无障碍写值**，但每次转路都必须依据已证实能力，不是“换一个消息号再试”。
