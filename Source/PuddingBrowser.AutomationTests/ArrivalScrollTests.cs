using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingBrowser.Automation;

namespace PuddingBrowser.AutomationTests;

/// <summary>
/// "滚到顶/滚到底"的收敛策略（§7 / G8）：必须能停，且**不得把"没动"当成滚成功**。
/// </summary>
[TestClass]
public sealed class ArrivalScrollTests
{
    private const double Viewport = 800;
    private const double Content = 5_000;

    [TestMethod]
    public void ToRefScrollsJustEnoughToBringTheElementIntoView()
    {
        // 元素在视口下方：向下滚"刚好够到"（top − viewport + margin），不超过单步上限。
        var below = ArrivalScroll.PlanToRef(
            elementTop: 900, elementBottom: 950, previousTop: null, viewportHeight: 800, stepsTaken: 0);

        Assert.IsTrue(below.ShouldContinue);
        Assert.AreEqual(900 - 800 + ArrivalScroll.DefaultRefMargin, below.Delta);

        // 元素很远时受单步上限约束。
        var far = ArrivalScroll.PlanToRef(
            elementTop: 5_000, elementBottom: 5_050, previousTop: null, viewportHeight: 800, stepsTaken: 0);
        Assert.AreEqual(ArrivalScroll.DefaultStepPixels, far.Delta);

        // 元素在视口上方：向上滚。
        var above = ArrivalScroll.PlanToRef(
            elementTop: -300, elementBottom: -250, previousTop: null, viewportHeight: 800, stepsTaken: 0);
        Assert.IsTrue(above.ShouldContinue);
        Assert.AreEqual(-(250 + ArrivalScroll.DefaultRefMargin), above.Delta);
    }

    [TestMethod]
    public void ToRefStopsWhenTheElementIsVisible()
    {
        var visible = ArrivalScroll.PlanToRef(
            elementTop: 100, elementBottom: 140, previousTop: null, viewportHeight: 800, stepsTaken: 0);

        Assert.IsFalse(visible.ShouldContinue);
        StringAssert.Contains(visible.StopReason, "already visible");

        // 元素比视口还高：顶边进入视口就算到位（再滚也装不下）。
        var taller = ArrivalScroll.PlanToRef(
            elementTop: 1, elementBottom: 5_000, previousTop: null, viewportHeight: 800, stepsTaken: 0);
        Assert.IsFalse(taller.ShouldContinue);
        StringAssert.Contains(taller.StopReason, "already visible");
    }

    [TestMethod]
    public void ToRefStopsWhenTheElementNeverMovesOrTheBudgetRunsOut()
    {
        // 元素不跟着动 ⇒ 它不在可滚动区域里（继续送轮没有意义）。
        var stuck = ArrivalScroll.PlanToRef(
            elementTop: 900, elementBottom: 950, previousTop: 900, viewportHeight: 800, stepsTaken: 1);

        Assert.IsFalse(stuck.ShouldContinue);
        StringAssert.Contains(stuck.StopReason, "did not move");

        var capped = ArrivalScroll.PlanToRef(
            elementTop: 900, elementBottom: 950, previousTop: null, viewportHeight: 800,
            stepsTaken: ArrivalScroll.MaxSteps);
        Assert.IsFalse(capped.ShouldContinue);
        StringAssert.Contains(capped.StopReason, "forever");

        // 拿不到视口高度就不猜。
        var noViewport = ArrivalScroll.PlanToRef(
            elementTop: 900, elementBottom: 950, previousTop: null, viewportHeight: 0, stepsTaken: 0);
        Assert.IsFalse(noViewport.ShouldContinue);
        StringAssert.Contains(noViewport.StopReason, "viewport");
    }

    [TestMethod]
    public void ScrollingDownContinuesUntilTheLimitIsCovered()
    {
        var next = ArrivalScroll.Plan(ArrivalTarget.Bottom, 1_000, previousPosition: null, Viewport, Content, stepsTaken: 0);

        Assert.IsTrue(next.ShouldContinue);
        Assert.AreEqual(ArrivalScroll.DefaultStepPixels, next.Delta);
        Assert.IsNull(next.StopReason);
    }

