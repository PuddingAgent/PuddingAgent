using Pudding.Contracts.Computer;
using PuddingComputer.Windows;

namespace PuddingComputer.WindowsTests;

/// <summary>
/// <see cref="ComputerScreenAnalyzer.IsCenterInside"/> 的边界条件。
/// <para>
/// 这个函数决定「按进程分析时哪些检出结果算数」。判错的方向有两种，代价不同：
/// </para>
/// <list type="bullet">
/// <item>误收（把窗口外的算进来）→ 目标表里混入别的应用的元素，噪声爆炸。
///   实测：不过滤时 169 个目标全部来自同桌面的另一个应用。</item>
/// <item>误排（把窗口内的算出去）→ 真目标被丢掉，表现为「分析到了什么也点不了」。</item>
/// </list>
/// </summary>
[TestClass]
public sealed class CenterInsideWindowTests
{
    private static readonly PhysicalRect Window = new(3292, 106, 1600, 1038);

    /// <summary>完全落在窗口中央的框 → 收。</summary>
    [TestMethod]
    public void DetectionWellInsideWindow_IsAccepted()
    {
        var rect = new PhysicalRect(3800, 500, 100, 40);
        Assert.IsTrue(ComputerScreenAnalyzer.IsCenterInside(rect, Window));
    }

    /// <summary>完全在窗口外（左侧别的显示器）→ 排除。</summary>
    [TestMethod]
    public void DetectionOnAnotherMonitor_IsRejected()
    {
        var rect = new PhysicalRect(998, 1291, 100, 40);
        Assert.IsFalse(ComputerScreenAnalyzer.IsCenterInside(rect, Window),
            "同桌面上另一个应用的元素必须被排除，否则目标表全是噪声");
    }

    /// <summary>中心在窗口左上角 → 收（上/左是闭区间）。</summary>
    [TestMethod]
    public void CenterExactlyOnTopLeftEdge_IsAccepted()
    {
        // 中心要落在 (3292,106)：矩形 100x40 → 左上角 (3242,86)
        var rect = new PhysicalRect(3242, 86, 100, 40);
        Assert.IsTrue(ComputerScreenAnalyzer.IsCenterInside(rect, Window));
    }

    /// <summary>中心刚好在右下边界外 → 排除（右/下是开区间）。</summary>
    [TestMethod]
    public void CenterOnBottomRightEdge_IsRejected()
    {
        // 窗口右边界 3292+1600=4892，下边界 106+1038=1144
        var rect = new PhysicalRect(4892 - 50, 1144 - 20, 100, 40);
        Assert.IsFalse(ComputerScreenAnalyzer.IsCenterInside(rect, Window),
            "右下边界是开区间：中心正好压在边界上时，元素有一半在窗口外");
    }

    /// <summary>
    /// 跨边界但中心在内 → 收。这是刻意的：判据是中心而非相交，
    /// 只要可点位置在窗口内就还算可用目标。
    /// </summary>
    [TestMethod]
    public void StraddlingWindowEdgeWithCenterInside_IsAccepted()
    {
        // 矩形左半在窗外，但中心仍在窗内
        var rect = new PhysicalRect(3272, 500, 100, 40);   // 中心 x = 3322 ≥ 3292
        Assert.IsTrue(ComputerScreenAnalyzer.IsCenterInside(rect, Window));
    }

    /// <summary>
    /// 跨边界且中心在外 → 排除。半截身子在窗外的元素，其可点位置不可预测。
    /// </summary>
    [TestMethod]
    public void StraddlingWindowEdgeWithCenterOutside_IsRejected()
    {
        // 窗口左边界 3292。矩形 (3200,500,100,40) 的中心 = 3200+50 = 3250 < 3292 → 在外。
        // （之前这里写成 3242，中心刚好 3292 压在边界上，属于“中心在边界”应接受的情形 ——
        //   是测试算错，不是实现错，2026-11-03 修正。）
        var rect = new PhysicalRect(3200, 500, 100, 40);
        Assert.IsFalse(ComputerScreenAnalyzer.IsCenterInside(rect, Window));
    }
}
