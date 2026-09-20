using PuddingCode.Goals;
using PuddingCode.Models;
using PuddingCode.Tools;

namespace PuddingRuntime.Services.TaskTools;

/// <summary>
/// Agent 侧 Goal 生命周期工具的结果映射（goal_start / goal_pause / goal_cancel 共用）。
/// <para>
/// 与 <see cref="GoalResumeTool"/> 同规：薄适配器不复制服务层判断；成功返回结构化结果，
/// 失败返回结构化 error（稳定 wire code），绝不抛异常。
/// </para>
/// </summary>
internal static class GoalLifecycleToolResults
{
    public static ToolExecutionResult Map(GoalLifecycleResult result)
    {
        if (!result.Success)
        {
            return ToolExecutionResult.Fail(TaskToolJson.Serialize(new
            {
                error = new
                {
                    code = result.Code,
                    message = result.Message,
                    goal_run_id = result.GoalRunId,
                    phase = result.Phase?.ToString(),
                    blocked_code = result.BlockedCode,
                    activation_epoch = result.ActivationEpoch,
                    aggregate_version = result.AggregateVersion,
                },
            }));
        }

        return ToolExecutionResult.Ok(TaskToolJson.Serialize(new GoalLifecycleToolResult
        {
            Success = result.Success,
            Code = result.Code,
            Message = result.Message,
            GoalRunId = result.GoalRunId,
            Objective = result.Objective,
            Phase = result.Phase?.ToString(),
            BlockedCode = result.BlockedCode,
            MaxIterations = result.MaxIterations,
            IterationsStarted = result.IterationsStarted,
            IterationsSettled = result.IterationsSettled,
            ActivationEpoch = result.ActivationEpoch,
            AggregateVersion = result.AggregateVersion,
        }));
    }
}

/// <summary>
/// goal_start — Agent 自主<b>开始</b>一个 Goal（= canonical <c>/goal &lt;objective&gt; [--rounds N]</c>，
/// 见 <see cref="IGoalLifecycleService"/>）。
/// <para>
/// 薄适配器：身份（workspace/session/agent）一律取自运行时上下文，绝不作为工具参数暴露；
/// 会话隔离与非终态唯一性由服务端 <c>GoalRunStore.FindActiveAsync(conversation, agent)</c> 强制。
/// </para>
/// </summary>
[Tool(
    id: "goal_start",
    name: "开始 Goal 执行",
    description: "为当前会话新建并启动一个 Goal（复用 canonical /goal 命令路径）。【何时用】1) 会话没有 Goal，需要就一个明确目标开启自主迭代；2) 上一个 Goal 已是终态（completed/cancelled/failed/budget_exhausted）—— 终态不在 active 集合内，因此 start 会创建一个全新 Goal。【怎么用】objective 必填（1–4000 字符），rounds 可选（1..256，省略用服务端默认）；client_request_id 可选，用于把重试收敛为同一意图（省略时服务层按 (action, 会话, Agent, 内容摘要) 确定性派生，重投不会创建第二个 Goal）。【坑】会话已有非终态 Goal 时 fail-closed 返回 goal_conflict，改变目标必须先用 goal_pause/goal_cancel 终结或改用人工 /goal edit|replace；Goal 功能被 flag 关闭（GoalRuns:Enabled=false）时返回 goal_disabled；**budget_exhausted 不能被本工具延长** —— 「延长额度」是人类权能（/goal extend），Agent 只能新建 Goal。",
    category: ToolCategory.Orchestration,
    permission: ToolPermissionLevel.Low)]
public sealed class GoalStartTool(IGoalLifecycleService service) : PuddingToolBase<GoalStartArgs>
{
    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        GoalStartArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        var result = await service.ExecuteAsync(new GoalLifecycleRequest
        {
            Action = GoalLifecycleAction.Start,
            WorkspaceId = context.WorkspaceId,
            ConversationId = context.SessionId,
            AgentInstanceId = context.AgentInstanceId,
            UserId = context.AgentInstanceId,
            ClientRequestId = args.ClientRequestId,
            Objective = args.Objective,
            Rounds = args.Rounds,
            SourceChannel = GoalLifecycleCodes.AgentToolSourceChannel,
        }, ct);

        return GoalLifecycleToolResults.Map(result);
    }
}

/// <summary>
/// goal_pause — Agent 自主<b>停止</b>当前 Goal 迭代（可恢复；canonical <c>/goal pause [reason]</c>）。
/// </summary>
[Tool(
    id: "goal_pause",
    name: "停止（暂停）Goal 执行",
    description: "暂停当前会话的 Goal：停止后续自主迭代，但保留 Goal 可恢复（phase → paused）。【何时用】需要临时让出控制权、等待人工决策、或阻塞原因尚不明确但必须立即停止迭代时使用。【怎么用】reason 可选（≤4000 字符），写入状态原因与审计；client_request_id 可选。【坑】只作用于当前会话本人 Agent 的 Goal（服务端按 conversation+agent 过滤），不存在跨会话路径；若会话没有非终态 Goal 返回 goal_not_found；暂停不是终结 —— 之后用 goal_resume 续跑，而 goal_cancel 才是不可恢复的终态出口；暂停会推进 activation epoch（因此同一 epoch 内的自动恢复配额会被重置）。",
    category: ToolCategory.Orchestration,
    permission: ToolPermissionLevel.Low)]
