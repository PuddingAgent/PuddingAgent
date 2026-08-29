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
    description: "提交 disposition，由后端状态机解释并推进工作区任务。【何时用】任务执行过程中汇报进展/阻塞/完成/拒绝/回退时使用。【怎么用】task_id、assignment_id 必须等于 Active Task Context 注入值；expected_version 传 worker 最新已知的服务端活版本（优先于注入快照，缺陷 2d5a2ebe），服务端 CAS 校验；disposition 取 accept/progress/todo/blocked/needs_approval/rejected/completed；blocked/rejected/needs_approval 必填 reason；completed 必填 result_summary；progress 必填 progress_summary 或 next_action 之一。【坑】上下文丢失（宿主重启）时经服务端反查 assignment 归属（任务须 InProgress、版本 CAS 匹配）安全重建后继续；迟到调用（已重派/已闭合）返回 assignment.stale/state_conflict/version_conflict；自然语言说“完成”不改变 Task，只有本工具生效。",
    category: ToolCategory.Orchestration,
    permission: ToolPermissionLevel.Low)]
    // 2026-08-28 裁定：task 看板元数据（用户原则：仅直接损坏/泄露用户数据需门禁）
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

        // 缺陷 3f8df399：ActiveTask 因宿主重启丢失时，经服务端反查归属安全重建等效上下文。
        var (guard, rebuiltActiveTask) = await TaskToolGuard.ValidateActiveTaskOrRebuildAsync(
            args.TaskId, args.AssignmentId, args.ExpectedVersion, context, _service, ct, requireInProgress: true);
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

        var active = rebuiltActiveTask!;
        var progressSummary = Merge(args.ProgressSummary, args.NextAction);

        try
        {
            var result = await _service.ApplyDispositionAsync(new TaskAgentUpdateRequest
            {
                WorkspaceId = context.WorkspaceId,
                TaskId = active.TaskId,
                AssignmentId = active.AssignmentId,
                ExpectedVersion = args.ExpectedVersion,
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
