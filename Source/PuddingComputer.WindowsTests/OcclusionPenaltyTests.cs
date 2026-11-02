using PuddingComputer.Windows;

namespace PuddingComputer.WindowsTests;

/// <summary>
/// <see cref="WindowsInteractiveWindowFinder.ApplyOcclusionPenalty"/> —— 窗口被遮挡时的降分。
/// <para>
/// 锁定的是一条实测教训（2026-11-03）：ChatGPT 进程同时暴露两个顶层窗口，
/// <c>FindBest</c> 按平均亮度选了亮度高（mean 248）的那个，
/// 但它在 z-order 底部、被完全遮挡，点击全被另一个接走 ——
/// 实测 120/202 个目标落点跑到了别的窗口上。
/// </para>
/// <para>
/// 修好后落点归属匹配率 40.6% → 100%。
/// </para>
/// </summary>
[TestClass]
public sealed class OcclusionPenaltyTests
{
    // 两个候选的实测参数（2026-11-03 当次）
    private const double ContentMean = 248.2;     // 0xCF0FCC 白底界面
    private const int ContentBuckets = 22;
    private const int ContentW = 1600, ContentH = 1038;

    private const double OverlayMean = 176.1;     // 0xE51328 含壁纸混入
    private const int OverlayBuckets = 53;
    private const int OverlayW = 1183, OverlayH = 1444;

    /// <summary>命中自己 → 分数不变。</summary>
    [TestMethod]
    public void CenterHitsSelf_NoPenalty()
    {
        double score = WindowsInteractiveWindowFinder.ScoreFor(ContentMean, ContentBuckets, ContentW, ContentH);

        double actual = WindowsInteractiveWindowFinder.ApplyOcclusionPenalty(
            score, centerHitSelf: true, centerHitNull: false);

        Assert.AreEqual(score, actual, 1e-9);
    }

    /// <summary>
    /// 命中 null → 不惩罚。合成层的画面在 D3D 层，Win32 命中测试本来就为空，
    /// 那类目标靠 <c>allowWithoutWindow</c> 处理，不能因此被当成"被遮挡"。
    /// </summary>
    [TestMethod]
    public void CenterHitsNull_NoPenalty()
    {
        double score = 0.8;

        double actual = WindowsInteractiveWindowFinder.ApplyOcclusionPenalty(
            score, centerHitSelf: false, centerHitNull: true);

        Assert.AreEqual(score, actual, 1e-9);
    }

    /// <summary>命中别的窗口 → 乘衰减系数。</summary>
    [TestMethod]
    public void CenterHitsAnotherWindow_Penalized()
    {
        const double score = 0.8;

        double actual = WindowsInteractiveWindowFinder.ApplyOcclusionPenalty(
            score, centerHitSelf: false, centerHitNull: false);

        Assert.AreEqual(score * WindowsInteractiveWindowFinder.OcclusionPenaltyFactor, actual, 1e-9);
        Assert.IsLessThan(score, actual, "必须真的降分，不能只是个摆设");
    }

    /// <summary>
    /// **核心用例**：用当晚真实参数复算，可点的暗窗口必须胜过被遮挡的亮窗口。
    /// <para>
    /// 若不施加遮挡惩罚，亮度高的 0xCF0FCC 会赢（分数 0.814 &gt; 0.215），
    /// 而这正是当时实际发生的错误选择。这条断言把"为什么选错了"钉成数字。
    /// </para>
    /// </summary>
    [TestMethod]
    public void ClickableDimWindow_BeatsOccludedBrightWindow()
    {
        double brightOccluded = WindowsInteractiveWindowFinder.ScoreFor(
            ContentMean, ContentBuckets, ContentW, ContentH);
        double dimClickable = WindowsInteractiveWindowFinder.ScoreFor(
            OverlayMean, OverlayBuckets, OverlayW, OverlayH);

        // 先确认原始分数确实是"亮的高"（否则这条测试就失去意义）
        Assert.IsGreaterThan(dimClickable, brightOccluded,
            $"前提：不惩罚时亮的更高（{brightOccluded:F3} > {dimClickable:F3}）");

        double afterPenalty = WindowsInteractiveWindowFinder.ApplyOcclusionPenalty(
            brightOccluded, centerHitSelf: false, centerHitNull: false);

        Assert.IsGreaterThan(
            lowerBound: afterPenalty,
            value: dimClickable,
            message: $"施加遮挡惩罚后，可点的暗窗口（{dimClickable:F3}）应胜过被遮挡的亮窗口（{afterPenalty:F3}）");
    }

    /// <summary>
    /// 衰减系数必须落在 (0,1)：为 0 会让"所有候选都被盖住"时直接抛异常，
    /// 为 1 则等于没惩罚。
    /// </summary>
    [TestMethod]
    public void PenaltyFactor_IsStrictlyBetweenZeroAndOne()
    {
        Assert.IsGreaterThan(0.0, WindowsInteractiveWindowFinder.OcclusionPenaltyFactor);
        Assert.IsLessThan(1.0, WindowsInteractiveWindowFinder.OcclusionPenaltyFactor);
    }
}
