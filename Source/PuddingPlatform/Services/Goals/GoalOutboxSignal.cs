namespace PuddingPlatform.Services.Goals;

/// <summary>
/// goal_outbox 的进程内低延迟唤醒信号。正确性不依赖此信号：Worker 仍执行低频恢复扫描。
/// </summary>
public sealed class GoalOutboxSignal
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    public void Signal()
    {
        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // 已有一个待消费信号即可；outbox 本身是 durable truth。
        }
    }

    public async Task WaitAsync(TimeSpan maximumDelay, CancellationToken ct)
    {
        if (maximumDelay <= TimeSpan.Zero)
            return;
        await _signal.WaitAsync(maximumDelay, ct);
    }
}
