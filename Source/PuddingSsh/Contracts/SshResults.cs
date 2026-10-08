using PuddingSsh.Diagnostics;

namespace PuddingSsh.Contracts;

/// <summary>
/// 执行状态（设计 §7.3）：<c>completed / failed / timed_out / cancelled / unknown</c>。
/// </summary>
public enum SshExecutionStatus
{
    Completed,
    Failed,
    TimedOut,
    Cancelled,
    Unknown,
}

/// <summary>
/// 提交事实（设计 §7.3）：区分「连接失败」与「已执行但结果丢失」。
/// </summary>
public enum SshExecutionState
{
    NotSubmitted,
    Submitted,
    Exited,
    Unknown,
}

/// <summary>握手/认证结果（<c>ssh_test</c>）。</summary>
public sealed record SshTestResult
{
    public required SshExecutionStatus Status { get; init; }
    public required string HostId { get; init; }
    public required string HostRevision { get; init; }
    public required SshTargetRef Target { get; init; }

    /// <summary>本次握手**实际验证通过**的指纹（<c>SHA256:...</c>）；未走到该阶段为 <see langword="null"/>。</summary>
    public string? VerifiedHostKeySha256 { get; init; }

    public string? HostKeyAlgorithm { get; init; }
    public bool AuthenticationSucceeded { get; init; }
    public required SshPhase Phase { get; init; }
    public string? ErrorCode { get; init; }
    public string? Diagnostic { get; init; }
    public int DurationMs { get; init; }

    /// <summary>各阶段耗时（阶段名 → 毫秒），用于诊断建连是否按 deadline 收敛。</summary>
    public IReadOnlyDictionary<string, int> PhaseDurationsMs { get; init; } = new Dictionary<string, int>();
}

/// <summary>
/// <c>ssh_execute</c> 结果合同（设计 §7.3）。退出码在没有收到服务端 exit-status 时为
/// <see langword="null"/>，**不得伪造为 0**；stderr 非空也不等于失败。
/// </summary>
public sealed record SshExecuteResult
{
    public required SshExecutionStatus Status { get; init; }
    public required SshExecutionState ExecutionState { get; init; }
    public required string HostId { get; init; }
    public required string HostRevision { get; init; }

    /// <summary>本次握手实际验证通过的指纹。</summary>
    public string? VerifiedHostKeySha256 { get; init; }

    /// <summary>远端退出码；未收到为 <see langword="null"/>。</summary>
    public int? ExitCode { get; init; }

    /// <summary>远端退出信号名（如 <c>TERM</c>）；未收到为 <see langword="null"/>。</summary>
    public string? ExitSignal { get; init; }

    public string Stdout { get; init; } = string.Empty;
    public string Stderr { get; init; } = string.Empty;

    /// <summary>实际捕获进结果的字节数（≤ 预算）。</summary>
    public long CapturedBytes { get; init; }

    /// <summary>双流实际到达的字节总数（含被丢弃部分）。</summary>
    public long ReceivedBytes { get; init; }

    public bool OutputTruncated { get; init; }

    /// <summary>建连失败且确定**尚未提交**时为 <see langword="true"/>；命令提交过程断线一律视为可能已执行。</summary>
    public bool RetrySafe { get; init; }

    public string? ErrorCode { get; init; }
    public required SshPhase Phase { get; init; }
    public string? Diagnostic { get; init; }
    public int DurationMs { get; init; }
}
