namespace PuddingRuntime.Services.Improvement.Rsi;

/// <summary>RSI 轨迹中的单个工具调用步（规格 §2.3：结局是一等字段，失败步不得被整体丢弃）。</summary>
public sealed record RsiToolStep
{
    /// <summary>所属回合 Id。</summary>
    public required string TurnId { get; init; }

    /// <summary>工具名（payload 的 name）。</summary>
    public required string ToolName { get; init; }

    /// <summary>稳定排序键（不得依赖 DB 返回顺序）。</summary>
    public required long Sequence { get; init; }

    /// <summary>三态结局：Unknown / Completed / Failed（缺失不是成功）。</summary>
    public required RsiToolOutcome Outcome { get; init; }

    /// <summary>退出码；缺失就是 null，不得填 0 冒充。</summary>
    public int? ExitCode { get; init; }

    /// <summary>错误文本；无错误为 null。</summary>
    public string? Error { get; init; }

    /// <summary>工具输出。</summary>
    public string? Output { get; init; }

    /// <summary>事件发生时间（UTC）。</summary>
    public required DateTimeOffset OccurredAtUtc { get; init; }
}

/// <summary>带结局标注的 RSI 工具轨迹（规格 §2.3）。</summary>
public sealed record RsiTrajectory
{
    public required string WorkspaceId { get; init; }

    public required string AgentInstanceId { get; init; }

    public required string SessionId { get; init; }

    public required string TurnId { get; init; }

    public required IReadOnlyList<RsiToolStep> Steps { get; init; }

    /// <summary>含 Unknown / Failed 步时为 true（供上层显式判断，不得静默丢弃失败步）。</summary>
    public required bool HasOutcomeAnomaly { get; init; }
}

/// <summary>RSI 轨迹装配的事件行输入形状（规格 §2.4：按 (TurnId, Type) 索引查询返回的原始事件投影）。</summary>
public sealed record RsiEventRow
{
    /// <summary>事件类型（tool.call.completed / tool.call.failed / tool.call.requested 等）。</summary>
    public required string Type { get; init; }

    /// <summary>稳定排序键（不得依赖 DB 返回顺序）。</summary>
    public required long Sequence { get; init; }

    /// <summary>payload JSON（原样透传给结局推导器解析）。</summary>
    public string? Payload { get; init; }

    /// <summary>事件发生时间（UTC）。</summary>
    public required DateTimeOffset OccurredAtUtc { get; init; }
}

/// <summary>单个回合的事件切片输入形状（规格 §2.5 推荐路径：按 turn 取事件）。</summary>
public sealed record RsiTurnSlice
{
    public required string WorkspaceId { get; init; }

    public required string AgentInstanceId { get; init; }

    public required string SessionId { get; init; }

    public required string TurnId { get; init; }

    public required IReadOnlyList<RsiEventRow> Events { get; init; }
}
