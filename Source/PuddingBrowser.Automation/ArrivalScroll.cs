namespace PuddingBrowser.Automation;

/// <summary>滚动"到位"目标（`to_top`/`to_bottom` 的语义）。</summary>
public enum ArrivalTarget
{
    Top,
    Bottom,
}

/// <summary>到达滚动的一步决策。</summary>
public sealed record ArrivalStep(double Delta, bool ShouldContinue, string? StopReason)
{
    public static ArrivalStep Continue(double delta) => new(delta, true, null);

    public static ArrivalStep Stop(string reason) => new(0, false, reason);
}

/// <summary>
/// **"滚到顶/底"的收敛策略**（设计方案 §7/G8 的 `to_top`/`to_bottom`）。
///
/// 原生滚轮没有"滚到底"这种原子操作：只能按步送滚轮事件，并且**必须能停下来**。三个停止条件：
/// <list type="number">
///   <item><b>到限</b>：位置 + 视口 已经覆盖内容高度（或到顶后位置 ≤ 0）；</item>
///   <item><b>不动了</b>：这一轮位置与上一轮相同 —— 已经到底/被容器卡住，
///         继续送轮只会浪费预算，而且**把"没动"报告成"滚成功"正是 G8 禁止的**；</item>
///   <item><b>步数预算耗尽</b>：无限滚动页面永远滚不到底，必须有上限（否则一次交互就能把会话拖死）。</item>
/// </list>
///
/// 抽成纯函数的原因：这三条都能在进程内逐条验证（真机上只剩"浏览器是否真的移动"）。
/// </summary>
public static class ArrivalScroll
{
    /// <summary>单步滚轮位移（像素）。</summary>
    public const double DefaultStepPixels = 600;

    /// <summary>步数上限（无限滚动页面的保护）。</summary>
    public const int MaxSteps = 40;

    /// <summary>判定"位置没变"的容差（亚像素抖动不该被当成移动）。</summary>
    public const double PositionEpsilon = 0.5;

    /// <summary>判定"已到限"的容差。</summary>
    public const double LimitEpsilon = 1.0;

    /// <summary>
    /// 决定下一步：返回 <see cref="ArrivalStep.Continue"/> 或 <see cref="ArrivalStep.Stop"/>（含原因）。
    /// </summary>
    /// <param name="target">滚到顶还是滚到底。</param>
    /// <param name="position">当前滚动位置。</param>
    /// <param name="previousPosition">上一轮位置（第一轮传 <c>null</c>）。</param>
    /// <param name="viewportHeight">视口高度。</param>
    /// <param name="contentHeight">内容高度（<c>scrollHeight</c>）。</param>
    /// <param name="stepsTaken">已执行的步数。</param>
    /// <param name="stepPixels">单步位移。</param>
    public static ArrivalStep Plan(
        ArrivalTarget target,
        double position,
        double? previousPosition,
        double viewportHeight,
        double contentHeight,
        int stepsTaken,
        double stepPixels = DefaultStepPixels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(stepPixels);
        ArgumentOutOfRangeException.ThrowIfNegative(stepsTaken);

        if (contentHeight <= 0 || viewportHeight <= 0)
        {
            // 拿不到尺寸就不猜"到没到"：宁可让调用方如实报告失败。
            return ArrivalStep.Stop("the page did not report usable scroll geometry");
        }

        if (stepsTaken >= MaxSteps)
        {
            return ArrivalStep.Stop($"stopped after {MaxSteps} steps (the page may scroll forever)");
        }

        // 不动了：说明已经到头、或被容器/滚动链挡住。
        if (previousPosition is { } previous && Math.Abs(position - previous) <= PositionEpsilon)
        {
            return ArrivalStep.Stop("the scroll position stopped changing");
        }

        if (target == ArrivalTarget.Top)
        {
            if (position <= LimitEpsilon)
            {
                return ArrivalStep.Stop("already at the top");
            }

            return ArrivalStep.Continue(-stepPixels);
        }

        var maxPosition = Math.Max(0, contentHeight - viewportHeight);
        if (position >= maxPosition - LimitEpsilon)
        {
            return ArrivalStep.Stop("already at the bottom");
        }

        return ArrivalStep.Continue(stepPixels);
    }
}
