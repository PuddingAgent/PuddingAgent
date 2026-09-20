namespace PuddingCode.Goals;

/// <summary>
/// ADR-074 §4 / ADR-092：Agent 侧 Goal 生命周期动作。
/// <para>
/// <b>刻意不包含 Extend 与 Policy、Clear</b>：<see cref="GoalCommandKind.Extend"/> 在契约中已被
/// 明确标注为「仅用户 slash / HTTP 入口（与 /goal policy 同级人类权能），不暴露为 agent 侧工具」
/// （见 <c>GoalContracts.cs</c> Extend 注释）。因此「延长额度」与「清除记录」只能由人工入口触发，
/// Agent 工具层不得代理，避免越过人类权能边界。
/// </para>
/// </summary>
public enum GoalLifecycleAction
{
    /// <summary>开始 / 新建 Goal（<see cref="GoalCommandKind.Set"/>）。会话存在非终态 Goal 时 fail-closed 返回 goal_conflict。</summary>
    Start = 1,

    /// <summary>停止（可恢复）：<see cref="GoalCommandKind.Pause"/>。</summary>
    Pause = 2,

    /// <summary>取消（终态）：<see cref="GoalCommandKind.Cancel"/>。</summary>
    Cancel = 3,
}

/// <summary>
/// Agent 侧 Goal 生命周期应用服务（薄适配器边界）。实现必须<b>只复用</b> canonical
/// <see cref="IGoalCommandService"/>.ExecuteAsync 路径，不复制状态机判断、不直写 goal_runs、
/// 不新增第二条创建/暂停/取消路径（与 <see cref="IGoalResumeService"/> 同构）。
/// </summary>
public interface IGoalLifecycleService
{
    /// <summary>
    /// 执行一次生命周期命令。身份三要素（workspace/conversation/agent）由调用方从运行时上下文注入，
    /// 服务端在 <c>GoalRunStore.FindActiveAsync(conversationId, agentInstanceId)</c> 上强制归属过滤，
    /// 因此不存在「跨会话/跨 Agent 操作他人 Goal」的路径。
    /// </summary>
    Task<GoalLifecycleResult> ExecuteAsync(GoalLifecycleRequest request, CancellationToken ct = default);
}

/// <summary>Agent 侧 Goal 生命周期请求。objective/reason 保留调用方原文，不拼接系统指令。</summary>
public sealed record GoalLifecycleRequest
{
    public required GoalLifecycleAction Action { get; init; }

    public required string WorkspaceId { get; init; }

    public required string ConversationId { get; init; }

    public required string AgentInstanceId { get; init; }

    /// <summary>发起方标识（Agent 自主发起时为 Agent 实例身份）。写入 GoalRun.CreatedByUserId 与审计。</summary>
    public required string UserId { get; init; }

    /// <summary>
    /// 可选幂等键。缺省时由服务层按 (action, conversation, agent, 内容摘要) 生成确定性值 ——
    /// 同一意图重投不会创建第二个 Goal（<see cref="GoalCommandKind.Set"/> 按 clientRequestId 幂等重放）。
    /// </summary>
    public string? ClientRequestId { get; init; }

    /// <summary><see cref="GoalLifecycleAction.Start"/> 必填：objective 去除首尾空白后 1–<see cref="GoalLimits.ObjectiveMaxLength"/> 字符。</summary>
    public string? Objective { get; init; }

    /// <summary><see cref="GoalLifecycleAction.Start"/> 可选：迭代预算，须落在 <see cref="GoalLimits.IsValidIterationBudget"/>；null 用服务端默认。</summary>
    public int? Rounds { get; init; }

    /// <summary><see cref="GoalLifecycleAction.Pause"/>/<see cref="GoalLifecycleAction.Cancel"/> 可选原因（≤ <see cref="GoalLimits.ObjectiveMaxLength"/> 字符）。</summary>
    public string? Reason { get; init; }

    /// <summary>审计来源通道；Agent 工具调用固定为 <see cref="GoalLifecycleCodes.AgentToolSourceChannel"/>。</summary>
    public string? SourceChannel { get; init; }
}

/// <summary>
/// 生命周期命令确定性结果。Message 是 presentation，客户端不得解析它驱动按钮；
/// 判定一律以 <see cref="Success"/> + <see cref="Code"/> 为准。
/// </summary>
public sealed record GoalLifecycleResult
{
    public required bool Success { get; init; }

    /// <summary>稳定 wire code：成功为 goal.started / goal.paused / goal.cancelled；失败透传 <see cref="GoalErrorCodes"/> 或 <c>goal.internal_error</c>。</summary>
    public required string Code { get; init; }

    public string? Message { get; init; }

    public string? GoalRunId { get; init; }

    public string? Objective { get; init; }

    public GoalPhase? Phase { get; init; }

    public string? BlockedCode { get; init; }

    public int? MaxIterations { get; init; }

    public int? IterationsStarted { get; init; }

    public int? IterationsSettled { get; init; }

    public int? ActivationEpoch { get; init; }

    public int? AggregateVersion { get; init; }
}

/// <summary>生命周期成功码与 Agent 工具来源通道常量。</summary>
public static class GoalLifecycleCodes
{
    public const string Started = "goal.started";

    public const string Paused = "goal.paused";

    public const string Cancelled = "goal.cancelled";

    /// <summary>Agent 工具调用写入 GoalRun.SourceChannel 的固定值，便于审计区分人工 slash / HTTP 入口。</summary>
    public const string AgentToolSourceChannel = "agent-tool";

    /// <summary>服务层兜底失败码（仅在 canonical 路径抛出未预期异常时使用）。</summary>
    public const string InternalError = "goal.internal_error";

    /// <summary>输入校验失败：objective 缺失/超长。</summary>
    public const string InvalidObjective = GoalErrorCodes.InvalidObjective;

    /// <summary>输入校验失败：rounds 越界或非整数。</summary>
    public const string InvalidRounds = GoalErrorCodes.InvalidRounds;
}
