using PuddingCode.Models;
using PuddingCode.Tasks;
using PuddingCode.Tools;

namespace PuddingRuntime.Services.TaskTools;

/// <summary>
/// task_goal_start — Agent 自主进入 Goal 模式（单卡派发，复用 canonical 调度链
/// Evaluator → Starter → TransactionStore，见 PuddingCore 契约 <see cref="ITaskGoalLaunchService"/>）。
/// <para>
/// 薄适配器：身份（workspace/agent/session）一律取自运行时上下文，绝不作为工具参数暴露；
/// 拒绝路径透传 <see cref="TaskGoalLaunchCodes"/> 稳定 wire code（结构化失败、不抛异常）；
/// 启动路径 reservation_id / task_plan_id 上游不透出，诚实留空、不伪造（契约 §4）。
/// </para>
/// </summary>
[Tool(
    id: "task_goal_start",
    name: "以 Goal 模式启动任务",
    description: "以 Goal 模式启动指定看板卡（单卡派发，复用 canonical 调度链）。【何时用】Agent 自主把一张 Backlog/Ready 卡转入 Goal 迭代执行时使用。【怎么用】task_id 必填；expected_version 可选 CAS（不符返回 task.version_conflict）；iteration_budget 可选请求迭代预算（服务端取 min(请求, 配置上限)，不得抬高，提供时必须 ≥ 1）；reason 可选，写入结果与审计。【坑】workspace/agent/session 身份由运行时上下文注入、不作为参数；同一卡已有活跃 Goal 绑定时幂等返回 already_running 与既有 goal_run_id；拒绝（task.not_found / task_held_by_other_agent / scheduler_disabled / task_not_dispatchable 等）返回结构化失败 code 而非异常；启动路径 reservation_id/task_plan_id 不可得（诚实留空）。",
    category: ToolCategory.Orchestration,
    permission: ToolPermissionLevel.Low)]
    // 2026-08-28 裁定：task 看板元数据（用户原则：仅直接损坏/泄露用户数据需门禁）
public sealed class TaskGoalStartTool(ITaskGoalLaunchService service) : PuddingToolBase<TaskGoalStartArgs>
{
    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        TaskGoalStartArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(args.TaskId))
        {
            return ToolExecutionResult.Fail("task_id is required.");
        }

        if (args.IterationBudget is < 1)
        {
            return ToolExecutionResult.Fail("iteration_budget must be >= 1 when provided.");
        }

        var result = await service.LaunchAsync(new TaskGoalLaunchRequest
        {
            WorkspaceId = context.WorkspaceId,
            TaskId = args.TaskId.Trim(),
            AgentId = context.AgentInstanceId,
            ConversationId = context.SessionId,
            ExpectedVersion = args.ExpectedVersion,
            IterationBudget = args.IterationBudget,
            Reason = args.Reason,
        }, ct);

        if (!result.Started)
        {
            // 结构化失败（非异常）：wire code 原样保留，便于 Agent 与面板识别（设计 §4）。
            return ToolExecutionResult.Fail(TaskToolJson.Serialize(new
            {
                error = new
                {
                    code = result.Code,
                    message = result.Message,
                    task_id = args.TaskId,
                    current_version = result.TaskVersion,
                    goal_run_id = result.GoalRunId,
                    assignment_id = result.AssignmentId,
                },
            }));
        }

        return ToolExecutionResult.Ok(TaskToolJson.Serialize(new TaskGoalStartResult
        {
            Started = true,
            Code = result.Code,
            GoalRunId = result.GoalRunId,
            AssignmentId = result.AssignmentId,
            TaskVersion = result.TaskVersion,
            Message = result.Message,
        }));
    }
}

/// <summary>task_goal_start 参数（身份字段一律由运行时上下文注入，见契约 §4）。</summary>
public sealed record TaskGoalStartArgs
{
    [ToolParam("目标看板卡 ID（必填）。")]
    public required string TaskId { get; init; }

    [ToolParam("可选 CAS：期望版本，不符返回 task.version_conflict。")]
    public int? ExpectedVersion { get; init; }

    [ToolParam("可选迭代预算请求：服务端取 min(请求, 配置上限)，不得抬高；提供时必须 ≥ 1。")]
    public int? IterationBudget { get; init; }

    [ToolParam("可选原因（进入结果 message 与审计日志）。")]
    public string? Reason { get; init; }
}

/// <summary>task_goal_start 成功结果（started=true，code 为 started 或 already_running 幂等码）。</summary>
public sealed record TaskGoalStartResult
{
    public required bool Started { get; init; }

    public required string Code { get; init; }

    public string? GoalRunId { get; init; }

    public string? AssignmentId { get; init; }

    public int? TaskVersion { get; init; }

    public required string Message { get; init; }
}
