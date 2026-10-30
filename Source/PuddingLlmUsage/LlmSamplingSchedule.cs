namespace PuddingLlmUsage;

/// <summary>
/// 采样节奏的纯算术。单独抽出来是因为它是唯一能「算错而不报错」的地方 ——
/// 算错了只会表现为「采样间隔怪怪的」，不会有任何异常。
/// </summary>
public static class LlmSamplingSchedule
{
    /// <summary>
    /// 求「严格晚于 <paramref name="nowUtc"/> 的下一个对齐时刻」。
    /// <para>
    /// 对齐基准是 <see cref="DateTimeOffset.UnixEpoch"/>（UTC 零点起算），
    /// 因此 10 分钟间隔会落在 :00 / :10 / :20…，不会因为进程启动时刻不同而各自漂移。
    /// </para>
    /// <para>
    /// 恰好在对齐点上时返回<b>下一个</b>槽位而不是它自己：
    /// 否则「刚采完」与「下一次该采」会指向同一时刻，形成零间隔连采。
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="interval"/> 非正。</exception>
    public static DateTimeOffset NextSlot(DateTimeOffset nowUtc, TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(interval), interval, "间隔必须为正。");

        var epochTicks = DateTimeOffset.UnixEpoch.UtcTicks;
        var nowTicks = nowUtc.ToUniversalTime().UtcTicks;

        if (nowTicks <= epochTicks)
            return DateTimeOffset.UnixEpoch;

        var elapsed = nowTicks - epochTicks;
        var slots = elapsed / interval.Ticks;
        var nextTicks = epochTicks + (slots + 1) * interval.Ticks;

        return new DateTimeOffset(nextTicks, TimeSpan.Zero);
    }

    /// <summary>
    /// 距离下一个槽位还有多久（永不为零或负）。
    /// </summary>
    public static TimeSpan DelayUntilNextSlot(DateTimeOffset nowUtc, TimeSpan interval)
    {
        var delay = NextSlot(nowUtc, interval) - nowUtc.ToUniversalTime();
        return delay > TimeSpan.Zero ? delay : interval;
    }

    /// <summary>
    /// 判断某个时刻是否是「采样槽位」的整数倍（用于把历史点归入固定间隔的桶）。
    /// </summary>
    public static bool IsOnSlot(DateTimeOffset valueUtc, TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(interval), interval, "间隔必须为正。");

        var elapsed = valueUtc.ToUniversalTime().UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks;
        return elapsed >= 0 && elapsed % interval.Ticks == 0;
    }
}
