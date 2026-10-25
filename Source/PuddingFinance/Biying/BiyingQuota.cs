namespace PuddingFinance.Biying;

/// <summary>
/// 日配额计数器（UTC 日切）。为什么必须有它：付费档 10,000/天看似宽裕，
/// 但一次全市场快照 + 反复试错可能在一小时内吃完，而**吃完之后所有查询都会失败**，
/// 如果没人提前告警，第二天只会表现为"系统突然不灵了"。
///
/// 缓存命中不计入消耗——这条在 <see cref="BiyingClient"/> 里通过"先查缓存再扣配额"保证。
/// </summary>
public sealed class BiyingQuota
{
    private readonly object _sync = new();
    private readonly IBiyingClock _clock;
    private readonly int _dailyLimit;
    private readonly double _warnRatio;

    private DateOnly _currentDay;
    private int _used;

    public BiyingQuota(int dailyLimit, double warnRatio, IBiyingClock clock)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dailyLimit);
        ArgumentNullException.ThrowIfNull(clock);
        _dailyLimit = dailyLimit;
        _warnRatio = Math.Clamp(warnRatio, 0d, 1d);
        _clock = clock;
        _currentDay = DayOf(clock.UtcNow);
        _used = 0;
    }

    public int DailyLimit => _dailyLimit;

    /// <summary>已用次数（跨日自动归零后的值）。</summary>
    public int Used
    {
        get
        {
            lock (_sync)
            {
                RollIfNeeded();
                return _used;
            }
        }
    }

    public int Remaining => Math.Max(0, _dailyLimit - Used);

    /// <summary>是否已进入告警区间（用尽前就要开始提醒）。</summary>
    public bool IsWarning => Used >= _dailyLimit * _warnRatio;

    /// <summary>
    /// 尝试消耗一次配额。返回 false 表示**已用尽**，调用方必须直接拒绝并回执，
    /// 不允许"试一次看看"——那正是把当天额度耗光的方式。
    /// </summary>
    public bool TryConsume(out int remaining)
    {
        lock (_sync)
        {
            RollIfNeeded();
            if (_used >= _dailyLimit)
            {
                remaining = 0;
                return false;
            }

            _used++;
            remaining = _dailyLimit - _used;
            return true;
        }
    }

    /// <summary>把一次调用退回（例如请求在扣配额后立刻因参数错误被本地拒绝）。</summary>
    public void Refund()
    {
        lock (_sync)
        {
            if (_used > 0)
                _used--;
        }
    }

    private void RollIfNeeded()
    {
        var today = DayOf(_clock.UtcNow);
        if (today == _currentDay)
            return;
        _currentDay = today;
        _used = 0;
    }

    private static DateOnly DayOf(DateTimeOffset moment) => DateOnly.FromDateTime(moment.UtcDateTime);
}
