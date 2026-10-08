namespace PuddingSsh.Contracts;

/// <summary>
/// 单主机公开限额（设计 §5 / §9.1）。默认值与硬上限在这里是**唯一真源**：
/// 调用方只能缩小，不能扩大；实际生效值取「请求值、主机上限、宿主 deadline」的最小值（A3 协调器落实）。
/// </summary>
public sealed record SshOperationLimits
{
    /// <summary>建连（DNS、TCP、握手、认证）统一 deadline，默认 15 秒；不能每阶段独立叠加。</summary>
    public int ConnectTimeoutSeconds { get; init; } = 15;

    /// <summary>单次操作总时长默认值，默认 60 秒。</summary>
    public int OperationTimeoutSeconds { get; init; } = 60;

    /// <summary>单次操作总时长上限，默认 600 秒。</summary>
    public int MaxOperationTimeoutSeconds { get; init; } = 600;

    /// <summary>stdout + stderr 捕获预算，默认合计 64 KiB。</summary>
    public int MaxOutputBytes { get; init; } = 64 * 1024;

    /// <summary>每主机并行操作数，默认 2（独占连接，不跨账号复用）。</summary>
    public int MaxConcurrentOperations { get; init; } = 2;

    /// <summary>捕获硬上限 256 KiB（设计 §9.1）。</summary>
    public const int MaxOutputBytesHardLimit = 256 * 1024;

    /// <summary>Core 总并行操作上限 8。</summary>
    public const int CoreMaxConcurrentOperations = 8;

    /// <summary>等待队列上限 32；满额立即 <c>ssh.busy</c>，不创建无界连接任务。</summary>
    public const int WaitQueueHardLimit = 32;

    /// <summary>操作清理宽限 2 秒：属于资源回收，不延长业务执行许可。</summary>
    public static readonly TimeSpan CleanupGrace = TimeSpan.FromSeconds(2);

    public static SshOperationLimits Default { get; } = new();

    /// <summary>有效操作超时 = min(请求值 ?? 默认值, 主机上限)，且不小于 1 秒。</summary>
    public TimeSpan ResolveOperationTimeout(int? requestedSeconds)
    {
        var requested = requestedSeconds ?? OperationTimeoutSeconds;
        var clamped = Math.Min(requested, MaxOperationTimeoutSeconds);
        return TimeSpan.FromSeconds(Math.Max(1, clamped));
    }

    /// <summary>有效捕获预算 = min(请求值 ?? 默认值, 主机预算, 硬上限 256 KiB)。</summary>
    public int ResolveOutputBudget(int? requestedBytes)
    {
        var requested = requestedBytes ?? MaxOutputBytes;
        return Math.Clamp(Math.Min(requested, MaxOutputBytes), 1, MaxOutputBytesHardLimit);
    }

    public TimeSpan ConnectTimeout => TimeSpan.FromSeconds(Math.Max(1, ConnectTimeoutSeconds));
}
