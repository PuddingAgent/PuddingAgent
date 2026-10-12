namespace PuddingSsh.Execution;

/// <summary>
/// 有界阶段 worker 池（ADR-096 D3）：阶段端口的**同步启动段**可能阻塞线程，
/// 因此不能直接在业务调用线程上跑。容量固定，满额即拒绝新增工作（<c>ssh.busy</c>），
/// 不无限创建脱离追踪的 <c>Task.Run</c>；已超时但仍占着线程的工作会被计数并最终归还。
/// </summary>
internal sealed class SshStageWorkerPool
{
    private int _active;
    private int _completed;

    public SshStageWorkerPool(int capacity)
    {
        Capacity = capacity < 1 ? 1 : capacity;
    }

    public int Capacity { get; }

    /// <summary>仍在占用的 worker 数（含已超时但线程尚未归还的）。</summary>
    public int Active => Volatile.Read(ref _active);

    public int Completed => Volatile.Read(ref _completed);

    public bool IsFull => Active >= Capacity;

    /// <summary>
    /// 在池内启动一个阶段调用。返回的 Task 与池的占用绑定：即使调用方放弃等待，
    /// 占用也会在工作真正结束时归还（业务等待有界，资源占用可查询）。
    /// </summary>
    public Task<T> Start<T>(Func<Task<T>> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (IsFull)
        {
            throw new SshStageCapacityExceededException();
        }

        Interlocked.Increment(ref _active);
        return Task.Run(async () =>
        {
            try
            {
                return await work().ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
                Interlocked.Increment(ref _completed);
            }
        });
    }
}

/// <summary>worker 池满：新增工作必须被拒绝，而不是排队堆积。</summary>
internal sealed class SshStageCapacityExceededException()
    : Exception("stage worker capacity exceeded");
