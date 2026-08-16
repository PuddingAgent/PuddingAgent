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

    [ToolParam("期望版本，必须等于 Active Task Context 注入的 expected_version 且等于当前 task.Version。")]
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

    [ToolParam("期望版本，必须等于 Active Task Context 注入的 expected_version 且等于当前 task.Version。")]
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
    /// 校验参数与注入的 Active Task Context 一致；返回 null 表示通过，否则返回统一错误体 JSON。
    /// </summary>
    public static string? ValidateActiveTask(
        string taskId,
        string assignmentId,
        int expectedVersion,
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

        if (expectedVersion != active.ExpectedVersion)
        {
            return TaskToolErrors.BuildErrorJson(
                TaskErrorCode.TaskStateConflict,
                $"expected_version {expectedVersion} does not match the Active Task Context expected_version {active.ExpectedVersion}.",
                taskId);
        }

        return null;
    }
}
