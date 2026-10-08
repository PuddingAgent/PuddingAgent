using Pudding.DesktopService.Browser;
using PuddingBrowser.Abstractions;

namespace DesktopServiceTests;

/// <summary>
/// 驱动策略适配器（把 Automation 的纯策略接进 seam）：到达滚动的一步决策必须与
/// <c>ArrivalScroll</c> 的判定一致 —— 适配器只做翻译，不做判断。
/// </summary>
public sealed class AutomationDriverPolicyTests
{
    private static readonly IBrowserDriverPolicy Policy = AutomationDriverPolicy.Instance;

    [Fact]
    public void ArrivalStepDelegatesToThePurePolicy()
    {
        // 中段向下：继续，位移为正。
        var down = Policy.PlanArrivalStep(
            toBottom: true, position: 1_000, previousPosition: null,
            viewportHeight: 800, contentHeight: 5_000, stepsTaken: 0);

        Assert.NotNull(down);
        Assert.True(down!.Value.ShouldContinue);
        Assert.True(down.Value.Delta > 0);
        Assert.Null(down.Value.StopReason);

        // 中段向上：位移为负。
        var up = Policy.PlanArrivalStep(
            toBottom: false, position: 1_000, previousPosition: 1_600,
            viewportHeight: 800, contentHeight: 5_000, stepsTaken: 0);

        Assert.NotNull(up);
        Assert.True(up!.Value.Delta < 0);
    }

    [Fact]
    public void ArrivalStepStopsWithAReasonInsteadOfScrollingForever()
    {
        // 位置没变 ⇒ 停（G8：零位移不能假称滚动成功）。
        var stalled = Policy.PlanArrivalStep(
            toBottom: true, position: 3_000, previousPosition: 3_000,
            viewportHeight: 800, contentHeight: 5_000, stepsTaken: 2);

        Assert.NotNull(stalled);
        Assert.False(stalled!.Value.ShouldContinue);
        Assert.Contains("stopped changing", stalled.Value.StopReason);

        // 已在顶部。
        var atTop = Policy.PlanArrivalStep(
            toBottom: false, position: 0, previousPosition: null,
            viewportHeight: 800, contentHeight: 5_000, stepsTaken: 0);

        Assert.False(atTop!.Value.ShouldContinue);
        Assert.Contains("top", atTop.Value.StopReason);
    }

    [Fact]
    public void ToRefStepDelegatesToThePurePolicy()
    {
        // 元素在视口下方 ⇒ 继续（正位移）；已可见 ⇒ 停。
        var below = Policy.PlanToRefStep(
            elementTop: 900, elementBottom: 950, previousTop: null, viewportHeight: 800, stepsTaken: 0);

        Assert.NotNull(below);
        Assert.True(below!.Value.ShouldContinue);
        Assert.True(below.Value.Delta > 0);

        var visible = Policy.PlanToRefStep(
            elementTop: 100, elementBottom: 140, previousTop: null, viewportHeight: 800, stepsTaken: 0);

        Assert.False(visible!.Value.ShouldContinue);
        Assert.Contains("already visible", visible.Value.StopReason);

        // 元素在视口上方 ⇒ 负位移。
        var above = Policy.PlanToRefStep(
            elementTop: -300, elementBottom: -250, previousTop: null, viewportHeight: 800, stepsTaken: 0);
        Assert.True(above!.Value.Delta < 0);
    }

    [Fact]
    public void TheNullPolicyRefusesArrivalScrolling()
    {
        // 没有策略注入的表面：明确"不支持"（null），而不是假装能滚。
        Assert.Null(NullBrowserDriverPolicy.Instance.PlanArrivalStep(
            toBottom: true, position: 0, previousPosition: null,
            viewportHeight: 800, contentHeight: 5_000, stepsTaken: 0));

        Assert.Null(NullBrowserDriverPolicy.Instance.PlanToRefStep(
            elementTop: 900, elementBottom: 950, previousTop: null, viewportHeight: 800, stepsTaken: 0));
    }
}
