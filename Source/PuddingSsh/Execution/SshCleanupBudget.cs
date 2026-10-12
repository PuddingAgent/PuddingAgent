namespace PuddingSsh.Execution;

/// <summary>
/// **单次清理预算**（ADR-096 D3 补充 L3）：从清理开始起算的一份绝对预算，
/// TERM / 排空 / 关闭 / Dispose 全部只取它的剩余时间。
/// <para>
/// 复核指出的两个反模式都在这里被排除：
/// <list type="bullet">
///   <item>一处按**业务剩余**取到 0（操作 deadline 已过 ⇒ 清理预算 0 ⇒ 立刻进隔离区，实际还没试过关闭）；</item>
///   <item>另一处又新开一份 2 秒（每步各自计时 ⇒ 总时长无界）。</item>
/// </list>
/// 清理是资源回收，不是执行许可，因此**不受业务 deadline 影响**。
/// </para>
/// </summary>
internal sealed class SshCleanupBudget
{
    private readonly TimeProvider _time;
    private readonly long _startTimestamp;
    private readonly TimeSpan _total;

    public SshCleanupBudget(TimeProvider time, TimeSpan total)
    {
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _total = total > TimeSpan.Zero ? total : TimeSpan.Zero;
        _startTimestamp = time.GetTimestamp();
    }

    /// <summary>预算总量。</summary>
    public TimeSpan Total => _total;

    /// <summary>剩余额度（单调时间计算，不为负）。</summary>
    public TimeSpan Remaining
    {
        get
        {
            var remaining = _total - _time.GetElapsedTime(_startTimestamp);
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }

    /// <summary>已经消耗的时间（诊断用）。</summary>
    public TimeSpan Elapsed => _time.GetElapsedTime(_startTimestamp);

    /// <summary>预算是否已经用尽。</summary>
    public bool IsExhausted => Remaining <= TimeSpan.Zero;
}
