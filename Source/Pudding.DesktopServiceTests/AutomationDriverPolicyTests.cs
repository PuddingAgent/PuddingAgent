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
    public void TheNullPolicyRefusesArrivalScrolling()
    {
        // 没有策略注入的表面：明确"不支持"（null），而不是假装能滚。
        Assert.Null(NullBrowserDriverPolicy.Instance.PlanArrivalStep(
            toBottom: true, position: 0, previousPosition: null,
            viewportHeight: 800, contentHeight: 5_000, stepsTaken: 0));
    }
}
