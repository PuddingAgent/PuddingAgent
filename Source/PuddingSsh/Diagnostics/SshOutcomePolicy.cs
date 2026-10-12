using PuddingSsh.Contracts;

namespace PuddingSsh.Diagnostics;

/// <summary>执行结果判定（ADR-096 D4）：把「退出证据」与「输出完整性」分成两个独立事实。</summary>
internal readonly record struct SshOutcomeDecision(SshExecutionStatus Status, string? ErrorCode);

/// <summary>
/// 纯函数判定：**只有明确退出码 0、两条流都读完且没有执行失败时才是 Completed**；
/// 信号结束、输出不完整或未收到退出证据都必须有明确状态/错误码，且**保留已知退出码**。
/// </summary>
internal static class SshOutcomePolicy
{
    public static SshOutcomeDecision Decide(
        bool executionFailed,
        SshExecutionStatus status,
        string? errorCode,
        int? exitCode,
        string? exitSignal,
        bool outputComplete,
        bool outputReadFailed,
        bool outputAborted)
    {
        // 1) 传输/执行层面的失败优先保持原样（例如 connection_lost、timeout）。
        if (executionFailed)
        {
            return new SshOutcomeDecision(status, errorCode);
        }

        // 2) 退出证据：没有 exit-status 时不得伪造 0；信号结束是独立事实。
        if (exitCode is null)
        {
            return exitSignal is null
                ? new SshOutcomeDecision(SshExecutionStatus.Failed, SshErrorCodes.ExitStatusMissing)
                : new SshOutcomeDecision(SshExecutionStatus.Failed, SshErrorCodes.RemoteExitSignal);
        }

        if (exitCode != 0)
        {
            return new SshOutcomeDecision(SshExecutionStatus.Failed, SshErrorCodes.RemoteExitNonzero);
        }

        // 3) 输出完整性：预算截断不算不完整（那种情况仍读到 EOF），读失败/中止才算。
        if (!outputComplete)
        {
            _ = outputReadFailed;
            _ = outputAborted;
            return new SshOutcomeDecision(SshExecutionStatus.Failed, SshErrorCodes.OutputIncomplete);
        }

        return new SshOutcomeDecision(SshExecutionStatus.Completed, null);
    }

    /// <summary>输出不完整原因的稳定 token（<c>null</c> 表示完整）。</summary>
    public static string? IncompleteReason(bool reachedEnd, bool readFailed, bool aborted) => reachedEnd
        ? null
        : readFailed
            ? SshOutputIncompleteReasons.ReadFailed
            : aborted
                ? SshOutputIncompleteReasons.Aborted
                : SshOutputIncompleteReasons.Unknown;
}

/// <summary>输出不完整原因的稳定 token（公开字段 <c>output_incomplete_reason</c>）。</summary>
public static class SshOutputIncompleteReasons
{
    /// <summary>读取流时发生异常（例如通道被撕开）。</summary>
    public const string ReadFailed = "read_failed";

    /// <summary>排空被中止（本端到期/取消/主动 Abort），未读到 EOF。</summary>
    public const string Aborted = "aborted";

    /// <summary>原因不明（实现未提供更细事实）。</summary>
    public const string Unknown = "unknown";
}
