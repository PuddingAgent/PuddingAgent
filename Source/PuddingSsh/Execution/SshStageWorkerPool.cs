namespace PuddingSsh.Execution;

/// <summary>
/// 有界阶段 worker 池（ADR-096 D3 补充约束）：令牌必须**原子**发放，容量是硬上限。
/// <para>
/// 复核 F9 的同类缺口：先 <c>IsFull</c> 再 <c>Interlocked.Increment</c> 是两步预留，
/// 并发下可越限。这里改为 CAS 循环取令牌：取不到立即失败（<c>ssh.busy</c>），不做无界等待。
/// 令牌在**真实工作结束时**归还，不因业务停止等待而归还——所以「业务已返回」不等于「线程已释放」。
/// </para>
/// </summary>
internal sealed class SshStageWorkerPool
{
    private int _active;
    private int _peak;
    private int _completed;
    private int _acquired;
    private int _rejected;

    public SshStageWorkerPool(int capacity)
    {
        Capacity = capacity < 1 ? 1 : capacity;
    }

    public int Capacity { get; }

    /// <summary>当前占用（含已超时但线程/任务尚未归还的）。</summary>
    public int Active => Volatile.Read(ref _active);

    /// <summary>历史峰值：并发 barrier 测试用它断言「从未越限」。</summary>
    public int Peak => Volatile.Read(ref _peak);

    public int Completed => Volatile.Read(ref _completed);

    public int Acquired => Volatile.Read(ref _acquired);

    public int Rejected => Volatile.Read(ref _rejected);

    public bool IsFull => Active >= Capacity;

    /// <summary>原子取得一个令牌；返回 true 表示调用方随后**必须** <see cref="Release"/>。</summary>
    public bool TryAcquire()
    {
        while (true)
        {
            var current = Volatile.Read(ref _active);
            if (current >= Capacity)
            {
                Interlocked.Increment(ref _rejected);
                return false;
            }

            if (Interlocked.CompareExchange(ref _active, current + 1, current) == current)
            {
                Interlocked.Increment(ref _acquired);
                RecordPeak(current + 1);
                return true;
            }
        }
    }

    /// <summary>归还令牌（幂等使用方自行保证只归还一次）。</summary>
    public void Release()
    {
        Interlocked.Decrement(ref _active);
        Interlocked.Increment(ref _completed);
    }

    /// <summary>
    /// 在池内启动一个阶段调用：**先取令牌再调度**（登记先于调度），
    /// 并在真实工作结束时归还令牌。调用线程立即拿到可等待句柄，同步启动段不会卡住它。
    /// </summary>
    public Task<T> StartAsync<T>(Func<Task<T>> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (!TryAcquire())
        {
            throw new SshStageCapacityExceededException();
        }

        return RunAsync(work);
    }

    /// <summary>无返回值版本（用于释放/关闭工作）。</summary>
    public Task StartAsync(Func<Task> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (!TryAcquire())
        {
            throw new SshStageCapacityExceededException();
        }

        return RunAsync(work);
    }

    private async Task<T> RunAsync<T>(Func<Task<T>> work)
    {
        try
        {
            return await Task.Run(work, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            Release();
        }
    }

    private async Task RunAsync(Func<Task> work)
    {
        try
        {
            await Task.Run(work, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            Release();
        }
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

/// <summary>worker 池满：新增工作必须被拒绝，而不是排队堆积。</summary>
internal sealed class SshStageCapacityExceededException()
    : Exception("stage worker capacity exceeded");
