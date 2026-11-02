using Pudding.Contracts.Computer;
using PuddingComputer.Windows;

namespace PuddingComputer.WindowsTests;

/// <summary>
/// <see cref="ComputerScreenAnalyzer"/> 的窗口解析行为。
/// <para>
/// 锁定的核心是**失败要显式**：找不到可用于坐标参考的窗口时必须抛异常，
/// 而不是悄悄退回整屏。退回整屏会让所有 <c>InsideWindow</c> 恒为 true，
/// 合成层目标就被当成可点目标了 —— 那正是"点了没反应"这类故障的起点。
/// </para>
/// </summary>
[TestClass]
public sealed class ScreenAnalyzerWindowResolutionTests
{
    /// <summary>
    /// 不存在的进程 → 必须抛异常，不能返回一个"整屏都是有效区"的分析结果。
    /// </summary>
    [TestMethod]
    public void AnalyzeForProcess_UnknownProcess_ThrowsInsteadOfFallingBackToFullScreen()
    {
        const int impossibleProcessId = int.MaxValue;

        var ex = Assert.ThrowsExactly<InvalidOperationException>(
            () => ComputerScreenAnalyzer.AnalyzeForProcess(impossibleProcessId));

        StringAssert.Contains(ex.Message, impossibleProcessId.ToString(),
            "异常消息里要带上进程 ID，否则排查时不知道是哪个目标没窗口");
    }

    /// <summary>
    /// 显式传 referenceWindow 的旧路径不应臆造一个"已解析窗口"，
    /// 否则调用方无法区分"我自己指的"和"系统替我选的"。
    /// </summary>
    [TestMethod]
    public void AnalyzeWithExplicitWindow_LeavesResolvedWindowNull()
    {
        using var capture = WindowsScreenCapturer.CaptureVirtualScreen();
        using var analysis = ComputerScreenAnalyzer.Analyze(
            capture, new PhysicalRect(0, 0, 800, 600));

        Assert.IsNull(analysis.ResolvedWindow,
            "调用方自己指定窗口时，ResolvedWindow 必须是 null —— " +
            "把它填上会让「谁选了窗口」这个事实变模糊");
    }

    /// <summary>
    /// 显式窗口仍是权威：分析结果的 Targets 必须按传入矩形判定 InsideWindow。
    /// 这里用一个远离屏幕的矩形，所有检测结果都应被标为窗口外。
    /// </summary>
    [TestMethod]
    public void AnalyzeWithExplicitWindow_MarksTargetsOutsideThatWindow()
    {
        using var capture = WindowsScreenCapturer.CaptureVirtualScreen();
        using var analysis = ComputerScreenAnalyzer.Analyze(
            capture, new PhysicalRect(-50_000, -50_000, 10, 10));

        if (analysis.Targets.Count == 0)
        {
            Assert.Inconclusive("本次截图没有检测到任何目标，无法验证 InsideWindow 判定");
            return;
        }

        Assert.IsTrue(analysis.Targets.All(t => !t.InsideWindow),
            "参考窗口在屏幕外时，所有目标都应判为窗口外");
    }
}
