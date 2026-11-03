# PuddingComputer.WindowsTests · code_map

Windows 平台组件测试。纯规则与合成图像用例不派发输入；窗口解析用例会只读采集当前桌面。

| 文件 | 覆盖边界 |
|---|---|
| [DesktopInputTests.cs](DesktopInputTests.cs) | 模拟 OS 边界验证 INPUT 布局、原生焦点、落点复核、部分输入释放、派发后窗口丢失，以及截图身份/几何/DPI/期限失效；不派发真实输入 |
| [InteractiveWindowScoreTests.cs](InteractiveWindowScoreTests.cs) | 验证生产路径仍使用的亮度、颜色与面积评分；不将样本排序解释为点击成功 |
| [CenterInsideWindowTests.cs](CenterInsideWindowTests.cs) | 检测目标中心是否位于参考矩形内；这是几何过滤，不是窗口中心命中测试 |
| [DetectorFocusRectTests.cs](DetectorFocusRectTests.cs) | 合成图像中的参考区域轮廓恢复与尺寸过滤 |
| [ScreenAnalysisGeometryTests.cs](ScreenAnalysisGeometryTests.cs) | 截图原点、尺寸映射，以及显式参考窗口的身份边界 |
| [ScreenAnalyzerWindowResolutionTests.cs](ScreenAnalyzerWindowResolutionTests.cs) | 无候选时失败、显式参考矩形、窗口外目标；依赖桌面截图，未检出目标时可报 Inconclusive |
| [WindowsInputSimulatorClickTests.cs](WindowsInputSimulatorClickTests.cs) | 纯点击解析、采集方式限制、越界与负坐标，不实际点击 |
| [WindowOwnershipTests.cs](WindowOwnershipTests.cs) | 实际点击路径使用的授权根判定；不承担分析窗口排序 |

被测组件：[Windows](../PuddingComputer.Windows/code_map.md)。
