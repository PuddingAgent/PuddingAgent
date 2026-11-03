using PuddingComputer.Windows;

[assembly: Parallelize(Scope = ExecutionScope.MethodLevel)]

namespace PuddingComputer.WindowsTests;

/// <summary>
/// <see cref="WindowsInteractiveWindowFinder.ScoreFor"/> 的边界测试。
/// <para>
/// 验证仍被 FindBest 消费的亮度、颜色与面积评分。固定样本只锁定分数关系，
/// 不证明真实窗口的遮挡状态、输入接收关系或点击效果。
/// </para>
/// </summary>
[TestClass]
public sealed class InteractiveWindowScoreTests
{
    /// <summary>
    /// 真窗口（白底界面、颜色少）必须显著高于混合矩形（含大量壁纸色彩）。
    /// </summary>
    [TestMethod]
    public void RealUiWindow_ScoresHigherThan_WallpaperMixedRect()
    {
        // 真窗口：接近纯白，颜色种类少
        double real = WindowsInteractiveWindowFinder.ScoreFor(247.2, 22, 1600, 1038);

        // 混合矩形：亮度被壁纸拉低，颜色种类多
        double mixed = WindowsInteractiveWindowFinder.ScoreFor(179.4, 90, 1183, 1444);

        Assert.IsGreaterThan(mixed, real,
            $"真窗口得分 {real:F3} 应高于混合矩形 {mixed:F3}");
    }

    /// <summary>
    /// 纯白窗口应接近满分 1.0。
    /// </summary>
    [TestMethod]
    public void PureWhiteWindow_ScoresNearOne()
    {
        double score = WindowsInteractiveWindowFinder.ScoreFor(255, 1, 1920, 1080);
        Assert.IsGreaterThan(0.99, score, $"纯白满屏窗口应接近 1.0，实际 {score:F3}");
    }

    /// <summary>
    /// 纯黑窗口也应高分 —— 深色主题界面同样是"界面底色"，不能因为暗就判负。
    /// </summary>
    [TestMethod]
    public void PureBlackWindow_AlsoScoresHigh()
    {
        double score = WindowsInteractiveWindowFinder.ScoreFor(5, 1, 1920, 1080);
        Assert.IsGreaterThan(0.95, score, $"纯黑深色界面应高分，实际 {score:F3}");
    }

    /// <summary>
    /// 颜色种类越多，分数越低（壁纸/照片内容的直接信号）。
    /// </summary>
    [TestMethod]
    public void MoreColors_LowersScore_Monotonically()
    {
        double few = WindowsInteractiveWindowFinder.ScoreFor(250, 10, 1600, 1000);
        double some = WindowsInteractiveWindowFinder.ScoreFor(250, 60, 1600, 1000);
        double many = WindowsInteractiveWindowFinder.ScoreFor(250, 160, 1600, 1000);

        Assert.IsGreaterThan(some, few, "颜色多应该扣分");
        Assert.IsGreaterThan(many, some, "颜色更多应该扣更多分");
    }

    /// <summary>
    /// 相同内容下，大窗口应略高于小窗口（面积奖），但幅度不能逆转颜色判据。
    /// </summary>
    [TestMethod]
    public void LargerWindow_SlightlyBetter_ButColorDominates()
    {
        double small = WindowsInteractiveWindowFinder.ScoreFor(250, 20, 800, 600);
        double large = WindowsInteractiveWindowFinder.ScoreFor(250, 20, 1920, 1080);

        // 大一点，但差距小于 45%（0.7 + 0.3 = 1.0 vs 0.7 + 少量）
        Assert.IsGreaterThanOrEqualTo(small, large, $"大窗口 {large:F3} 应 ≥ 小窗口 {small:F3}");
        Assert.IsLessThan(1.45, large / small, "面积奖不应逆转颜色判据");

        // 但颜色很差的小窗口，不能靠面积翻盘
        double cleanSmall = WindowsInteractiveWindowFinder.ScoreFor(250, 10, 800, 600);
        double dirtyLarge = WindowsInteractiveWindowFinder.ScoreFor(250, 180, 1920, 1080);
        Assert.IsGreaterThan(dirtyLarge, cleanSmall, "干净的小窗口应胜过脏的大窗口");
    }

    /// <summary>
    /// 分数必须落在 0..1，不得溢出。
    /// </summary>
    [TestMethod]
    public void Score_AlwaysInUnitRange()
    {
        foreach (var (lum, buckets, w, h) in new[]
        {
            (0.0, 0, 100, 100),
            (255.0, 0, 4000, 4000),
            (128.0, 200, 1920, 1080),
            (200.0, 500, 5000, 5000),
        })
        {
            double s = WindowsInteractiveWindowFinder.ScoreFor(lum, buckets, w, h);
            Assert.IsGreaterThanOrEqualTo(0.0, s, $"({lum},{buckets},{w},{h}) 越界为 {s}");
            Assert.IsLessThanOrEqualTo(1.0, s, $"({lum},{buckets},{w},{h}) 越界为 {s}");
        }
    }
}
