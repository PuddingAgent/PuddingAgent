namespace PuddingLlmUsage.Contracts;

/// <summary>
/// 一次余额观测。既表示成功读取到的余额，也表示一次**失败的采样尝试**。
/// <para>
/// <b>为什么失败也要有位置</b>：若只记录成功样本，采样中断在曲线上会表现为「两点之间没有变化」，
/// 与「确实没花钱」无法区分 —— 这正是本仓库反复禁止的「空洞否定」。失败样本让缺口可见。
/// </para>
/// </summary>
/// <param name="ProviderId">服务商标识（与 <c>llm.providers.json</c> 的 providerId 一致）。</param>
/// <param name="Currency">币种（如 <c>CNY</c>）。失败样本也要求提供，否则无法归入曲线。</param>
/// <param name="TotalBalance">余额总额；失败样本为 <c>null</c>。</param>
/// <param name="GrantedBalance">其中赠送部分；服务商不给则为 <c>null</c>（<b>不是 0</b>）。</param>
/// <param name="ToppedUpBalance">其中充值部分；同上。</param>
/// <param name="ObservedAtUtc">观测时刻（UTC）。</param>
/// <param name="Succeeded">是否成功读到余额。</param>
/// <param name="Error">失败原因；成功时为 <c>null</c>。</param>
public sealed record LlmBalanceObservation(
    string ProviderId,
    string Currency,
    decimal? TotalBalance,
    decimal? GrantedBalance,
    decimal? ToppedUpBalance,
    DateTimeOffset ObservedAtUtc,
    bool Succeeded,
    string? Error)
{
    /// <summary>成功观测的构造入口（收窄参数，避免调用方写错 <c>Succeeded</c>）。</summary>
    public static LlmBalanceObservation Success(
        string providerId,
        string currency,
        decimal totalBalance,
        DateTimeOffset observedAtUtc,
        decimal? grantedBalance = null,
        decimal? toppedUpBalance = null) =>
        new(providerId, currency, totalBalance, grantedBalance, toppedUpBalance, observedAtUtc, true, null);

    /// <summary>失败观测的构造入口。余额字段恒为 <c>null</c>。</summary>
    public static LlmBalanceObservation Failure(
        string providerId,
        string currency,
        string error,
        DateTimeOffset observedAtUtc) =>
        new(providerId, currency, null, null, null, observedAtUtc, false, error);
}

/// <summary>相邻两次观测之间发生了什么。</summary>
public enum LlmUsageIntervalKind
{
    /// <summary>余额下降 = 发生消耗，金额可算。</summary>
    Consumption = 1,

    /// <summary>余额上升 = 发生充值（或服务商退款/调整），<b>消耗不可由差值推得</b>。</summary>
    Recharge = 2,

    /// <summary>余额持平。可能是「确实没花钱」，也可能是「两端之间没调用」——只能报事实。</summary>
    NoChange = 3,

    /// <summary>端点缺失或失败，无法判定。</summary>
    Unavailable = 4,

    /// <summary>两端间隔超过阈值：中间可能发生过未被采到的充值，直接做差会失真。</summary>
    Gap = 5,
}

/// <summary>
/// 相邻两次观测之间的一段区间。
/// <para>
/// <see cref="Consumption"/> 为 <c>null</c> 表示**无法判定**，绝不用 0 冒充 ——
/// 0 会被读者当作「这段时间没花钱」，而实际情况可能是「没采到」。
/// </para>
/// </summary>
/// <param name="BalanceDelta">后一次减前一次的余额差（可为负）。<see cref="LlmUsageIntervalKind.Unavailable"/> 时为 <c>null</c>。</param>
/// <param name="Consumption">可判定的消耗金额（正数）；无法判定为 <c>null</c>。</param>
/// <param name="Note">给读者的一句话解释，写清为什么这个区间是这样判定的。</param>
public sealed record LlmUsageInterval(
    string ProviderId,
    string Currency,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    TimeSpan Duration,
    decimal? BalanceDelta,
    decimal? Consumption,
    LlmUsageIntervalKind Kind,
    string Note);

/// <summary>一段时间内的汇总。窗口内无法判定的区间单独计数，不摊进平均值。</summary>
/// <param name="IntervalCount">窗口内区间总数（含无法判定者）。</param>
/// <param name="DecidableIntervalCount">其中能给出消耗金额的区间数。</param>
/// <param name="RechargeCount">判定为充值的区间数。</param>
/// <param name="GapCount">判定为缺口（超阈值）的区间数。</param>
/// <param name="UnavailableCount">因端点失败而无法判定的区间数。</param>
/// <param name="TotalConsumption">可判定区间的消耗合计；一个都不可判定时为 <c>null</c>。</param>
/// <param name="DecidableDuration">可判定区间覆盖的时长合计。</param>
public sealed record LlmUsageSummary(
    string ProviderId,
    string Currency,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    int IntervalCount,
    int DecidableIntervalCount,
    int RechargeCount,
    int GapCount,
    int UnavailableCount,
    decimal? TotalConsumption,
    TimeSpan DecidableDuration)
{
    /// <summary>
    /// 可判定区间的平均消耗速率（每小时的金额）。<b>分母只用可判定时长</b>，
    /// 且没有任何可判定区间时为 <c>null</c>。
    /// </summary>
    public decimal? ConsumptionPerHour =>
        DecidableDuration <= TimeSpan.Zero || TotalConsumption is null
            ? null
            : decimal.Round(TotalConsumption.Value / (decimal)DecidableDuration.TotalHours, 4, MidpointRounding.AwayFromZero);
}

/// <summary>
/// 分析选项。
/// </summary>
/// <param name="GapThreshold">
/// 相邻观测超过该间隔即判为 <see cref="LlmUsageIntervalKind.Gap"/>。
/// <para>
/// 默认 30 分钟 = 三个 10 分钟采样周期：允许丢一两拍而不误报，又能在真正断采时如实暴露。
/// </para>
/// </param>
/// <param name="AmountTolerance">
/// 判定「持平」的容差。服务商返回值可能有末位抖动；默认 <c>0</c>（严格相等），
/// 因为金额是 decimal，抖动应当被视作真实变化而不是被吞掉。
/// </param>
public sealed record LlmUsageAnalysisOptions(
    TimeSpan GapThreshold,
    decimal AmountTolerance)
{
    /// <summary>默认选项：10 分钟采样下的 3 拍缺口阈值，零容差。</summary>
    public static LlmUsageAnalysisOptions Default { get; } =
        new(TimeSpan.FromMinutes(30), 0m);
}
