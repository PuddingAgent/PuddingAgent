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

    /// <summary>
    /// 关闭工作（会话 Dispose / 迟到回收）的独立容量，默认 8（L1.6）：
    /// 关闭不能被同一池里卡死的 connect/exec 饿死，因此用单独且有上限的池；
    /// 令牌耗尽时拒绝新的资源创建，而不是用无记账 <c>Task.Run</c> 绕过容量。
    /// </summary>
    public int MaxCloseWorkers { get; init; } = 8;

    public static SshCoordinatorOptions Default { get; } = new();

    /// <summary>清理隔离区容量的上限（防御性上限，避免配置把清理变成无界堆积）。</summary>
    public const int MaxQuarantinedResourcesHardLimit = 32;

    /// <summary>阶段 worker 池容量的上限。</summary>
    public const int MaxStageWorkersHardLimit = 32;

    /// <summary>
    /// 机器可校验的协调器限额校验：**非法配置直接拒绝**（<c>ssh.config_invalid</c>），
    /// 不允许「先夹取到合法值再继续」——那会让越权配置看起来生效了。
    /// </summary>
    public void Validate()
    {
        Require(
            CoreMaxConcurrentOperations >= 1 && CoreMaxConcurrentOperations <= Contracts.SshOperationLimits.CoreMaxConcurrentOperations,
            "coordinator.core_max_concurrent_operations",
            CoreMaxConcurrentOperations.ToString());
        Require(
            MaxWaitQueueLength >= 0 && MaxWaitQueueLength <= Contracts.SshOperationLimits.WaitQueueHardLimit,
            "coordinator.max_wait_queue_length",
            MaxWaitQueueLength.ToString());
        Require(
            CleanupGrace > TimeSpan.Zero && CleanupGrace <= Contracts.SshOperationLimits.CleanupGrace,
            "coordinator.cleanup_grace",
            CleanupGrace.ToString());
        Require(
            MaxQuarantinedResources >= 0 && MaxQuarantinedResources <= MaxQuarantinedResourcesHardLimit,
            "coordinator.max_quarantined_resources",
            MaxQuarantinedResources.ToString());
        Require(
            MaxStageWorkers >= 1 && MaxStageWorkers <= MaxStageWorkersHardLimit,
            "coordinator.max_stage_workers",
            MaxStageWorkers.ToString());
        Require(
            MaxCloseWorkers >= 1 && MaxCloseWorkers <= MaxStageWorkersHardLimit,
            "coordinator.max_close_workers",
            MaxCloseWorkers.ToString());
    }

    private static void Require(bool condition, string field, string value)
    {
        if (!condition)
        {
            throw new Diagnostics.SshComponentException(
                Diagnostics.SshErrorCodes.ConfigInvalid,
                $"协调器配置非法：{field}={value}；请改为合法取值（硬上限见 SshOperationLimits / SshCoordinatorOptions 常量）。");
        }
    }
}

/// <summary>
/// 停止报告（ADR-096 D3）：停止等待有界，且**如实报告**仍未结束的东西。
/// <paramref name="PendingOperations"/> 是业务仍在跑的操作数；<paramref name="PendingResources"/> 是
/// **业务已返回但物理资源仍未结束**的租约数（L1：Dispose 完成不等于所有工作结束）。
/// </summary>
public sealed record SshStopReport(int PendingOperations, int PendingCleanups, int PendingResources, bool TimedOut);