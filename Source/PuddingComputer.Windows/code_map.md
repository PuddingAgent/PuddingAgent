# PuddingComputer.Windows · code_map

Windows Computer Use 平台组件。依赖方向：Windows → Automation → Contracts；不负责宿主装配。

| 文件 | 职责与边界 |
|---|---|
| [WindowsInteractiveWindowFinder.cs](WindowsInteractiveWindowFinder.cs) | 按进程枚举可见窗口，使用亮度、颜色种类、面积选择分析窗口；中心命中归属不参与评分，评分不证明输入可达性 |
| [ComputerScreenAnalyzer.cs](ComputerScreenAnalyzer.cs) | 截图、窗口解析、检测与标注；保留图像几何与分析窗口身份，找不到候选时明确失败 |
| [OpenCvElementDetector.cs](OpenCvElementDetector.cs) | 背景差异与边缘检测，支持参考区域和轮廓尺寸过滤 |
| [WindowsScreenCapturer.cs](WindowsScreenCapturer.cs) | GDI 屏幕采集，返回像素与采集区域 |
| [WindowsMonitorEnumerator.cs](WindowsMonitorEnumerator.cs) | 枚举显示器范围，判断物理屏幕点是否落在实际显示区域 |
| [WindowsDpiEnforcer.cs](WindowsDpiEnforcer.cs) | DPI 感知声明与当前线程感知查询 |
| [WindowsInputSimulator.cs](WindowsInputSimulator.cs) | 图像点映射与鼠标派发；在实际落点检查指定的授权窗口根，和分析窗口评分分开 |
| [WindowsKeyboardSimulator.cs](WindowsKeyboardSimulator.cs) | Unicode 文本、单键与组合键派发 |
| [WindowsWindowManager.cs](WindowsWindowManager.cs) | 窗口枚举、几何、前台控制与窗口命中查询 |

测试入口：[WindowsTests](../PuddingComputer.WindowsTests/code_map.md)。
