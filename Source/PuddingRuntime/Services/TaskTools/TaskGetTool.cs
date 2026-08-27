using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PuddingCode.Tasks;
using PuddingCode.Models;
using PuddingCode.Tools;

namespace PuddingRuntime.Services.TaskTools;

/// <summary>
/// task_get — 返回单个 Task 的完整详情（允许转换、允许 disposition、Assignment 摘要、近期事件、验收标准）。
/// </summary>
[Tool(
    id: "task_get",
    name: "查看工作区任务详情",
    description: "查看单个工作区任务的完整详情：字段、允许的状态转换、允许的 disposition、当前 Assignment 摘要、近期事件、验收标准。【何时用】认领/更新任务前需要确认当前状态、版本、验收标准时使用。【怎么用】task_id 必填；可选 assignment_id（提供时与当前 active assignment 比对）、events_limit（近期事件条数，1..100，默认 20）。【坑】仅允许查看 active assignment 属于自己的任务；非 mine 任务与不存在返回 task.not_found（信息隐藏）；已取消/已归档任务 board_column 为 null。Get a single workspace task's full detail (fields, allowed transitions/dispositions, active assignment summary, recent events, acceptance criteria).",
    category: ToolCategory.Orchestration,
    permission: ToolPermissionLevel.Low)]
    // 2026-08-28 裁定：task 看板元数据（用户原则：仅直接损坏/泄露用户数据需门禁）
public sealed class TaskGetTool : PuddingToolBase<TaskGetArgs>
{
    private readonly ITaskAgentCommandService _service;
    private readonly IOptions<WorkspaceTaskFeatureOptions> _options;

    public TaskGetTool(
        ITaskAgentCommandService service,
        IOptions<WorkspaceTaskFeatureOptions> options,
        ILogger<TaskGetTool> logger)
    {
        _service = service;
        _options = options;
    }

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        TaskGetArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        if (!_options.Value.Enabled)
        {
            return ToolExecutionResult.Fail(TaskToolErrors.BuildErrorJson(
                TaskErrorCode.CapabilityMissing,
                "Workspace task tools are disabled (WorkspaceTasks.Enabled=false)."));
        }

        try
        {
            var result = await _service.GetAsync(
                context.WorkspaceId,
                args.TaskId,
                context.AgentInstanceId,
                args.EventsLimit ?? 20,
                ct);

            if (result is null)
            {
                return ToolExecutionResult.Fail(TaskToolErrors.BuildErrorJson(
                    TaskErrorCode.TaskNotFound,
                    $"Task '{args.TaskId}' not found.",
                    args.TaskId));
            }

            // assignment_id 提供时与 active assignment 比对（stale 守卫）。
            if (args.AssignmentId is not null
                && !string.Equals(args.AssignmentId, result.Task.ActiveAssignmentId, StringComparison.Ordinal))
            {
                return ToolExecutionResult.Fail(TaskToolErrors.BuildErrorJson(
                    TaskErrorCode.AssignmentStale,
                    $"Assignment '{args.AssignmentId}' is not the active assignment for task '{args.TaskId}'.",
                    args.TaskId));
            }

            return ToolExecutionResult.Ok(TaskToolJson.Serialize(result));
        }
        catch (TaskStoreException ex)
        {
            return ToolExecutionResult.Fail(TaskToolErrors.BuildErrorJson(ex));
        }
    }
}
