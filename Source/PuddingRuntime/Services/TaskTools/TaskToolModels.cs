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

// Stage 2（D5）只读边界（本文件即执行者侧工具的参数/结果面）：
//   ① 执行者侧四个工具（task_list / task_get / task_claim / task_update）的参数模型
//      <b>一律不提供父层级写参数</b>（无 parent_task_id、无 clear_parent）；父子关系仅管理者
//      （ManageTasksTool 的 ManageTasksArgs.parent_task_id / clear_parent）可写。
//   ② 执行者侧只能从「结果」侧只读看到父层级标记：TaskListArgs / TaskGetArgs 无入参，
//      返回物为 Core 契约 DTO（TaskAgentListItem.parent_task_id / is_container、
//      TaskAgentTaskDetail.parent_task_id / is_container / child_task_count / completed_child_count），
//      由本目录工具直接序列化，无本地包装 record；这些字段由
//      PuddingPlatform.Services.Tasks.TaskAgentCommandService 的 ToListItem / ToTaskDetail 填充。

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

    /// <summary>
    /// Stage 3（D5 收口）：父任务 ID，<b>只读</b>暴露——执行者侧无任何父子关系写参数
    /// （本目录四个工具的 Args 均无 parent_task_id / clear_parent）。
    /// 由 <c>TaskAgentCommandService.BuildMutationResult</c> 从任务实体填充。
    /// </summary>
    public string? ParentTaskId { get; init; }

    /// <summary>
    /// Stage 3（D2 收口，只读）：是否为容器（存在直接子卡）。容器不可 claim，
    /// 故 claim 成功路径恒为 false；字段仅作只读展示，不参与任何状态派生（D3）。
    /// </summary>
    public bool IsContainer { get; init; }
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
    /// <summary>Active Task Context 缺失的统一消息（反查重建失败时附加 context_rebuild 诊断）。</summary>
    private const string ActiveContextMissingMessage =
        "task_claim/task_update requires an Active Task Runtime Context; no task was dispatched to this run.";

    /// <summary>
    /// 反查重建失败时的拒绝体（卡 3133b149）：错误码与 mine 信息隐藏策略与纯注入路径完全一致，
    /// 仅附加非泄露诊断 context_rebuild{attempted,stage,outcome}，
    /// 使心跳/子代理 run 的 active_context_missing 不再与「平台未注入」混为一谈。
    /// </summary>
    private static string BuildRebuildRejectedError(
        string taskId,
        bool attempted,
        string stage,
        string outcome)
        => TaskToolErrors.BuildErrorJson(
            TaskErrorCode.TaskActiveContextMissing,
            ActiveContextMissingMessage,
            taskId,
            contextRebuild: new TaskContextRebuildDiagnostics
            {
                Attempted = attempted,
                Stage = stage,
                Outcome = outcome,
            });

    public static string? ValidateActiveTask(
        string taskId,
        string assignmentId,
        ToolExecutionContext context)
    {
        if (context.ActiveTask is null)
        {
            return TaskToolErrors.BuildErrorJson(
                TaskErrorCode.TaskActiveContextMissing,
                ActiveContextMissingMessage,
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
    ///      update 受理 InProgress/Blocked（卡 813ad427：Blocked 且 active assignment 归属当前 Agent 时
    ///      允许 canonical 恢复上报，合法 disposition 仍由服务端状态机 fail closed 裁决）；
    ///      不符 → task.state_conflict（附 current_status）；
    ///   ⑤ task.Version == expected_version（CAS；后续 Claim/Apply 服务端二次 CAS），
    ///      不符 → task.version_conflict（附 current_version）。
    /// 任一不满足则返回原拒绝语义（错误码与 mine 信息隐藏策略均不变），并附加非泄露诊断
    /// context_rebuild{attempted,stage,outcome}（卡 3133b149）：inputs/incomplete_inputs、
    /// lookup/not_visible、ownership/agent_mismatch——使「平台未注入」与「卡不属于我」可区分。
    /// 查询服务故障（TaskStoreException）不在此吞掉，交由调用方既有的 catch 统一映射。
    /// </summary>
    /// <returns>Error 非 null 表示拒绝；否则 ActiveTask 为可继续 canonical 流程的有效上下文。</returns>
    public static async Task<(string? Error, ActiveTaskRuntimeContext? ActiveTask)> ValidateActiveTaskOrRebuildAsync(
        string taskId,
        string assignmentId,
        int expectedVersion,
        ToolExecutionContext context,
        ITaskAgentCommandService service,
        CancellationToken ct,
        bool allowBlockedRecovery)
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

        // 入参不完整无法反查，保持原拒绝（diagnostics: inputs/incomplete_inputs）。
        if (string.IsNullOrWhiteSpace(taskId) || string.IsNullOrWhiteSpace(assignmentId))
        {
            return (BuildRebuildRejectedError(taskId, attempted: false, stage: "inputs", outcome: "incomplete_inputs"), null);
        }

        var lookup = await service.GetAsync(context.WorkspaceId, taskId, context.AgentInstanceId, eventsLimit: 1, ct);
        if (lookup is null)
        {
                        // mine 信息隐藏：任务不存在或归属其他 Agent → 无法安全重建，保持原拒绝（不泄露归属）。
            return (BuildRebuildRejectedError(taskId, attempted: true, stage: "lookup", outcome: "not_visible"), null);
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
            return (BuildRebuildRejectedError(taskId, attempted: true, stage: "ownership", outcome: "agent_mismatch"), null);
        }

        // 卡 813ad427（2026-09-14 裁定）：Blocked + active assignment 归属当前 Agent 时，其所属
        // Task-bound Goal 必须能 canonical 上报，不得只留「管理者手工改状态」一条路。
        // 状态机已允许 Blocked→Ready（TryInterpretDisposition(Todo)）与 Blocked→Failed（MarkFailed），
        // 故此处只放开「能否重建上下文」，disposition 的合法性仍由服务端状态机裁决：
        // Blocked 下 progress/completed/blocked/needs_approval 仍返回 task.state_conflict。
        var statusOk = allowBlockedRecovery
            ? lookup.Task.Status is "InProgress" or "Blocked"
            : lookup.Task.Status is "Assigned" or "InProgress";
        if (!statusOk)
        {
            return (TaskToolErrors.BuildErrorJson(
                TaskErrorCode.TaskStateConflict,
                $"Task '{taskId}' is in state '{lookup.Task.Status}'; {(allowBlockedRecovery ? "task_update" : "task_claim")} requires {(allowBlockedRecovery ? "InProgress or Blocked" : "Assigned or InProgress")}.",
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