    [TestMethod]
    public void ScrollingUpUsesANegativeDeltaAndStopsAtTheTop()
    {
        Assert.AreEqual(-ArrivalScroll.DefaultStepPixels,
            ArrivalScroll.Plan(ArrivalTarget.Top, 1_000, 1_600, Viewport, Content, stepsTaken: 0).Delta);

        var atTop = ArrivalScroll.Plan(ArrivalTarget.Top, 0.2, 600, Viewport, Content, stepsTaken: 1);
        Assert.IsFalse(atTop.ShouldContinue);
        StringAssert.Contains(atTop.StopReason, "top");
    }

    [TestMethod]
    public void ReachingTheBottomStopsInsteadOfReportingSuccessForever()
    {
        // 内容 5000 − 视口 800 = 最大位置 4200。
        var atBottom = ArrivalScroll.Plan(ArrivalTarget.Bottom, 4_200, 3_600, Viewport, Content, stepsTaken: 5);

        Assert.IsFalse(atBottom.ShouldContinue);
        StringAssert.Contains(atBottom.StopReason, "bottom");
    }

    [TestMethod]
    public void AStalledPositionStopsTheScroll()
    {
        // G8：位置没变就必须停下 —— 继续送轮并回报"滚成功"是明确禁止的。
        var stalled = ArrivalScroll.Plan(ArrivalTarget.Bottom, 3_000, 3_000.2, Viewport, Content, stepsTaken: 3);

        Assert.IsFalse(stalled.ShouldContinue);
        StringAssert.Contains(stalled.StopReason, "stopped changing");
    }

    [TestMethod]
    public void TheStepBudgetProtectsAgainstEndlessScrolling()
    {
        // 无限滚动页面：位置一直在变、内容高度也在长（因此到限判定不该先命中），步数必须封顶。
        const double growingContent = 10_000_000;

        var capped = ArrivalScroll.Plan(
            ArrivalTarget.Bottom, 100_000, 99_000, Viewport, growingContent, stepsTaken: ArrivalScroll.MaxSteps);

        Assert.IsFalse(capped.ShouldContinue);
        StringAssert.Contains(capped.StopReason, "forever");

        // 上限前一步仍可继续。
        Assert.IsTrue(ArrivalScroll.Plan(
            ArrivalTarget.Bottom, 100_000, 99_000, Viewport, growingContent, stepsTaken: ArrivalScroll.MaxSteps - 1)
            .ShouldContinue);
    }

    [TestMethod]
    public void MissingGeometryIsRefusedInsteadOfGuessed()
    {
        var noContent = ArrivalScroll.Plan(ArrivalTarget.Bottom, 100, null, Viewport, contentHeight: 0, stepsTaken: 0);
        Assert.IsFalse(noContent.ShouldContinue);
        StringAssert.Contains(noContent.StopReason, "geometry");

        var noViewport = ArrivalScroll.Plan(ArrivalTarget.Bottom, 100, null, viewportHeight: 0, Content, stepsTaken: 0);
        Assert.IsFalse(noViewport.ShouldContinue);
    }

    [TestMethod]
    public void ContentShorterThanTheViewportIsAlreadyAtBothEnds()
    {
        // 不需要滚动：位置 0，内容比视口还矮。
        var bottom = ArrivalScroll.Plan(ArrivalTarget.Bottom, 0, null, Viewport, contentHeight: 500, stepsTaken: 0);
        Assert.IsFalse(bottom.ShouldContinue);
        StringAssert.Contains(bottom.StopReason, "bottom");

        var top = ArrivalScroll.Plan(ArrivalTarget.Top, 0, null, Viewport, contentHeight: 500, stepsTaken: 0);
        Assert.IsFalse(top.ShouldContinue);
    }

    [TestMethod]
    public void InvalidStepInputsAreRefused()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => ArrivalScroll.Plan(ArrivalTarget.Bottom, 0, null, Viewport, Content, stepsTaken: 0, stepPixels: 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => ArrivalScroll.Plan(ArrivalTarget.Bottom, 0, null, Viewport, Content, stepsTaken: -1));
    }
}
