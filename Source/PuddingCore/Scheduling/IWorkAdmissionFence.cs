using PuddingCode.Tasks;

namespace PuddingCode.Scheduling;

/// <summary>WorkAdmissionFence 判定结论（ADR-072 §6.3）。</summary>
public enum FenceVerdict
{
    /// <summary>允许（可进入下一步派发/执行）。</summary>
    Allow,

    /// <summary>推迟（携带 nextEligibleAt 语义，本阶段 Fence stub 仅返回 allow）。</summary>
    Defer,

    /// <summary>拒绝（稳定拒绝码）。</summary>
    Deny,
}

/// <summary>Fence 输入：task/agent/policy/time 的最小契约（ADR-072 §6.3 判定顺序）。</summary>
public sealed record WorkAdmissionFenceInput
{
    /// <summary>所属工作区 ID。</summary>
    public required string WorkspaceId { get; init; }

    /// <summary>任务 ID。</summary>
    public required string TaskId { get; init; }

    /// <summary>Assignment ID。</summary>
    public required string AssignmentId { get; init; }

    /// <summary>候选 Agent ID。</summary>
    public required string AgentId { get; init; }

    /// <summary>任务当前状态。</summary>
    public required WorkspaceTaskStatus TaskStatus { get; init; }

    /// <summary>任务优先级。</summary>
    public required TaskPriority Priority { get; init; }

    /// <summary>执行窗口。</summary>
    public required TaskExecutionWindow ExecutionWindow { get; init; }

    /// <summary>判定时点（UTC）。</summary>
    public DateTimeOffset EvaluatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>Fence 输出：verdict + 稳定 code + validUntil（ADR-072 §6.3）。</summary>
public sealed record WorkAdmissionDecision
{
    /// <summary>判定结论。</summary>
    public required FenceVerdict Verdict { get; init; }

    /// <summary>稳定决策码（<see cref="DecisionCode"/>）。</summary>
    public required DecisionCode Code { get; init; }

    /// <summary>结论有效期（Defer 时为下次可派发时间，Allow 可空）。</summary>
    public DateTimeOffset? ValidUntilUtc { get; init; }

    /// <summary>面向操作者的说明。</summary>
    public string? Reason { get; init; }

    /// <summary>便捷构造：Allow。</summary>
    public static WorkAdmissionDecision Allow(
        DecisionCode code,
        DateTimeOffset? validUntilUtc = null,
        string? reason = null)
        => new()
        {
            Verdict = FenceVerdict.Allow,
            Code = code,
            ValidUntilUtc = validUntilUtc,
            Reason = reason,
        };

    /// <summary>便捷构造：Defer。</summary>
    public static WorkAdmissionDecision Defer(
        DecisionCode code,
        DateTimeOffset validUntilUtc,
        string? reason = null)
        => new()
        {
            Verdict = FenceVerdict.Defer,
            Code = code,
            ValidUntilUtc = validUntilUtc,
            Reason = reason,
        };

    /// <summary>便捷构造：Deny。</summary>
    public static WorkAdmissionDecision Deny(DecisionCode code, string? reason = null)
        => new()
        {
            Verdict = FenceVerdict.Deny,
            Code = code,
            Reason = reason,
        };
}

/// <summary>
/// WorkAdmissionFence 契约（ADR-072 §6.4 / ST-01.4）。输入 task/agent/policy/time，
/// 输出 allow/defer/deny + code + validUntil。完整 Fence 逻辑留待 AU-01；本任务仅提供
/// <c>ManualAlwaysAllowFence</c> 占位实现。
/// </summary>
public interface IWorkAdmissionFence
{
    /// <summary>对给定输入做准入判定（纯函数，相同输入得到相同输出）。</summary>
    Task<WorkAdmissionDecision> EvaluateAsync(WorkAdmissionFenceInput input, CancellationToken ct = default);
}
