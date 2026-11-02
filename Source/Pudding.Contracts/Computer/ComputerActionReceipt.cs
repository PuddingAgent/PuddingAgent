namespace Pudding.Contracts.Computer;

/// <summary>执行侧事实：驱动到底有没有被调用、输入有没有提交出去。</summary>
public enum ComputerExecution
{
    /// <summary>驱动未被调用（旧观察、无授权、被接管、焦点错误……），**无副作用**。</summary>
    NotStarted,

    /// <summary>输入已完整提交。**可能有副作用**，必须先观察。</summary>
    Dispatched,

    /// <summary>驱动执行完毕。</summary>
    Completed,

    /// <summary>断连、驱动挂起、Worker 崩溃 —— 终态未知。**保守认为可能生效**。</summary>
    Unknown,
}

/// <summary>验证侧事实：后置条件是否被证实。</summary>
public enum ComputerVerification
{
    NotRequested,
    Passed,
    Failed,
    Unverified,
}

/// <summary>给调用方的最终判定（由前两者归约而来，不是第四个独立事实）。</summary>
public enum ComputerCompletion
{
    /// <summary>拦在驱动之前，什么都没发生；不能盲重试旧参数，要先刷新/恢复。</summary>
    Blocked,

    /// <summary>已提交但未验证 —— 需要先观察。</summary>
    Dispatched,

    /// <summary>必需的后置条件全部通过，有新证据。</summary>
    Verified,

    /// <summary>部分提交或后置条件不满足。**不能因失败重发整段**（可能已部分生效）。</summary>
    Failed,

    /// <summary>终态未知 —— 按"可能已生效"处理。</summary>
    Unknown,
}

/// <summary>
/// 动作回执。
/// <para>
/// 三个状态刻意分开，而不是一个 success 布尔：
/// 「没做」「做了但没验证」「做了且验证过」「做了一半」四种情况对调用方的下一步**完全不同**，
/// 压缩成一个布尔就会把"部分提交"误当成"没做"从而重发，造成重复副作用。
/// </para>
/// <para>
/// <see cref="MayHaveSideEffects"/> 与 <see cref="SafeToRetry"/> 是给重试策略用的：
/// 只有 <see cref="ComputerCompletion.Blocked"/> 才是安全的直接重试，
/// 其余情况需要先观察或用同一个 operation 查询终态。
/// </para>
/// </summary>
public sealed record ComputerActionReceipt(
    string OperationId,
    ComputerExecution Execution,
    ComputerVerification Verification,
    ComputerCompletion Completion,
    bool MayHaveSideEffects,
    bool SafeToRetry,
    string Backend,
    int? SubmittedInputCount,
    int? ExpectedInputCount,
    IReadOnlyList<string> EvidenceRefs,
    string? ErrorCode);
