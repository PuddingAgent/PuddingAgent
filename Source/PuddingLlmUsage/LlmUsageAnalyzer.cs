using PuddingLlmUsage.Contracts;

namespace PuddingLlmUsage;

/// <summary>
/// 把余额观测序列还原成「消耗区间」。纯函数：不发请求、不读时钟、不碰存储。
/// <para>
/// <b>判定顺序是有意的</b>（每一层都因为下一层不可靠）：
/// <list type="number">
/// <item><description>端点失败 ⇒ <see cref="LlmUsageIntervalKind.Unavailable"/>。没有数值就无法算差。</description></item>
/// <item><description>间隔超阈值 ⇒ <see cref="LlmUsageIntervalKind.Gap"/>。缺口内可能发生过未被采到的充值，
/// 此时即便余额下降，也不能断言这就是消耗。</description></item>
/// <item><description>余额上升 ⇒ <see cref="LlmUsageIntervalKind.Recharge"/>。充值金额未知，
/// 差值只能说明「充了至少这么多」，<b>不能反推消耗</b>。</description></item>
/// <item><description>余额持平 ⇒ <see cref="LlmUsageIntervalKind.NoChange"/>。这是事实陈述，不是「没花钱」的证明。</description></item>
/// <item><description>余额下降 ⇒ <see cref="LlmUsageIntervalKind.Consumption"/>。此时差值才是消耗。</description></item>
/// </list>
/// </para>
/// </summary>
public static class LlmUsageAnalyzer
{
    /// <summary>
    /// 按 (provider, currency) 分组、组内按时间排序后，逐对生成区间。
    /// 输入顺序无关；同一时刻的重复观测会生成时长为零的区间，交给同一套规则判定。
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="observations"/> 为 <c>null</c>。</exception>
    public static IReadOnlyList<LlmUsageInterval> Analyze(
        IEnumerable<LlmBalanceObservation> observations,
        LlmUsageAnalysisOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(observations);

        options ??= LlmUsageAnalysisOptions.Default;

        if (options.GapThreshold <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), options.GapThreshold, "GapThreshold 必须为正。");

        if (options.AmountTolerance < 0m)
            throw new ArgumentOutOfRangeException(nameof(options), options.AmountTolerance, "AmountTolerance 不得为负。");

        var result = new List<LlmUsageInterval>();

        var groups = observations
            .Where(o => o is not null)
            .GroupBy(o => (o.ProviderId, o.Currency), StringTupleComparer.Instance)
            .OrderBy(g => g.Key.ProviderId, StringComparer.Ordinal)
            .ThenBy(g => g.Key.Currency, StringComparer.Ordinal);

        foreach (var group in groups)
        {
            var ordered = group
                .OrderBy(o => o.ObservedAtUtc)
                .ToArray();

            for (var i = 1; i < ordered.Length; i++)
                result.Add(BuildInterval(ordered[i - 1], ordered[i], options));
        }

