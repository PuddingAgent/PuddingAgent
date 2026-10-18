namespace PuddingConversationReadModel;

/// <summary>
/// <see cref="SseCatchUpMeter"/> 的内部可变计量状态（纯内存；时钟由调用方以 elapsed 传入）。
/// <para>停止后冻结：不再消费、不重复计数，也不覆盖首次停止原因。</para>
/// </summary>
internal sealed class SseCatchUpMeterState
{
    private readonly ConversationReadModelLimits _limits;
    private int _eventsConsumed;
    private long _bytesConsumed;
    private SseCatchUpStopReason _stopReason;
    private TimeSpan _lastElapsed;
    private bool _hasElapsed;

    internal SseCatchUpMeterState(ConversationReadModelLimits limits) => _limits = limits;

    internal int EventsConsumed => _eventsConsumed;

    internal long BytesConsumed => _bytesConsumed;

    internal SseCatchUpStopReason StopReason => _stopReason;

    internal bool TryConsume(int eventBytes, TimeSpan elapsed)
    {
        if (eventBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(eventBytes), eventBytes, "eventBytes must be non-negative.");
        if (elapsed < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(elapsed), elapsed, "elapsed must be non-negative.");
        if (_hasElapsed && elapsed < _lastElapsed)
            throw new ArgumentOutOfRangeException(nameof(elapsed), elapsed, "elapsed must be monotonically non-decreasing.");

        if (_stopReason != SseCatchUpStopReason.None)
            return false;

        // 触发限额的事件不消费：先于计数/字节/耗时任一触顶即停止并冻结原因。
        if (_eventsConsumed + 1 > _limits.SseCatchUpMaxEvents)
        {
            _stopReason = SseCatchUpStopReason.EventLimit;
            return false;
        }

        if (_bytesConsumed + eventBytes > _limits.SseCatchUpMaxBytes)
        {
            _stopReason = SseCatchUpStopReason.ByteLimit;
            return false;
        }

        if (elapsed > _limits.SseCatchUpMaxDuration)
        {
            _stopReason = SseCatchUpStopReason.TimeBudget;
            return false;
        }

        _eventsConsumed++;
        _bytesConsumed += eventBytes;
        _lastElapsed = elapsed;
        _hasElapsed = true;
        return true;
    }
}
