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
    /// 因此它不延长业务执行许可（设计 §9.1）。
    /// </summary>
    public TimeSpan CleanupGrace { get; init; } = Contracts.SshOperationLimits.CleanupGrace;

    public static SshCoordinatorOptions Default { get; } = new();
}
