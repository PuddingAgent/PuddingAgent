using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PuddingCode.Tasks;
using PuddingCode.Tools;

namespace PuddingRuntime.Services.TaskTools;

/// <summary>
/// TB-06 四工具共享的参数/结果模型与序列化帮助。
/// <para>
/// 工具输出为 canonical JSON：Core 契约 DTO（<see cref="PuddingCode.Tasks.TaskAgentListResult"/> 等）
/// 与工具本地结果 record 统一用 <see cref="TaskToolJson"/>（snake_case + 忽略 null）物化，
/// wire 枚举值由 Platform 侧 <see cref="PuddingPlatform.Services.Tasks.TaskWireMaps"/> 已转好。
/// </para>
/// </summary>
internal static class TaskToolJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = new SnakeCaseNamingPolicy(),
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize(object? value)
        => JsonSerializer.Serialize(value, value?.GetType() ?? typeof(object), Options);

    private sealed class SnakeCaseNamingPolicy : JsonNamingPolicy
    {
        public override string ConvertName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return name;
            }

            var sb = new StringBuilder(name.Length + 8);
            for (var i = 0; i < name.Length; i++)
            {
                var c = name[i];
                if (char.IsUpper(c))
                {
                    if (i > 0)
                    {
                        sb.Append('_');
                    }

                    sb.Append(char.ToLowerInvariant(c));
                }
                else
                {
                    sb.Append(c);
                }
            }

            return sb.ToString();
        }
    }
}

// ── task_list ──────────────────────────────────────────────

public sealed record TaskListArgs
{
    [ToolParam("wire Status 过滤（Backlog/Ready/Deferred/Reserved/Assigned/NeedsReview/InProgress/Blocked/Completed/Failed/Cancelled/Archived）。")]
    public string? Status { get; init; }

    [ToolParam("wire BoardColumn 过滤（Backlog/Todo/InProgress/Done/Failed）；与 status 互斥。")]
    public string? BoardColumn { get; init; }

    [ToolParam("wire Priority 过滤（p0/p1/p2/p3）。")]
    public string? Priority { get; init; }

    [ToolParam("分页大小，1..100，默认 50。")]
    public int? Limit { get; init; }

    [ToolParam("keyset 游标（{sortOrder}|{taskId}），取上一页返回的 next_cursor。")]
    public string? Cursor { get; init; }
}

// ── task_get ───────────────────────────────────────────────

public sealed record TaskGetArgs
{
    [ToolParam("任务 ID。")]
    public required string TaskId { get; init; }

    [ToolParam("Assignment ID；提供时与 task.ActiveAssignmentId 比对，不匹配返回 assignment.stale。")]
    public string? AssignmentId { get; init; }

    [ToolParam("近期事件条数，1..100，默认 20。")]
    public int? EventsLimit { get; init; }
}

// ── task_claim ─────────────────────────────────────────────

public sealed record TaskClaimArgs
{
    [ToolParam("任务 ID，必须等于 Active Task Context 注入的 task_id。")]
    public required string TaskId { get; init; }

    [ToolParam("Assignment ID，必须等于 Active Task Context 注入的 assignment_id。")]
    public required string AssignmentId { get; init; }

    [ToolParam("期望版本：worker 最新已知的服务端活版本（优先于注入快照，缺陷 2d5a2ebe）；服务端 CAS 校验，不符返回 task.version_conflict。")]
    public required int ExpectedVersion { get; init; }
}

/// <summary>task_claim 成功结果（§5.3）。</summary>
public sealed record TaskClaimResult
{
    public required string TaskId { get; init; }
    public required string Status { get; init; }
    public required int Version { get; init; }
    public required string AssignmentId { get; init; }
    public required string AssignmentStatus { get; init; }
    public required string Event { get; init; }
    public required string BoardColumn { get; init; }
}

// ── task_update ────────────────────────────────────────────

public sealed record TaskUpdateArgs
{
    [ToolParam("任务 ID，必须等于 Active Task Context 注入的 task_id。")]
    public required string TaskId { get; init; }

