# PuddingComputer.WindowsTests · code_map
> 源指纹: TestResults/**=2-7b8c53266e12, *.cs=8-8a54f874f8f9, *.csproj=1-ffc8159738f4 · 条目数: 8 · 最近整理: 2026-11-03

Windows 平台组件测试。纯规则与合成图像用例不派发输入；窗口解析用例会只读采集当前桌面。

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `DesktopInputTests.cs` | 用 OS 边界桩验证输入布局与截图身份期限失效 | `DesktopInputTests` | `WindowsDesktopInput` | 不派发真实输入；Chromium 根焦点允许但永不判为已验证 |
| `InteractiveWindowScoreTests.cs` | 亮度、颜色种类与面积评分的生产路径语义 | `InteractiveWindowScoreTests` | `WindowsInteractiveWindowFinder` | 不将样本排序解释为点击成功 |
| `CenterInsideWindowTests.cs` | 检测目标中心是否位于参考矩形内 | `CenterInsideWindowTests` | `ComputerScreenAnalyzer` | 这是几何过滤，不是窗口中心命中测试 |
| `DetectorFocusRectTests.cs` | 合成图像中的参考区域轮廓恢复与尺寸过滤 | `DetectorFocusRectTests` | `OpenCvElementDetector` | 尺寸上限相对参考区域而非整屏 |
| `ScreenAnalysisGeometryTests.cs` | 截图原点与尺寸映射，以及显式参考窗口的身份边界 | `ScreenAnalysisGeometryTests` | `ComputerScreenAnalyzer` | — |
| `ScreenAnalyzerWindowResolutionTests.cs` | 无候选时失败、显式参考矩形与窗口外目标 | `ScreenAnalyzerWindowResolutionTests` | `ComputerScreenAnalyzer` | 依赖桌面截图，未检出目标时报 Inconclusive 而非失败 |
| `WindowsInputSimulatorClickTests.cs` | 点击坐标解析的映射约束回归 | `WindowsInputSimulatorClickTests` | `WindowsInputSimulator` | 不实际点击 |
| `WindowOwnershipTests.cs` | 实际点击路径使用的授权根判定 | `WindowOwnershipTests` | `WindowsInputSimulator` | 不承担分析窗口排序 |

被测组件：[Windows](../PuddingComputer.Windows/code_map.md)。
