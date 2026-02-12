using System.Collections.Concurrent;

namespace PuddingRuntime.Services.AgentLoop;

/// <summary>单轮执行记录——记录一次 LLM 调用及其工具调用结果的摘要。</summary>
public sealed record TurnRecord
{
    public required int Round { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>本轮 LLM 输出的 status 字段（CONTINUE / DONE / WAIT / FAILED / CANCELLED）。</summary>
    public required string Status { get; init; }

    /// <summary>本轮 message 字段摘要（截断至 512 字符）。</summary>
    public string? MessageSummary { get; init; }

    /// <summary>本轮调用的工具 SkillId；无工具调用时为 null。</summary>
    public string? ToolName { get; init; }

    /// <summary>工具参数 JSON（原始，用于重复检测参考）。</summary>
    public string? ToolArgs { get; init; }

    /// <summary>工具调用是否成功；无工具调用时为 null。</summary>
    public bool? ToolSuccess { get; init; }

    /// <summary>工具错误摘要；工具成功或无工具调用时为 null。</summary>
    public string? ToolError { get; init; }
}

/// <summary>
/// 恢复锚点——当执行进入 WAIT 态时生成，记录恢复执行所需的最小上下文。
/// Controller + Event Bus 凭此锚点在条件命中后通过 DispatchWakeupRequest 重新唤醒 Agent。
/// </summary>
public sealed record ResumeAnchor
{
    public required string AnchorId { get; init; }
    public required string SessionId { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    /// <summary>等待类型（如 "WaitingEvent"、"WaitingApproval"）。</summary>
    public required string WaitType { get; init; }
    /// <summary>LLM 在 WAIT 轮给出的等待原因（来自 meta.reason）。</summary>
    public string? WaitReason { get; init; }
    /// <summary>进入等待时已完成的最后一轮序号（0-based）。</summary>
    public required int LastRound { get; init; }
    public string? TaskPlanId { get; init; }
    public string? TaskNodeId { get; init; }
    public string? ParentTaskNodeId { get; init; }
    public int? DelegationDepth { get; init; }
    public int? MaxDelegationDepth { get; init; }
    public string? RoleInPlan { get; init; }
    public bool? AllowSubDelegation { get; init; }
    public bool? AllowAgentCreation { get; init; }
    public string? AssignedObjective { get; init; }
    public string? ExpectedOutputContract { get; init; }
}

/// <summary>
/// 执行日志——按 Session 记录每轮执行摘要，
/// 供 CompletionPolicy 判定、可观测性审计和 ResumeAnchor 生成使用。
/// </summary>
public sealed class ExecutionJournal
{
    private readonly ConcurrentDictionary<string, List<TurnRecord>> _records = new();
    private readonly ConcurrentDictionary<string, ResumeAnchor> _anchors = new();

    /// <summary>记录一条轮次记录。</summary>
    public void Record(string sessionId, TurnRecord record) =>
        _records.GetOrAdd(sessionId, _ => []).Add(record);

    /// <summary>返回指定 Session 的全部轮次记录（只读）。</summary>
    public IReadOnlyList<TurnRecord> GetTurns(string sessionId) =>
        _records.TryGetValue(sessionId, out var list)
            ? list.AsReadOnly()
            : Array.Empty<TurnRecord>();

    /// <summary>清理指定 Session 的轮次日志（执行永久结束后调用）。</summary>
    public void Clear(string sessionId) =>
        _records.TryRemove(sessionId, out _);

    // ── ResumeAnchor 管理 ──────────────────────────────────────────────────

    /// <summary>设置（或覆盖）指定 Session 的恢复锚点。</summary>
    public void SetAnchor(string sessionId, ResumeAnchor anchor) =>
        _anchors[sessionId] = anchor;

    /// <summary>返回指定 Session 的恢复锚点；不存在时返回 null。</summary>
    public ResumeAnchor? GetAnchor(string sessionId) =>
        _anchors.TryGetValue(sessionId, out var a) ? a : null;

    /// <summary>清理指定 Session 的恢复锚点（唤醒开始时调用）。</summary>
    public void ClearAnchor(string sessionId) =>
        _anchors.TryRemove(sessionId, out _);
}
