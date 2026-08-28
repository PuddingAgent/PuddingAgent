using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PuddingCode.Tasks;
using PuddingCode.Models;
using PuddingCode.Tools;

namespace PuddingRuntime.Services.TaskTools;

/// <summary>
/// task_claim — 认领分配给我的任务（Assigned → InProgress，与 task_update(accept) 共享路径）。
/// </summary>
[Tool(
    id: "task_claim",
    name: "认领工作区任务",
    description: "认领分配给我的工作区任务（Assigned→InProgress）。【何时用】开始执行已分配任务时使用。【怎么用】task_id、assignment_id、expected_version 三个参数都必须等于运行时注入的 Active Task Context 值；重复认领（已 InProgress）幂等 no-op。【坑】正常路径强制要求 Active Task Context 非空；宿主重启导致上下文丢失时，若入参完整且服务端反查确认 assignment 归属当前 Agent（任务状态 Assigned/InProgress、版本 CAS 匹配）则自动重建等效上下文继续；版本不匹配返回 version_conflict；迟到调用（已重派/已闭合）返回 assignment.stale/state_conflict；workspace_id 由运行时注入。Claim the assigned workspace task (Assigned→InProgress); task_id/assignment_id/expected_version must match the injected Active Task Context; idempotent no-op when already InProgress. When the injected context is lost after a host restart, an equivalent context is rebuilt via a server-side ownership lookup.",
    category: ToolCategory.Orchestration,
    permission: ToolPermissionLevel.Low)]
    // 2026-08-28 裁定：task 看板元数据（用户原则：仅直接损坏/泄露用户数据需门禁）
public sealed class TaskClaimTool : PuddingToolBase<TaskClaimArgs>
{
    private readonly ITaskAgentCommandService _service;
    private readonly IOptions<WorkspaceTaskFeatureOptions> _options;

    public TaskClaimTool(
        ITaskAgentCommandService service,
        IOptions<WorkspaceTaskFeatureOptions> options,
        ILogger<TaskClaimTool> logger)
    {
        _service = service;
        _options = options;
    }

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        TaskClaimArgs args,
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
            args.TaskId, args.AssignmentId, args.ExpectedVersion, context, _service, ct, requireInProgress: false);
        if (guard is not null)
        {
            return ToolExecutionResult.Fail(guard);
        }

        var active = rebuiltActiveTask!;
        try
        {
            var result = await _service.ClaimAsync(new TaskAgentClaimRequest
            {
                WorkspaceId = context.WorkspaceId,
                TaskId = active.TaskId,
                AssignmentId = active.AssignmentId,
                ExpectedVersion = active.ExpectedVersion ?? args.ExpectedVersion,
                AgentId = context.AgentInstanceId,
                ExecutionId = context.ExecutionIdentity?.RunId,
                SessionId = context.SessionId,
                TraceId = context.ExecutionIdentity?.TraceId,
            }, ct);

            return ToolExecutionResult.Ok(TaskToolJson.Serialize(new TaskClaimResult
            {
                TaskId = result.TaskId,
                Status = result.Status,
                Version = result.Version,
                AssignmentId = result.AssignmentId,
                AssignmentStatus = result.AssignmentStatus,
                Event = result.Event,
                BoardColumn = result.BoardColumn,
            }));
        }
        catch (TaskStoreException ex)
        {
            return ToolExecutionResult.Fail(TaskToolErrors.BuildErrorJson(ex));
        }
    }
}
