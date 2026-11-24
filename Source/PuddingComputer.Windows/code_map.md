# PuddingComputer.Windows · code_map
> 源指纹: *.cs=13-c8f25aa6d198, *.csproj=1-7896e3dbd7b9 · 条目数: 13 · 最近整理: 2026-11-03

Windows Computer Use 平台组件。依赖方向：Windows → Automation → Contracts；不负责宿主装配。

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `ComputerDesktopSession.cs` | observe/input 共用的有界会话：原图与标号图共用同一几何 | `ComputerDesktopSession`、`DesktopObservation` | `ComputerScreenAnalyzer.cs` | 截图 ID 绑定调用者、窗口实例、DPI/布局与局部像素，且只能消费一次 |
| `WindowsDesktopInput.cs` | 输入前的前台焦点与落点归属检查，并核对 SendInput 数量 | `WindowsDesktopInput`、`IWindowsInputPlatform`、`DesktopInputResult` | `Win32InputPlatform.cs` | 部分派发不可重放；UI 效果仍待观察 |
| `Win32InputPlatform.cs` | SendInput 平台层：鼠标与键盘 INPUT 结构 | `NativeDesktopInput`、`InputUnion`、`MouseData` | `WindowsDesktopInput.cs` | INPUT 布局在两架构一致，漏掉 union 会静默丢事件 |
| `IComputerElementDetector.cs` | 可替换的视觉候选提供者接缝 | `IComputerElementDetector` | `OpenCvElementDetector.cs` | 只给物理屏幕框、来源与置信度，不宣称可交互语义 |
| `WindowsInteractiveWindowFinder.cs` | 按进程枚举可见窗口并按亮度、颜色种类与面积选分析窗口 | `WindowsInteractiveWindowFinder`、`WindowCandidate` | `ComputerScreenAnalyzer.cs` | 中心命中归属不参与评分；评分不证明输入可达性 |
| `ComputerScreenAnalyzer.cs` | 截图与窗口解析，以及检测和标注的编排 | `ComputerScreenAnalyzer`、`ScreenAnalysis` | `WindowsScreenCapturer.cs` | 保留分析窗口身份；找不到候选时明确失败，不退回整屏 |
| `OpenCvElementDetector.cs` | 背景差异与边缘检测，支持参考区域和轮廓尺寸过滤 | `OpenCvElementDetector` | `IComputerElementDetector.cs` | — |
| `WindowsScreenCapturer.cs` | GDI 屏幕采集，返回像素与采集区域 | `WindowsScreenCapturer`、`ScreenCapture` | `ComputerScreenAnalyzer.cs` | 采集区域必须随像素一起给出，否则图内坐标无法还原 |
| `WindowsMonitorEnumerator.cs` | 枚举显示器范围并判断物理点是否落在真实显示区域 | `WindowsMonitorEnumerator`、`MonitorBounds` | `ComputerCoordinateMapper` | — |
| `WindowsDpiEnforcer.cs` | 同步线程 Per-Monitor V2 作用域并恢复 | `WindowsDpiEnforcer` | `WindowsDesktopInput.cs` | 进程 DPI 声明只允许显式调用；加载程序集不改变宿主 DPI |
| `WindowsInputSimulator.cs` | 图像点映射与鼠标派发，并在实际落点复核授权窗口根 | `WindowsInputSimulator`、`ClickResolution`、`ClickOutcome` | `WindowsMonitorEnumerator.cs` | 落点归属检查与分析窗口评分必须分开，混用会放行错窗口 |
| `WindowsKeyboardSimulator.cs` | Unicode 文本、单键与组合键派发 | `WindowsKeyboardSimulator`、`VirtualKey` | `Win32InputPlatform.cs` | — |
| `WindowsWindowManager.cs` | 顶层窗口的枚举与几何查询 | `WindowsWindowManager`、`WindowInfo` | `WindowsInputSimulator.cs` | — |

测试入口：[WindowsTests](../PuddingComputer.WindowsTests/code_map.md)。

工具消费者：[computer_input](../PuddingRuntime/Tools/BuiltIns/Computer/ComputerInputTool.cs)、[computer_observe](../PuddingHost/Tools/ComputerObserveTool.cs)。操作契约见 [运行手册](../../Docs/13_runbooks/Computer-Use.md)。
