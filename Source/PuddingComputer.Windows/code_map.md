# PuddingComputer.Windows · code_map

Windows Computer Use 平台组件。依赖方向：Windows → Automation → Contracts；不负责宿主装配。

| 文件 | 职责与边界 |
|---|---|
| [ComputerDesktopSession.cs](ComputerDesktopSession.cs) | observe/input 共用的有界会话；原图与标号图共用几何；截图 ID 绑定调用者、窗口实例、DPI/布局和局部像素，单次消费 |
| [WindowsDesktopInput.cs](WindowsDesktopInput.cs) + [Win32InputPlatform.cs](Win32InputPlatform.cs) | 前台/原生焦点、光标读回、实际落点归属与 SendInput 数量检查；部分派发不可重放，UI 效果仍待观察 |
| [IComputerElementDetector.cs](IComputerElementDetector.cs) | 可替换视觉候选提供者；物理屏幕框、来源、可选文字/置信度，不宣称可交互语义 |
| [WindowsInteractiveWindowFinder.cs](WindowsInteractiveWindowFinder.cs) | 按进程枚举可见窗口，使用亮度、颜色种类、面积选择分析窗口；中心命中归属不参与评分，评分不证明输入可达性 |
| [ComputerScreenAnalyzer.cs](ComputerScreenAnalyzer.cs) | 截图、窗口解析、检测与标注；保留图像几何与分析窗口身份，找不到候选时明确失败 |
| [OpenCvElementDetector.cs](OpenCvElementDetector.cs) | 背景差异与边缘检测，支持参考区域和轮廓尺寸过滤 |
| [WindowsScreenCapturer.cs](WindowsScreenCapturer.cs) | GDI 屏幕采集，返回像素与采集区域 |
| [WindowsMonitorEnumerator.cs](WindowsMonitorEnumerator.cs) | 枚举显示器范围，判断物理屏幕点是否落在实际显示区域 |
| [WindowsDpiEnforcer.cs](WindowsDpiEnforcer.cs) | 同步线程 Per-Monitor V2 作用域并恢复；加载程序集不改变宿主 DPI，进程声明只允许显式调用 |
| [WindowsInputSimulator.cs](WindowsInputSimulator.cs) | 图像点映射与鼠标派发；在实际落点检查指定的授权窗口根，和分析窗口评分分开 |
| [WindowsKeyboardSimulator.cs](WindowsKeyboardSimulator.cs) | Unicode 文本、单键与组合键派发 |
| [WindowsWindowManager.cs](WindowsWindowManager.cs) | 窗口枚举、几何、前台控制与窗口命中查询 |

测试入口：[WindowsTests](../PuddingComputer.WindowsTests/code_map.md)。

工具消费者：[computer_input](../PuddingRuntime/Tools/BuiltIns/Computer/ComputerInputTool.cs)、[computer_observe](../PuddingHost/Tools/ComputerObserveTool.cs)。操作契约见 [运行手册](../../Docs/13_runbooks/Computer-Use.md)。
