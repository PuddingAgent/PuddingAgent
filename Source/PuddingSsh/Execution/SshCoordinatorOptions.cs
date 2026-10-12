namespace PuddingSsh.Execution;

/// <summary>
/// 协调器预算（设计 §9.1）。默认值取自 <see cref="Contracts.SshOperationLimits"/> 的硬上限，
/// 引用方可以按部署收窄，但**不允许**放宽到超过硬上限的语义（宿主总并行 8 是产品级约束）。
/// </summary>
public sealed record SshCoordinatorOptions
{
    /// <summary>Core 总并行操作上限，默认 8。</summary>
    public int CoreMaxConcurrentOperations { get; init; } = Contracts.SshOperationLimits.CoreMaxConcurrentOperations;

    /// <summary>等待队列上限，默认 32；满额**立即**返回 <c>ssh.busy</c>，不创建无界连接任务。</summary>
    public int MaxWaitQueueLength { get; init; } = Contracts.SshOperationLimits.WaitQueueHardLimit;

    /// <summary>
    /// 操作清理宽限，默认 2 秒。语义是**资源回收**：超过后不再等会话返回，直接释放独占连接，
    /// 因此它不延长业务执行许可（设计 §9.1）。TERM/关闭/排空/Dispose 共用这一份预算。
    /// </summary>
    public TimeSpan CleanupGrace { get; init; } = Contracts.SshOperationLimits.CleanupGrace;

    /// <summary>
    /// 清理隔离区容量，默认 8（ADR-096 D3）：预算内没结束的资源仍被追踪并占用计数，
    /// 满额即拒绝新增工作（<c>ssh.busy</c>），不无限堆积脱离追踪的清理任务。
    /// </summary>
    public int MaxQuarantinedResources { get; init; } = 8;

    /// <summary>
    /// 阶段 worker 池容量，默认 8：阶段端口的同步启动段可能阻塞线程，因此必须在线程池上执行，
    /// 且容量固定；满额即拒绝新增工作。
    /// </summary>
    public int MaxStageWorkers { get; init; } = 8;

    public static SshCoordinatorOptions Default { get; } = new();
}

/// <summary>停止报告（ADR-096 D3）：停止等待有界，且**如实报告**仍未结束的操作与隔离资源。</summary>
public sealed record SshStopReport(int PendingOperations, int PendingCleanups, bool TimedOut);