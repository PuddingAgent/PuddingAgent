using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingBrowser.Automation;

namespace PuddingBrowser.AutomationTests;

/// <summary>
/// 低细节档的**质量阶梯**：detail=low 承诺 512px/40 KiB，而这个承诺此前**不可满足**——
/// quality 固定 80，密集页面（抖音搜索结果实测 43502 B / 56061 B）必然撞预算，
/// 于是 detail=low 在真实页面上有一半时间拿不到图。
///
/// 这里锁定的是"按结果选画质"这条判断本身，而不是 WebView2 的拍摄：
/// ① 只降不升（调用方要 q50 时不许偷偷先用 q80 拍）；
/// ② 第一个满足预算的档位胜出，不多拍；
/// ③ 降到底仍超就把**最小**的一次交回去并标未达预算——不截断、不冒充可读。
/// </summary>
[TestClass]
public sealed class ScreenshotQualityLadderTests
{
    [TestMethod]
    public void StepsDescendFromTheRequestedStartOnly()
    {
        CollectionAssert.AreEqual(
            new[] { 80, 65, 50, ScreenshotPlan.MinimumQuality },
            ScreenshotPlan.QualitySteps(80).ToArray());
    }

    [TestMethod]
    public void StepsKeepTheStartAndNeverExceedIt()
    {
        // 起点更高 ⇒ 可选档更多，但第一档永远是调用方给的起点。
        CollectionAssert.AreEqual(
            new[] { 92, 80, 65, 50, ScreenshotPlan.MinimumQuality },
            ScreenshotPlan.QualitySteps(92).ToArray());

        // 起点低于某档 ⇒ 那一档不会被"升"上来。
        CollectionAssert.AreEqual(
            new[] { 50, ScreenshotPlan.MinimumQuality },
            ScreenshotPlan.QualitySteps(50).ToArray());

        // 起点已经在下限以下 ⇒ 只剩它自己，不做任何牺牲。
        CollectionAssert.AreEqual(new[] { 30 }, ScreenshotPlan.QualitySteps(30).ToArray());
    }

    [TestMethod]
    public void StepsRejectOutOfRangeQuality()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => ScreenshotPlan.QualitySteps(0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => ScreenshotPlan.QualitySteps(101));
    }

    [TestMethod]
    public async Task FirstStepThatFitsWinsAndNoFurtherCaptureHappens()
    {
        List<int> captured = [];
        var steps = ScreenshotPlan.QualitySteps(80);

        // q80 超、q65 过：必须停在 q65，不能再拍 q50/q38。
        var result = await ScreenshotPlan.CaptureWithinBudgetAsync(
            steps,
            maxBytes: 40_000,
            (quality, _) =>
            {
                captured.Add(quality);
                return Task.FromResult(new byte[quality == 65 ? 30_000 : 56_061]);
            },
            CancellationToken.None);

        Assert.IsTrue(result.WithinBudget);
        Assert.AreEqual(65, result.Quality);
        Assert.AreEqual(30_000, result.Bytes.Length);
        CollectionAssert.AreEqual(new[] { 80, 65 }, captured);
        CollectionAssert.AreEqual(new[] { 56_061, 30_000 }, result.AttemptByteCounts.ToArray());
    }

    [TestMethod]
    public async Task WithoutABudgetOnlyTheFirstStepIsCaptured()
    {
        List<int> captured = [];

        var result = await ScreenshotPlan.CaptureWithinBudgetAsync(
            ScreenshotPlan.QualitySteps(80),
            maxBytes: null,
            (quality, _) =>
            {
                captured.Add(quality);
                return Task.FromResult(new byte[999_999]);
            },
            CancellationToken.None);

        Assert.IsTrue(result.WithinBudget);
        Assert.AreEqual(80, result.Quality);
        CollectionAssert.AreEqual(new[] { 80 }, captured);
    }

    [TestMethod]
    public async Task WhenEveryStepExceedsTheBudgetTheSmallestCaptureIsReturnedAndFlagged()
    {
        List<int> captured = [];

        // 每档都超预算，且体积随档位单调下降：交回的必须是 q38 那张，并且明确标注未达预算。
        // 基准偏移量让**每一个**档位都真的超过 40000（q38*1000 = 38000 会意外装进去，
        // 那样测的就不再是"全过不了"这条路径）。
        var result = await ScreenshotPlan.CaptureWithinBudgetAsync(
            ScreenshotPlan.QualitySteps(80),
            maxBytes: 40_000,
            (quality, _) =>
            {
                captured.Add(quality);
                return Task.FromResult(new byte[(quality * 1_000) + 10_000]);
            },
            CancellationToken.None);

        Assert.IsFalse(result.WithinBudget);
        Assert.AreEqual(ScreenshotPlan.MinimumQuality, result.Quality);
        Assert.AreEqual((ScreenshotPlan.MinimumQuality * 1_000) + 10_000, result.Bytes.Length);
        CollectionAssert.AreEqual(new[] { 80, 65, 50, ScreenshotPlan.MinimumQuality }, captured);
    }

    [TestMethod]
    public async Task EmptyLadderIsRejected()
    {
        await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            ScreenshotPlan.CaptureWithinBudgetAsync(
                [],
                maxBytes: 1,
                (_, _) => Task.FromResult(Array.Empty<byte>()),
                CancellationToken.None));
    }
}