    [ToolParam("Assignment ID，必须等于 Active Task Context 注入的 assignment_id。")]
    public required string AssignmentId { get; init; }

    [ToolParam("期望版本：worker 最新已知的服务端活版本（优先于注入快照，缺陷 2d5a2ebe）；服务端 CAS 校验，不符返回 task.version_conflict。")]
    public required int ExpectedVersion { get; init; }

    [ToolParam("disposition：accept/progress/todo/blocked/needs_approval/rejected/completed。")]
    public required string Disposition { get; init; }

    [ToolParam("blocked/rejected/needs_approval 必填的原因。")]
    public string? Reason { get; init; }

    [ToolParam("进度百分比 0..100。")]
    public int? ProgressPercent { get; init; }

    [ToolParam("进度摘要（progress 时 summary 或 next_action 至少其一）。")]
    public string? ProgressSummary { get; init; }

    [ToolParam("下一步动作（progress 时 summary 或 next_action 至少其一）。")]
    public string? NextAction { get; init; }

    [ToolParam("completed 必填的结果摘要。")]
    public string? ResultSummary { get; init; }

    [ToolParam("completed 可选的结果产物标识列表。")]
    public string[]? Artifacts { get; init; }
}

/// <summary>task_claim / task_update 共享的 Active Task Context 守卫。</summary>
internal static class TaskToolGuard
{
    /// <summary>
    /// 校验 task_id/assignment_id 与注入的 Active Task Context 一致；返回 null 表示通过，否则返回统一错误体 JSON。
    /// <para>
    /// expected_version 不再与注入快照比对（缺陷 2d5a2ebe 移除第一重 CAS 互斥）；
    /// 服务端活版本 CAS 是唯一权威，由调用方/服务端在 Claim/Apply 时裁决。
    /// </para>
    /// </summary>
    public static string? ValidateActiveTask(
        string taskId,
        string assignmentId,
        ToolExecutionContext context)
    {
        if (context.ActiveTask is null)
        {
            return TaskToolErrors.BuildErrorJson(
                TaskErrorCode.TaskActiveContextMissing,
                "task_claim/task_update requires an Active Task Runtime Context; no task was dispatched to this run.",
                taskId);
        }

        var active = context.ActiveTask;
        if (!string.Equals(taskId, active.TaskId, StringComparison.Ordinal))
        {
            return TaskToolErrors.BuildErrorJson(
                TaskErrorCode.TaskStateConflict,
                $"task_id '{taskId}' does not match the Active Task Context task_id '{active.TaskId}'.",
                taskId);
        }

        if (!string.Equals(assignmentId, active.AssignmentId, StringComparison.Ordinal))
        {
            return TaskToolErrors.BuildErrorJson(
                TaskErrorCode.TaskStateConflict,
                $"assignment_id '{assignmentId}' does not match the Active Task Context assignment_id '{active.AssignmentId}'.",
                taskId);
        }

        return null;
    }

