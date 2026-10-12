namespace PuddingSsh.Execution;

/// <summary>
/// 清理隔离区（ADR-096 D3 补充约束）：**先原子预留令牌，再转移资源**。
/// <para>
/// 复核 F9 的缺口：原来的 <c>Track</c> 没有预留/限额判定，只要 <c>IsFull</c> 检查发生在排队之前，
/// 已排队请求就能越限进入（capacity=1 实测 pending=4）。现在：
/// <list type="bullet">
///   <item><see cref="TryReserve"/> 用 CAS 发放隔离令牌，取不到就是取不到——永远 <c>Pending ≤ Capacity</c>。</item>
///   <item>转移失败时资源**继续持有原准入槽**（调用方不得先释放原槽再无条件追踪）。</item>
///   <item><c>Capacity = 0</c> 的语义是「禁用转移、保留原槽」，<see cref="IsFull"/> 为 false，
///         因此不会把正常请求悄悄全部拒掉。</item>
/// </list>
/// </para>
/// </summary>
internal sealed class SshCleanupQuarantine
{
    private int _pending;
    private int _peak;
    private int _completed;

    public SshCleanupQuarantine(int capacity)
    {
        Capacity = capacity < 0 ? 0 : capacity;
    }

    public int Capacity { get; }

    /// <summary>已被预留、尚未结束的隔离令牌数。</summary>
    public int Pending => Volatile.Read(ref _pending);

    /// <summary>历史峰值（并发 barrier 测试断言峰值 ≤ 容量）。</summary>
    public int Peak => Volatile.Read(ref _peak);

    public int Completed => Volatile.Read(ref _completed);

    /// <summary>是否已满。容量 0 表示「禁用转移」，不算满。</summary>
    public bool IsFull => Capacity > 0 && Pending >= Capacity;

    /// <summary>原子预留一个隔离令牌；成功者随后必须调用 <see cref="ReleaseReservation"/> 或 <see cref="TrackReserved"/>。</summary>
    public bool TryReserve()
    {
        while (true)
        {
            var current = Volatile.Read(ref _pending);
            if (Capacity <= 0 || current >= Capacity)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _pending, current + 1, current) == current)
            {
                RecordPeak(current + 1);
                return true;
            }
        }
    }

    /// <summary>把已预留的令牌绑定到一个任务：任务结束（含 fault）时释放令牌，异常被观察。</summary>
    public void TrackReserved(Task cleanup)
    {
        ArgumentNullException.ThrowIfNull(cleanup);

        if (cleanup.IsCompleted)
        {
            Complete(cleanup);
            return;
        }

        _ = cleanup.ContinueWith(
            static (completed, state) => ((SshCleanupQuarantine)state!).Complete(completed),
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>归还一个预留（资源已自行结束或已由别处收尾）。</summary>
    public void ReleaseReservation()
    {
        Interlocked.Decrement(ref _pending);
        Interlocked.Increment(ref _completed);
    }

    private void Complete(Task task)
    {
        // 观察异常：隔离区的任务不得变成未观察异常。注意「异常已观察」不等于「关闭成功」——
        // 调用方必须另外拿到关闭证据（见 SshResourceLease.Closed）。
        _ = task.Exception;
        Interlocked.Decrement(ref _pending);
        Interlocked.Increment(ref _completed);
    }

    private void RecordPeak(int candidate)
    {
        while (true)
        {
            var peak = Volatile.Read(ref _peak);
            if (candidate <= peak || Interlocked.CompareExchange(ref _peak, candidate, peak) == peak)
            {
                return;
            }
        }
    }
}
