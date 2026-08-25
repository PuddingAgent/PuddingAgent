namespace PuddingCode.Goals;

/// <summary>
/// ADR-074 §5.1: GoalRun 聚合阶段。created 不持久化 —— 创建事务直接提交 active。
/// completed / cancelled / failed / budget_exhausted 为终态。
/// </summary>
public enum GoalPhase
{
    Active = 1,
    Paused = 2,
    Blocked = 3,
    BudgetExhausted = 4,
    Completed = 5,
    Cancelled = 6,
    Failed = 7,
}

/// <summary>ADR-074 §4: /goal 统一命令动作。</summary>
public enum GoalCommandKind
{
    Status,
    Set,
    Edit,
    Replace,
    Pause,
    Resume,
    Cancel,
    Clear,
}

/// <summary>ADR-074 §3: 外层 Goal Iteration 预算与 objective 边界的唯一硬限制来源。</summary>
public static class GoalLimits
{
    /// <summary>单个 Goal 允许的最大 accepted Goal Iteration 数，用户不可调大。</summary>
    public const int MaxIterationsHardLimit = 256;

    public const int MinIterations = 1;

    /// <summary>--rounds 省略时的默认值，精确为 256。</summary>
    public const int DefaultMaxIterations = MaxIterationsHardLimit;

    /// <summary>objective 去除首尾空白后的 Unicode 字符上限。</summary>
    public const int ObjectiveMaxLength = 4000;

    public static bool IsValidIterationBudget(int value)
        => value is >= MinIterations and <= MaxIterationsHardLimit;
}

/// <summary>ADR-074 §4: 已解析的 /goal 命令。objective/reason 保留用户原文，不拼接系统指令。</summary>
public sealed record GoalCommand
{
    public required GoalCommandKind Kind { get; init; }

    /// <summary>去除首尾空白后的 objective 原文（1–4000 字符）。</summary>
    public string? Objective { get; init; }

    /// <summary>用户显式指定的 --rounds；null 表示使用默认 256。越界值在解析期即拒绝。</summary>
    public int? Rounds { get; init; }

    /// <summary>pause/cancel 的可选自由文本原因。</summary>
    public string? Reason { get; init; }
}

/// <summary>Goal 聚合只读快照。UI / SSE / 审计共用同一服务端投影，不从聊天文本反推。</summary>
public sealed record GoalSnapshot
{
    public required string GoalRunId { get; init; }
    public required string WorkspaceId { get; init; }
    public required string ConversationId { get; init; }
    public required string AgentInstanceId { get; init; }
    public required string Objective { get; init; }
    public required int ObjectiveVersion { get; init; }
    public required GoalPhase Phase { get; init; }
    public string? BlockedCode { get; init; }
    public string? BlockedMessage { get; init; }
    public string? StatusReason { get; init; }
    public required int MaxIterations { get; init; }
    public required int IterationsStarted { get; init; }
    public required int IterationsSettled { get; init; }
    public required int ActivationEpoch { get; init; }
    public required int AggregateVersion { get; init; }
    public string? CreatedByUserId { get; init; }
    public string? SourceChannel { get; init; }
    public string? SourceCommandId { get; init; }
    public string? LastNextAction { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public required DateTimeOffset UpdatedAtUtc { get; init; }
    public DateTimeOffset? TerminalAtUtc { get; init; }
}

/// <summary>G1 阶段 iteration 明细恒为空列表；G2 起由 durable outbox 续行写入。</summary>
public sealed record GoalIterationSnapshot
{
    public required string GoalRunId { get; init; }
    public required int ActivationEpoch { get; init; }
    public required int IterationNo { get; init; }
    public required string Status { get; init; }
    public string? CommandId { get; init; }
    public string? TurnId { get; init; }
    public DateTimeOffset? StartedAtUtc { get; init; }
    public DateTimeOffset? SettledAtUtc { get; init; }
}

/// <summary>结构化 /goal 命令请求（CLI 文本与 Control Plane API 共用）。</summary>
public sealed record GoalCommandRequest(
    string WorkspaceId,
    string ConversationId,
    string AgentInstanceId,
    string UserId,
    string ClientRequestId,
    GoalCommand Command,
    string? SourceChannel = null,
    int? ExpectedVersion = null)
{
    /// <summary>从系统命令原始文本解析并构造请求；解析失败返回 false。</summary>
    public static bool TryCreate(
        string workspaceId,
        string conversationId,
        string agentInstanceId,
        string userId,
        string clientRequestId,
        string commandText,
        string? sourceChannel,
        out GoalCommandRequest? request,
        out string? errorCode,
        out string? errorMessage)
    {
        if (!GoalCommandTextParser.TryParse(commandText, out var command, out errorCode, out errorMessage))
        {
            request = null;
            return false;
        }

        request = new GoalCommandRequest(
            workspaceId, conversationId, agentInstanceId, userId, clientRequestId,
            command, sourceChannel);
        return true;
    }
}

/// <summary>Goal 命令确定性结果。Message 是 presentation，客户端不得解析它驱动按钮。</summary>
public sealed record GoalCommandResult(
    bool Success,
    string? ErrorCode,
    string Message,
    GoalSnapshot? Snapshot)
{
    public static GoalCommandResult Ok(string message, GoalSnapshot snapshot)
        => new(true, null, message, snapshot);

    public static GoalCommandResult Fail(string errorCode, string message, GoalSnapshot? snapshot = null)
        => new(false, errorCode, message, snapshot);
}

/// <summary>Goal 域确定性错误码。</summary>
public static class GoalErrorCodes
{
    public const string GoalDisabled = "goal_disabled";
    public const string InvalidCommand = "invalid_goal_command";
    public const string InvalidObjective = "invalid_objective";
    public const string InvalidRounds = "invalid_rounds";
    public const string GoalNotFound = "goal_not_found";
    public const string GoalConflict = "goal_conflict";
    public const string InvalidState = "invalid_goal_state";
    public const string VersionConflict = "goal_version_conflict";
}