        return result;
    }

    private static LlmUsageInterval BuildInterval(
        LlmBalanceObservation from,
        LlmBalanceObservation to,
        LlmUsageAnalysisOptions options)
    {
        var duration = to.ObservedAtUtc - from.ObservedAtUtc;
        if (duration < TimeSpan.Zero)
            duration = TimeSpan.Zero;

        // ① 端点失败：没有数值，任何差值都是伪造。
        if (!from.Succeeded || !to.Succeeded)
        {
            var which = !from.Succeeded && !to.Succeeded ? "两端均采样失败"
                : !from.Succeeded ? "起点采样失败"
                : "终点采样失败";
            var detail = (!from.Succeeded ? from.Error : null) ?? (!to.Succeeded ? to.Error : null);
            return new LlmUsageInterval(
                from.ProviderId, from.Currency, from.ObservedAtUtc, to.ObservedAtUtc, duration,
                BalanceDelta: null,
                Consumption: null,
                LlmUsageIntervalKind.Unavailable,
                detail is null ? which : $"{which}：{detail}");
        }

        var delta = to.TotalBalance!.Value - from.TotalBalance!.Value;

        // ② 缺口：中间可能漏采到充值，差值不可信。
        if (duration > options.GapThreshold)
        {
            return new LlmUsageInterval(
                from.ProviderId, from.Currency, from.ObservedAtUtc, to.ObservedAtUtc, duration,
                delta,
                Consumption: null,
                LlmUsageIntervalKind.Gap,
                $"间隔 {duration.TotalMinutes:F1} 分钟超过阈值 {options.GapThreshold.TotalMinutes:F1} 分钟，"
                + "中间可能发生过未被采到的充值，不做消耗推断。");
        }

        // ③ 充值：余额上升只说明「充了多少」，不说明「花了多少」。
        if (delta > options.AmountTolerance)
        {
            return new LlmUsageInterval(
                from.ProviderId, from.Currency, from.ObservedAtUtc, to.ObservedAtUtc, duration,
                delta,
                Consumption: null,
                LlmUsageIntervalKind.Recharge,
                $"余额上升 {delta}，判定为充值/调整；充值金额未知，本区间不推算消耗。");
        }

        // ④ 持平：这是观测事实，不能升级成「没花钱」的结论。
        if (delta >= -options.AmountTolerance)
        {
            return new LlmUsageInterval(
                from.ProviderId, from.Currency, from.ObservedAtUtc, to.ObservedAtUtc, duration,
                delta,
                Consumption: 0m,
                LlmUsageIntervalKind.NoChange,
                "余额持平：只能说明这两个时点之间余额没变，不等于期间没有调用。");
        }

        // ⑤ 真实消耗。
        return new LlmUsageInterval(
            from.ProviderId, from.Currency, from.ObservedAtUtc, to.ObservedAtUtc, duration,
            delta,
            Consumption: -delta,
            LlmUsageIntervalKind.Consumption,
            $"余额下降 {-delta}，判定为消耗。");
    }

    /// <summary>
    /// 汇总落在 [<paramref name="fromUtc"/>, <paramref name="toUtc"/>] **之内**的区间。
    /// <para>
    /// 只计完全落在窗口内的区间：跨越窗口边界的区间若被裁一半，会把消耗摊成虚假的精确值。
    /// 这种区间被排除且不影响 <see cref="LlmUsageSummary.IntervalCount"/> 之外的任何分母。
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="intervals"/> 为 <c>null</c>。</exception>
    /// <exception cref="ArgumentException"><paramref name="toUtc"/> 早于 <paramref name="fromUtc"/>。</exception>
    public static IReadOnlyList<LlmUsageSummary> Summarize(
        IEnumerable<LlmUsageInterval> intervals,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc)
    {
        ArgumentNullException.ThrowIfNull(intervals);

        if (toUtc < fromUtc)
            throw new ArgumentException("toUtc 不得早于 fromUtc。", nameof(toUtc));

        var inWindow = intervals
            .Where(i => i is not null)
            .Where(i => i.FromUtc >= fromUtc && i.ToUtc <= toUtc)
            .GroupBy(i => (i.ProviderId, i.Currency), StringTupleComparer.Instance)
            .OrderBy(g => g.Key.ProviderId, StringComparer.Ordinal)
            .ThenBy(g => g.Key.Currency, StringComparer.Ordinal);

        var summaries = new List<LlmUsageSummary>();

        foreach (var group in inWindow)
        {
            var list = group.ToArray();
            var decidable = list.Where(i => i.Consumption is not null).ToArray();

            var total = decidable.Length == 0
                ? (decimal?)null
                : decidable.Sum(i => i.Consumption!.Value);

            var decidableDuration = TimeSpan.FromTicks(decidable.Sum(i => i.Duration.Ticks));

            summaries.Add(new LlmUsageSummary(
                group.Key.ProviderId,
                group.Key.Currency,
                fromUtc,
                toUtc,
                IntervalCount: list.Length,
                DecidableIntervalCount: decidable.Length,
                RechargeCount: list.Count(i => i.Kind == LlmUsageIntervalKind.Recharge),
                GapCount: list.Count(i => i.Kind == LlmUsageIntervalKind.Gap),
                UnavailableCount: list.Count(i => i.Kind == LlmUsageIntervalKind.Unavailable),
                TotalConsumption: total,
                DecidableDuration: decidableDuration));
        }

        return summaries;
    }

    /// <summary>
    /// 分组键用值语义实现 <see cref="StringComparer.Ordinal"/> 比较，
    /// 避免默认字符串比较受区域性影响（币种/ProviderId 都是 ASCII 标识符）。
    /// </summary>
    private sealed class StringTupleComparer : IEqualityComparer<(string ProviderId, string Currency)>
    {
        public static readonly StringTupleComparer Instance = new();

        public bool Equals((string ProviderId, string Currency) x, (string ProviderId, string Currency) y) =>
            string.Equals(x.ProviderId, y.ProviderId, StringComparison.Ordinal)
            && string.Equals(x.Currency, y.Currency, StringComparison.Ordinal);

        public int GetHashCode((string ProviderId, string Currency) obj) =>
            HashCode.Combine(
                StringComparer.Ordinal.GetHashCode(obj.ProviderId),
                StringComparer.Ordinal.GetHashCode(obj.Currency));
    }
}
