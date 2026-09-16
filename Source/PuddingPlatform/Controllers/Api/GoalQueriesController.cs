using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PuddingCode.Goals;
using PuddingCode.Tasks;

namespace PuddingPlatform.Controllers.Api;

/// <summary>
/// ADR-074 §10: Goal 只读查询 API。任意入口（Web/Desktop/Connector）共用此投影。
/// </summary>
[Authorize]
[ApiController]
[Route("api/v1")]
public sealed class GoalQueriesController(IGoalQueryService goalQueryService) : ControllerBase
{
    [HttpGet("conversations/{conversationId}/goal")]
    public async Task<IActionResult> GetConversationGoal(
        [FromRoute] string conversationId,
        [FromHeader(Name = "X-Workspace-Id")] string workspaceId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workspaceId))
            return ValidationProblem("Missing X-Workspace-Id header.");

        // 优先返回活动 Goal；无活动 Goal 时返回最近一个（含终态），供 Banner 显示回执。
        var snapshot = await goalQueryService.GetActiveAsync(workspaceId, conversationId, ct)
            ?? await goalQueryService.GetLatestAsync(workspaceId, conversationId, ct);

        return Ok(new { goal = GoalCommandsController.ToDto(snapshot) });
    }

    [HttpGet("goals/{goalId}")]
    public async Task<IActionResult> GetGoal(
        [FromRoute] string goalId,
        CancellationToken ct)
    {
        var snapshot = await goalQueryService.GetAsync(goalId, ct);
        return snapshot is null
            ? Problem(statusCode: 404, title: "goal_not_found", detail: $"Goal '{goalId}' does not exist.")
            : Ok(new { goal = GoalCommandsController.ToDto(snapshot) });
    }

    [HttpGet("goals/{goalId}/iterations")]
    public async Task<IActionResult> GetGoalIterations(
        [FromRoute] string goalId,
        CancellationToken ct)
    {
        var snapshot = await goalQueryService.GetAsync(goalId, ct);
        if (snapshot is null)
            return Problem(statusCode: 404, title: "goal_not_found", detail: $"Goal '{goalId}' does not exist.");

        // G1：iteration 明细恒为空；G2 durable outbox 续行起产生真实条目。
        var iterations = await goalQueryService.GetIterationsAsync(goalId, ct);
        return Ok(new
        {
            goalRunId = goalId,
            iterations = iterations.Select(i => new
            {
                i.IterationNo,
                i.ActivationEpoch,
                status = i.Status,
                commandId = i.CommandId,
                turnId = i.TurnId,
                startedAtUtc = i.StartedAtUtc,
                settledAtUtc = i.SettledAtUtc,
            }),
        });
    }

    /// <summary>
    /// W2：目标 → 步骤（冻结计划 depth1 叶子）+ 校验状态的只读投影。
    /// 与 GET /goals/{goalId} 一致，不要求 X-Workspace-Id；goal 不存在 → 404 goal_not_found；
    /// 有 goal 但无冻结计划 → 200 + hasPlan=false + 空步骤/全 0 计数。
    /// </summary>
    [HttpGet("goals/{goalId}/steps")]
    public async Task<IActionResult> GetGoalSteps(
        [FromRoute] string goalId,
        CancellationToken ct)
    {
        var snapshot = await goalQueryService.GetStepsAsync(goalId, ct);
        if (snapshot is null)
            return Problem(statusCode: 404, title: "goal_not_found", detail: $"Goal '{goalId}' does not exist.");

        return Ok(new GoalStepsResponseDto(
            snapshot.GoalRunId,
            JsonNamingPolicy.SnakeCaseLower.ConvertName(snapshot.Phase.ToString()),
            snapshot.PlanVersion,
            snapshot.HasPlan,
            new GoalStepProgressDto(
                snapshot.Progress.StepsTotal,
                snapshot.Progress.StepsPassed,
                snapshot.Progress.StepsFailed,
                snapshot.Progress.StepsInProgress,
                snapshot.Progress.CurrentStepId),
            snapshot.Steps.Select(step => new GoalStepDto(
                step.NodeId,
                step.SequenceNo,
                step.Kind,
                step.Title,
                step.Status,
                step.StartedAtUtc,
                step.CompletedAtUtc,
                step.BlockerCode,
                step.EvidenceRefs)).ToList(),
            snapshot.Checks.Select(check => new GoalCheckDto(
                check.CheckId,
                check.CriterionId,
                check.Status,
                check.ExitCode,
                check.Summary,
                check.EvidenceRefs)).ToList()));
    }

    /// <summary>
    /// TD-2：目标 → 归属 Agent 的拆解 TODO（只读，面板「拆解区」数据源）。
    /// 与 GET /goals/{goalId}/steps 一致不要求 X-Workspace-Id；goal 不存在 → 404 goal_not_found；
    /// 有 goal 但 Agent 尚未写拆解 → 200 + found=false（与 todo_read 工具同语义，不是错误）。
    /// 归属 Agent 由服务端从 goal 解析（todo_lists 无 workspace 维度，agent_id 隔离硬约束），
    /// 绝不接受客户端传入；本端点只读，面板手改不在本刀范围。
    /// </summary>
    [HttpGet("goals/{goalId}/todo")]
    public async Task<IActionResult> GetGoalTodo(
        [FromRoute] string goalId,
        CancellationToken ct)
    {
        var snapshot = await goalQueryService.GetTodoAsync(goalId, ct);
        return snapshot is null
            ? Problem(statusCode: 404, title: "goal_not_found", detail: $"Goal '{goalId}' does not exist.")
            : Ok(new GoalTodoResponseDto(
                snapshot.GoalRunId,
                snapshot.Found,
                snapshot.ListId,
                snapshot.Title,
                snapshot.Revision,
                snapshot.Items,
                snapshot.Summary));
    }

    /// <summary>步骤进度计数。stepsInProgress 为非终态步骤数（与结算侧当前步骤选定同口径）。</summary>
    public sealed record GoalStepProgressDto(
        int StepsTotal,
        int StepsPassed,
        int StepsFailed,
        int StepsInProgress,
        string? CurrentStepId);

    /// <summary>单个步骤（task_nodes depth1 叶子）。blockerCode：task_nodes 无该列，当前恒为 null。</summary>
    public sealed record GoalStepDto(
        string NodeId,
        int SequenceNo,
        string? Kind,
        string? Title,
        string Status,
        DateTimeOffset? StartedAtUtc,
        DateTimeOffset? CompletedAtUtc,
        string? BlockerCode,
        IReadOnlyList<string> EvidenceRefs);

    /// <summary>
    /// <b>目标级</b>校验状态投影（goal_check_records，仅当前 activation epoch）。
    /// 检查与步骤之间没有任何外键关联 —— 不得把本列表当作 per-step 校验展示。
    /// </summary>
    public sealed record GoalCheckDto(
        string CheckId,
        string CriterionId,
        string Status,
        int? ExitCode,
        string? Summary,
        IReadOnlyList<string> EvidenceRefs);

    public sealed record GoalStepsResponseDto(
        string GoalRunId,
        string Phase,
        int? PlanVersion,
        bool HasPlan,
        GoalStepProgressDto Progress,

        /// <summary>按 sequence_no 升序。</summary>
        IReadOnlyList<GoalStepDto> Steps,
        IReadOnlyList<GoalCheckDto> Checks);

    /// <summary>
    /// TD-2：GET /goals/{goalId}/todo 的响应。Items/Summary 直接复用 <see cref="TodoItemView"/>
    /// 与 <see cref="TodoSummary"/>（camelCase wire：slug/title/status/note/evidenceRef/blockedReason/
    /// orderIndex/startedAtUtc/completedAtUtc、total/pending/inProgress/completed/blocked/currentSlug/
    /// blockedSlugs）—— 面板与 todo_read 工具同一视图（设计 §4），不做二次映射。
    /// </summary>
    public sealed record GoalTodoResponseDto(
        string GoalRunId,
        bool Found,
        string? ListId,
        string? Title,
        int Revision,
        IReadOnlyList<TodoItemView> Items,
        TodoSummary? Summary);
}
