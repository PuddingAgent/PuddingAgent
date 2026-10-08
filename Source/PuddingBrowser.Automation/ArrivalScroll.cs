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
    /// <summary>
    /// **把参考元素滚进视口**（`to_ref`）的一步决策。
    ///
    /// 与滚到顶/底的区别：目标不是一个"极限位置"，而是"某个元素可见"。因此判定基于元素相对视口的
    /// 上下边（`getBoundingClientRect` 的 top/bottom）：
    /// <list type="bullet">
    ///   <item><b>已经可见</b> ⇒ 停（完全可见；或元素本身比视口还高时"顶边已进入视口"）；</item>
    ///   <item><b>在视口上方/下方</b> ⇒ 朝该方向滚"刚好够到"的距离（受单步上限约束）；</item>
    ///   <item><b>位置不再变化</b> ⇒ 停（元素不在任何可滚动容器里、或被固定层挡住）；</item>
    ///   <item><b>步数封顶</b> ⇒ 停。</item>
    /// </list>
    /// </summary>
    /// <param name="elementTop">元素上边相对视口顶部的偏移（负值表示在视口上方）。</param>
    /// <param name="elementBottom">元素下边相对视口顶部的偏移。</param>
    /// <param name="previousTop">上一轮的上边偏移（第一轮传 <c>null</c>）。</param>
    /// <param name="viewportHeight">视口高度。</param>
    /// <param name="stepsTaken">已执行步数。</param>
    /// <param name="stepPixels">单步位移上限。</param>
    /// <param name="margin">"刚好够到"时预留的边距（避免元素贴着边缘）。</param>
    public static ArrivalStep PlanToRef(
        double elementTop,
        double elementBottom,
        double? previousTop,
        double viewportHeight,
        int stepsTaken,
        double stepPixels = DefaultStepPixels,
        double margin = DefaultRefMargin)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(stepPixels);
        ArgumentOutOfRangeException.ThrowIfNegative(stepsTaken);
        ArgumentOutOfRangeException.ThrowIfNegative(margin);

        if (viewportHeight <= 0)
        {
            return ArrivalStep.Stop("the page did not report a usable viewport height");
        }

        if (stepsTaken >= MaxSteps)
        {
            return ArrivalStep.Stop($"stopped after {MaxSteps} steps (the page may scroll forever)");
        }

        if (previousTop is { } previous && Math.Abs(elementTop - previous) <= PositionEpsilon)
        {
            // 元素没有跟着动 ⇒ 它不在可滚动区域里（或被固定/覆盖层挡住）：继续送轮没有意义。
            return ArrivalStep.Stop("the target element did not move while scrolling");
        }

        var fullyVisible = elementTop >= -PositionEpsilon && elementBottom <= viewportHeight + PositionEpsilon;
        var tallerThanViewport = (elementBottom - elementTop) > viewportHeight;

        if (fullyVisible || (tallerThanViewport && elementTop >= -PositionEpsilon))
        {
            return ArrivalStep.Stop("the target element is already visible");
        }

        if (elementBottom < 0)
        {
            // 在视口上方：向上滚"刚好够到"（元素下边回到视口内 + 边距）。
            var distance = -elementBottom + margin;
            return ArrivalStep.Continue(-Math.Min(stepPixels, distance));
        }

        // 在视口下方：向下滚"刚好够到"（元素上边进入视口 + 边距）。
        var downDistance = elementTop - viewportHeight + margin;
        return ArrivalStep.Continue(Math.Min(stepPixels, downDistance));
    }

    /// <summary>"刚好够到"时预留的边距（像素）。</summary>
    public const double DefaultRefMargin = 8;

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