    /// <summary>
    /// 缺陷 3f8df399：宿主重启后恢复 session 的新 run 无派发 metadata（context.ActiveTask==null），
    /// task_claim/task_update 直接拒绝导致已 InProgress 的任务永远无法 canonical 关单。
    /// 当且仅当注入上下文缺失时，经任务查询服务反查 assignment 归属，安全重建等效上下文，
    /// 校验强度不低于派发注入（注入路径不做 expected_version 快照比对——缺陷 2d5a2ebe；
    /// 反查路径额外执行 ⑤ 服务端活版本 CAS）：
    ///   ① GetAsync(workspaceId, taskId, 当前 AgentInstanceId)——mine 过滤下非 mine 与不存在统一
    ///      返回 null（Platform 信息隐藏裁决），跨 Agent 伪造无法通过；
    ///   ② active assignment 存在且 AssignmentId 与入参一致（过期/伪造 → assignment.stale，
    ///      与 ClaimAsync 服务端守卫同语义）；
    ///   ③ assignment.AgentId == 当前 Agent（防御性双保险）；
    ///   ④ 状态门槛：claim 受理 Assigned/InProgress（InProgress 由 ClaimAsync 幂等 no-op）；
    ///      update 要求 InProgress；不符 → task.state_conflict（附 current_status）；
    ///   ⑤ task.Version == expected_version（CAS；后续 Claim/Apply 服务端二次 CAS），
    ///      不符 → task.version_conflict（附 current_version）。
    /// 任一不满足则返回原拒绝语义（行为与未引入 fallback 时一致）。查询服务故障（TaskStoreException）
    /// 不在此吞掉，交由调用方既有的 catch 统一映射。
    /// </summary>
    /// <returns>Error 非 null 表示拒绝；否则 ActiveTask 为可继续 canonical 流程的有效上下文。</returns>
    public static async Task<(string? Error, ActiveTaskRuntimeContext? ActiveTask)> ValidateActiveTaskOrRebuildAsync(
        string taskId,
        string assignmentId,
        int expectedVersion,
        ToolExecutionContext context,
        ITaskAgentCommandService service,
        CancellationToken ct,
        bool requireInProgress)
    {
        var error = ValidateActiveTask(taskId, assignmentId, context);
        if (error is null)
        {
            return (null, context.ActiveTask);
        }

        // 注入上下文存在时的参数不匹配是真实调用错误，不做重建。
        if (context.ActiveTask is not null)
        {
            return (error, null);
        }

        // 入参不完整无法反查，保持原拒绝。
        if (string.IsNullOrWhiteSpace(taskId) || string.IsNullOrWhiteSpace(assignmentId))
        {
            return (error, null);
        }

        var lookup = await service.GetAsync(context.WorkspaceId, taskId, context.AgentInstanceId, eventsLimit: 1, ct);
        if (lookup is null)
        {
            // mine 信息隐藏：任务不存在或归属其他 Agent → 无法安全重建，保持原拒绝（不泄露归属）。
            return (error, null);
        }

        var assignment = lookup.ActiveAssignment;
        if (assignment is null
            || !string.Equals(assignment.AssignmentId, assignmentId, StringComparison.Ordinal))
        {
            return (TaskToolErrors.BuildErrorJson(
                TaskErrorCode.AssignmentStale,
                $"Assignment '{assignmentId}' is not the active assignment for task '{taskId}'.",
                taskId,
                lookup.Task.Version,
                lookup.Task.Status), null);
        }

        if (!string.Equals(assignment.AgentId, context.AgentInstanceId, StringComparison.Ordinal))
        {
            return (error, null);
        }

        var statusOk = requireInProgress
            ? string.Equals(lookup.Task.Status, "InProgress", StringComparison.Ordinal)
            : lookup.Task.Status is "Assigned" or "InProgress";
        if (!statusOk)
        {
            return (TaskToolErrors.BuildErrorJson(
                TaskErrorCode.TaskStateConflict,
                $"Task '{taskId}' is in state '{lookup.Task.Status}'; {(requireInProgress ? "task_update" : "task_claim")} requires {(requireInProgress ? "InProgress" : "Assigned or InProgress")}.",
                taskId,
                lookup.Task.Version,
                lookup.Task.Status), null);
        }

        if (lookup.Task.Version != expectedVersion)
        {
            return (TaskToolErrors.BuildErrorJson(
                TaskErrorCode.TaskVersionConflict,
                $"Task '{taskId}' version conflict: expected {expectedVersion}, actual {lookup.Task.Version}.",
                taskId,
                lookup.Task.Version), null);
        }

        var rebuilt = new ActiveTaskRuntimeContext
        {
            WorkspaceId = context.WorkspaceId,
            TaskId = taskId,
            AssignmentId = assignmentId,
            AgentId = context.AgentInstanceId,
            Origin = lookup.Task.Origin ?? string.Empty,
            Priority = lookup.Task.Priority,
            ExecutionWindow = lookup.Task.ExecutionWindow,
            ExpectedVersion = expectedVersion,
        };
        return (null, rebuilt);
    }
}
