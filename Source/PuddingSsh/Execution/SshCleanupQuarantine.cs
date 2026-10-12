namespace PuddingSsh.Execution;

/// <summary>
/// 清理隔离区（ADR-096 D3）：预算内没结束的资源不能就这么丢掉——它们仍会被追踪，
/// 直到真正结束才释放计数；容量固定，**满额即拒绝新增工作**（<c>ssh.busy</c>），
/// 不用无限创建脱离追踪的 <c>Task.Run</c>。
/// </summary>
internal sealed class SshCleanupQuarantine
{
    private int _pending;
    private int _completed;

    public SshCleanupQuarantine(int capacity)
    {
        Capacity = capacity < 0 ? 0 : capacity;
    }

    public int Capacity { get; }

    /// <summary>仍在隔离区里的清理数。</summary>
    public int Pending => Volatile.Read(ref _pending);

    /// <summary>历史完成数（诊断用）。</summary>
    public int Completed => Volatile.Read(ref _completed);

    public bool IsFull => Pending >= Capacity;

    /// <summary>把一个尚未结束的清理任务纳入追踪：完成后释放计数，异常被观察。</summary>
    public void Track(Task cleanup)
    {
        ArgumentNullException.ThrowIfNull(cleanup);
        Interlocked.Increment(ref _pending);

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

    /// <summary>把一个**尚未开始**的清理工作纳入追踪：它必须自己保证有界。</summary>
    public void Track(Func<Task> cleanupWork)
    {
        ArgumentNullException.ThrowIfNull(cleanupWork);
        Interlocked.Increment(ref _pending);

        try
        {
            var task = cleanupWork();
            if (task.IsCompleted)
            {
                Complete(task);
                return;
            }

            _ = task.ContinueWith(
                static (completed, state) => ((SshCleanupQuarantine)state!).Complete(completed),
                this,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        catch (Exception)
        {
            Interlocked.Decrement(ref _pending);
            throw;
        }
    }

    private void Complete(Task task)
    {
        _ = task.Exception; // 观察异常：隔离区的任务不得变成未观察异常。
        Interlocked.Decrement(ref _pending);
        Interlocked.Increment(ref _completed);
    }
}
