namespace PuddingComputer.UIAutomation;

/// <summary>
/// 无障碍树的「预热」判据。
/// <para>
/// 实测（2026-11-04）：Chromium 系窗口的 UIA 树是**增量构建**的。同一次运行里，
/// 第一个调用者可能只看到根的 0~1 个子节点，几百毫秒后才增长到完整（实测 59 → 78）。
/// 因此「读到的节点很少」不等于「该应用没有无障碍」——也可能是**读得太早**。
/// </para>
/// <para>
/// 这条规则只决定「还要不要再等一轮」，不决定最终可用性判定：
/// 预热窗口一旦用尽就照常采集并按实际结果分类，绝不把等待当作成功。
/// </para>
/// </summary>
public static class UiaStabilization
{
    /// <summary>预热窗口上限。取小值是为了让「本来就只有一个面板」的应用不被无谓拖慢。</summary>
    public static readonly TimeSpan DefaultWarmupWindow = TimeSpan.FromMilliseconds(800);

    /// <summary>两次探测之间的间隔。</summary>
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// 是否应继续等待树出现子节点。
    /// </summary>
    /// <param name="childrenSeen">最近一次探测读到的子节点数。</param>
    /// <param name="elapsedInWarmup">已等待时长。</param>
    /// <param name="warmupWindow">预热窗口上限。</param>
    /// <returns>true 表示「再等一轮」；已经读到子节点或窗口用尽时返回 false。</returns>
    public static bool ShouldKeepWarming(int childrenSeen, TimeSpan elapsedInWarmup, TimeSpan warmupWindow)
    {
        if (childrenSeen > 0)
            return false;

        if (warmupWindow <= TimeSpan.Zero)
            return false;

        return elapsedInWarmup < warmupWindow;
    }

    /// <summary>把总超时与预热上限取小，避免预热吃掉整次采集的预算。</summary>
    public static TimeSpan EffectiveWarmup(TimeSpan captureTimeout, TimeSpan configured)
    {
        if (configured <= TimeSpan.Zero)
            return TimeSpan.Zero;

        return captureTimeout <= configured ? captureTimeout : configured;
    }
}
