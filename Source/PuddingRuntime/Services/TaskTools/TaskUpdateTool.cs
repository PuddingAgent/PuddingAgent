using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PuddingCode.Tasks;
using PuddingCode.Models;
using PuddingCode.Tools;

namespace PuddingRuntime.Services.TaskTools;

/// <summary>
/// task_update — 提交 disposition，后端状态机解释（复用 TaskStateMachine.TryInterpretDisposition）。
/// </summary>
[Tool(
    id: "task_update",
    name: "更新工作区任务",
    description: "提交 disposition，由后端状态机解释并推进工作区任务。【何时用】任务执行过程中汇报进展/阻塞/完成/拒绝/回退时使用。【怎么用】task_id、assignment_id、expected_version 必须等于 Active Task Context 注入值；disposition 取 accept/progress/todo/blocked/needs_approval/rejected/completed；blocked/rejected/needs_approval 必填 reason；completed 必填 result_summary；progress 必填 progress_summary 或 next_action 之一。【坑】迟到调用（已重派/已闭合）返回 assignment.stale/state_conflict/version_conflict；自然语言说“完成”不改变 Task，只有本工具生效。Submit a disposition (accept/progress/todo/blocked/needs_approval/rejected/completed) to advance the task; required fields depend on disposition (reason for blocked/rejected/needs_approval, result_summary for completed, progress_summary/next_action for progress).",
    category: ToolCategory.Orchestration,
    permission: ToolPermissionLevel.Medium)]
public sealed class TaskUpdateTool : PuddingToolBase<TaskUpdateArgs>
{
    private readonly ITaskAgentCommandService _service;
    private readonly IOptions<WorkspaceTaskFeatureOptions> _options;

    public TaskUpdateTool(
        ITaskAgentCommandService service,
        IOptions<WorkspaceTaskFeatureOptions> options,
        ILogger<TaskUpdateTool> logger)
    {
        _service = service;
        _options = options;
    }

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        TaskUpdateArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        if (!_options.Value.Enabled)
        {
            return ToolExecutionResult.Fail(TaskToolErrors.BuildErrorJson(
                TaskErrorCode.CapabilityMissing,
                "Workspace task tools are disabled (WorkspaceTasks.Enabled=false)."));
        }

        var guard = TaskToolGuard.ValidateActiveTask(args.TaskId, args.AssignmentId, args.ExpectedVersion, context);
        if (guard is not null)
        {
            return ToolExecutionResult.Fail(guard);
        }

        // disposition 未知 → invalid_disposition 422。
        var disposition = args.Disposition?.Trim() ?? string.Empty;
        if (!IsKnownDisposition(disposition))
        {
            return ToolExecutionResult.Fail(TaskToolErrors.BuildErrorJson(
                TaskErrorCode.TaskInvalidDisposition,
                $"Unknown disposition '{disposition}'. Use accept/progress/todo/blocked/needs_approval/rejected/completed.",
                args.TaskId));
        }

        // 必填规则（§5.4）。
        if (disposition is "blocked" or "rejected" or "needs_approval"
            && string.IsNullOrWhiteSpace(args.Reason))
        {
            return ToolExecutionResult.Fail(TaskToolErrors.BuildErrorJson(
                TaskErrorCode.TaskReasonRequired,
                $"disposition '{disposition}' requires a non-empty reason.",
                args.TaskId));
        }

        if (disposition == "progress"
            && string.IsNullOrWhiteSpace(args.ProgressSummary)
            && string.IsNullOrWhiteSpace(args.NextAction))
        {
            return ToolExecutionResult.Fail(TaskToolErrors.BuildErrorJson(
                TaskErrorCode.TaskReasonRequired,
                "disposition 'progress' requires progress_summary or next_action.",
                args.TaskId));
        }

        if (disposition == "completed" && string.IsNullOrWhiteSpace(args.ResultSummary))
        {
            return ToolExecutionResult.Fail(TaskToolErrors.BuildErrorJson(
                TaskErrorCode.TaskResultRequired,
                "disposition 'completed' requires a non-empty result_summary.",
                args.TaskId));
        }

        if (args.ProgressPercent is { } percent && (percent < 0 || percent > 100))
        {
            return ToolExecutionResult.Fail("progress_percent must be between 0 and 100.");
        }

        if (args.Artifacts is not null && args.Artifacts.Any(string.IsNullOrWhiteSpace))
        {
            return ToolExecutionResult.Fail("artifacts must contain only non-empty strings.");
        }

        var active = context.ActiveTask!;
        var progressSummary = Merge(args.ProgressSummary, args.NextAction);

        try
        {
            var result = await _service.ApplyDispositionAsync(new TaskAgentUpdateRequest
            {
                WorkspaceId = context.WorkspaceId,
                TaskId = active.TaskId,
                AssignmentId = active.AssignmentId,
                ExpectedVersion = active.ExpectedVersion ?? args.ExpectedVersion,
                Disposition = disposition,
                AgentId = context.AgentInstanceId,
                Reason = args.Reason,
                ProgressPercent = args.ProgressPercent,
                ProgressSummary = progressSummary,
                ResultSummary = args.ResultSummary,
                Artifacts = args.Artifacts,
                ExecutionId = context.ExecutionIdentity?.RunId,
                SessionId = context.SessionId,
                TraceId = context.ExecutionIdentity?.TraceId,
            }, ct);

            return ToolExecutionResult.Ok(TaskToolJson.Serialize(result));
        }
        catch (TaskStoreException ex)
        {
            return ToolExecutionResult.Fail(TaskToolErrors.BuildErrorJson(ex));
        }
    }

    private static bool IsKnownDisposition(string disposition) => disposition is
        "accept" or "progress" or "todo" or "blocked" or "needs_approval" or "rejected" or "completed";

    private static string? Merge(string? progressSummary, string? nextAction)
    {
        var parts = new[] { progressSummary, nextAction }
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToArray();
        return parts.Length == 0 ? null : string.Join("\n", parts);
    }
}
