namespace PuddingCode.SubAgents;

/// <summary>子代理主动回信给父代理的额度判定结论。</summary>
public enum SubAgentParentReportDecision
{
    /// <summary>允许发送。</summary>
    Allowed,
    /// <summary>本次 run 的回信条数已用尽。</summary>
    ExceededQuota,
    /// <summary>距上一条回信不足最小间隔。</summary>
    TooSoon
}

/// <summary>判定结论 + 可读原因 + 剩余额度；原因进运行日志，便于事后解释「这条为什么被拦」。</summary>
/// <param name="Decision">结论。</param>
/// <param name="Reason">人类可读原因，会原样回给模型，因此必须写清「下一步该怎么办」。</param>
/// <param name="Remaining">本次判定之后仍未被占用的额度（不含本条的提交）。</param>
public sealed record SubAgentParentReportPlan(
    SubAgentParentReportDecision Decision,
    string Reason,
    int Remaining)
{
    /// <summary>是否允许发送。</summary>
    public bool Allowed => Decision == SubAgentParentReportDecision.Allowed;
}

/// <summary>
/// 子代理 → 父代理<b>主动</b>回信的额度账本（按 run 一份）。
/// <para>
/// 与 <see cref="SubAgentProgressReportPolicy"/> 分工不同，两者不能互相替代：
/// 那一层判定「系统自动进度汇报」该不该发，输入是轮次；这一层判定「模型自己调
/// <c>send_message(to: agent:parent)</c>」该不该放行，输入是**时间**——因为工具调用路径
/// 拿不到轮次，时间才是这条路上可靠可得的信息。
/// </para>
/// <para>
/// 为什么需要它：自动进度汇报有节流，但模型仍可每轮主动回信；父代理真正的成本项是它自己的
/// 上下文窗口，不做上限时一个长跑子代理能把父会话塞满，而这个代价不会出现在子代理的账单上。
/// </para>
/// <para>
/// <b>判定与消耗分离</b>：<see cref="TryAcquire"/> 是纯判定、不改状态，
/// <see cref="Commit"/> 只在消息**真的送达**后推进——否则一次传输失败会白扣掉一条额度，
/// 事后看日志会得到「明明没收到却说超额」的错误解释。
/// </para>
/// </summary>
public sealed class SubAgentParentReportBudget
{
    /// <summary>默认额度：一个 run 最多主动回信 8 条。</summary>
    public const int DefaultMaxMessages = 8;

    /// <summary>默认最小间隔 20 秒。</summary>
    public static readonly TimeSpan DefaultMinInterval = TimeSpan.FromSeconds(20);

    private readonly int _maxMessages;
    private readonly TimeSpan _minInterval;
    private int _sent;
    private DateTimeOffset? _lastSentAt;

    /// <param name="maxMessages">额度上限；小于 1 时按 1 处理（配成 0 不应退化成「永远发不出」）。</param>
    /// <param name="minInterval">最小间隔；为 null 用默认值，负数按 0 处理（0 = 不限间隔，仍受额度约束）。</param>
    public SubAgentParentReportBudget(int maxMessages = DefaultMaxMessages, TimeSpan? minInterval = null)
    {
        _maxMessages = maxMessages < 1 ? 1 : maxMessages;

        var interval = minInterval ?? DefaultMinInterval;
        _minInterval = interval < TimeSpan.Zero ? TimeSpan.Zero : interval;
    }

    /// <summary>已消耗的额度。</summary>
    public int SentCount => _sent;

    /// <summary>尚未占用的额度。</summary>
    public int Remaining => _maxMessages - _sent;

    /// <summary>
    /// 判定此刻是否允许主动回信。<b>不改变状态</b>：被拒的调用不会扣额度，
    /// 也不会把「最近一次回信时刻」推后。
    /// </summary>
    /// <param name="now">调用方提供的当前时刻（注入而非内部取时钟，便于逐分支断言）。</param>
    public SubAgentParentReportPlan TryAcquire(DateTimeOffset now)
    {
        if (_sent >= _maxMessages)
        {
            return new SubAgentParentReportPlan(
                SubAgentParentReportDecision.ExceededQuota,
                $"parent reply quota exhausted ({_sent}/{_maxMessages}); report the result at the end instead",
                0);
        }

        if (_lastSentAt is { } last)
        {
            var elapsed = now - last;

            // 时钟回拨时 elapsed 为负。这一层的语义是「距上次过了多久」，时间倒退说明不了
            // 「太频繁」，所以钳到 0：minInterval > 0 时结果不变（仍判 TooSoon）；
            // minInterval = 0（不限间隔）时不会被一次系统校时变成永久拒绝。
            if (elapsed < TimeSpan.Zero)
                elapsed = TimeSpan.Zero;

            if (elapsed < _minInterval)
            {
                return new SubAgentParentReportPlan(
                    SubAgentParentReportDecision.TooSoon,
                    $"only {elapsed.TotalSeconds:F0}s since the last parent reply; minimum is {_minInterval.TotalSeconds:F0}s",
                    Remaining);
            }
        }

        return new SubAgentParentReportPlan(
            SubAgentParentReportDecision.Allowed,
            $"within quota ({_sent + 1}/{_maxMessages})",
            Remaining);
    }

    /// <summary>
    /// 在消息确实送达之后消耗一条额度。
    /// </summary>
    /// <returns>额度已用尽时返回 <c>false</c> 且不改变状态（调用方应据此当作未送达处理）。</returns>
    public bool Commit(DateTimeOffset now)
    {
        if (_sent >= _maxMessages)
            return false;

        _sent++;
        _lastSentAt = now;
        return true;
    }
}