public sealed class GoalPauseTool(IGoalLifecycleService service) : PuddingToolBase<GoalPauseArgs>
{
    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        GoalPauseArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        var result = await service.ExecuteAsync(new GoalLifecycleRequest
        {
            Action = GoalLifecycleAction.Pause,
            WorkspaceId = context.WorkspaceId,
            ConversationId = context.SessionId,
            AgentInstanceId = context.AgentInstanceId,
            UserId = context.AgentInstanceId,
            ClientRequestId = args.ClientRequestId,
            Reason = args.Reason,
            SourceChannel = GoalLifecycleCodes.AgentToolSourceChannel,
        }, ct);

        return GoalLifecycleToolResults.Map(result);
    }
}

/// <summary>
/// goal_cancel — Agent 自主<b>取消</b>当前 Goal（终态，不可恢复；canonical <c>/goal cancel [reason]</c>）。
/// </summary>
[Tool(
    id: "goal_cancel",
    name: "取消 Goal",
    description: "取消当前会话的 Goal 并进入终态 cancelled（停止迭代且不可恢复）。【何时用】目标已作废、被新目标取代、或阻塞无法在预算内解除且不应继续占用迭代预算时使用。【怎么用】reason 可选（≤4000 字符），写入取消原因与审计；client_request_id 可选。【坑】这是**不可逆**的终态出口：终态 Goal 不能被 goal_resume 恢复，只能 goal_start 新建；只作用于当前会话本人 Agent 的 Goal；会话无活动 Goal 时返回 goal_not_found；取消会释放 Goal 绑定的任务与续行链路（结算语义），不可当作暂停用 —— 只想临时停止请用 goal_pause。",
    category: ToolCategory.Orchestration,
    permission: ToolPermissionLevel.Low)]
public sealed class GoalCancelTool(IGoalLifecycleService service) : PuddingToolBase<GoalCancelArgs>
{
    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        GoalCancelArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        var result = await service.ExecuteAsync(new GoalLifecycleRequest
        {
            Action = GoalLifecycleAction.Cancel,
            WorkspaceId = context.WorkspaceId,
            ConversationId = context.SessionId,
            AgentInstanceId = context.AgentInstanceId,
            UserId = context.AgentInstanceId,
            ClientRequestId = args.ClientRequestId,
            Reason = args.Reason,
            SourceChannel = GoalLifecycleCodes.AgentToolSourceChannel,
        }, ct);

        return GoalLifecycleToolResults.Map(result);
    }
}

/// <summary>goal_start 参数（身份字段一律由运行时上下文注入）。</summary>
public sealed record GoalStartArgs
{
    [ToolParam("Goal 目标原文（去除首尾空白后 1–4000 字符）。")]
    public string? Objective { get; init; }

    [ToolParam("可选迭代预算 rounds：1..256 的整数；省略用服务端默认（256）。越界返回 invalid_rounds。")]
    public int? Rounds { get; init; }

    [ToolParam("可选幂等键；省略时服务层按 (action, 会话, Agent, 内容摘要) 确定性派生，重投不创建第二个 Goal。")]
    public string? ClientRequestId { get; init; }
}

/// <summary>goal_pause 参数。</summary>
public sealed record GoalPauseArgs
{
    [ToolParam("可选暂停原因（≤4000 字符），写入状态原因与审计。")]
    public string? Reason { get; init; }

    [ToolParam("可选幂等键。")]
    public string? ClientRequestId { get; init; }
}

/// <summary>goal_cancel 参数。</summary>
public sealed record GoalCancelArgs
{
    [ToolParam("可选取消原因（≤4000 字符），写入取消原因与审计。")]
    public string? Reason { get; init; }

    [ToolParam("可选幂等键。")]
    public string? ClientRequestId { get; init; }
}

/// <summary>生命周期工具成功结果（phase 为 GoalPhase wire 字符串，来自服务端快照）。</summary>
public sealed record GoalLifecycleToolResult
{
    public required bool Success { get; init; }

    /// <summary>goal.started / goal.paused / goal.cancelled。</summary>
    public required string Code { get; init; }

    public string? Message { get; init; }

    public string? GoalRunId { get; init; }

    public string? Objective { get; init; }

    public string? Phase { get; init; }

    public string? BlockedCode { get; init; }

    public int? MaxIterations { get; init; }

    public int? IterationsStarted { get; init; }

    public int? IterationsSettled { get; init; }

    public int? ActivationEpoch { get; init; }

    public int? AggregateVersion { get; init; }
}
