namespace PuddingCode.Tasks;

/// <summary>
/// ADR-072 §9.1：metadata → <see cref="ActiveTaskRuntimeContext"/> 的唯一映射器。
/// <para>
/// 语义与派发路径（<c>AgentInvocationDispatchFactory.BuildActiveTask</c>）逐键一致：
/// task_id/assignment_id 等键均支持 snake/camel/Pascal 三别名（按序取首个非空白值）；
/// <c>task_id</c> 或 <c>assignment_id</c> 任一缺失即返回 <c>null</c>（fail-safe）；
/// <c>origin</c>/<c>priority</c>/<c>execution_window</c> 缺省为 <see cref="string.Empty"/>；
/// 不填 <c>DeliveryId</c>（仅投递路径 binding 侧赋值）。
/// canonical 命令路径（ExecutionCommandReader）与投递路径共用本映射器，禁止两处各自演化。
/// </para>
/// </summary>
public static class ActiveTaskMetadata
{
    /// <summary>
    /// 从命令/envelope metadata 构建 <see cref="ActiveTaskRuntimeContext"/>。
    /// 缺 <c>task_id</c> 或 <c>assignment_id</c>（含全空白）时返回 <c>null</c>。
    /// </summary>
    public static ActiveTaskRuntimeContext? TryBuild(
        string workspaceId,
        string agentId,
        IReadOnlyDictionary<string, string>? metadata)
    {
        var taskId = GetValue(metadata, "task_id", "taskId", "TaskId");
        if (string.IsNullOrWhiteSpace(taskId))
        {
            return null;
        }

        var assignmentId = GetValue(metadata, "assignment_id", "assignmentId", "AssignmentId");
        if (string.IsNullOrWhiteSpace(assignmentId))
        {
            return null;
        }

        return new ActiveTaskRuntimeContext
        {
            WorkspaceId = workspaceId,
            TaskId = taskId,
            AssignmentId = assignmentId,
            AgentId = agentId,
            Origin = GetValue(metadata, "origin", "Origin") ?? string.Empty,
            Priority = GetValue(metadata, "priority", "Priority") ?? string.Empty,
            ExecutionWindow = GetValue(metadata, "execution_window", "executionWindow", "ExecutionWindow") ?? string.Empty,
            ExpectedVersion = GetInt(metadata, "expected_version", "expectedVersion", "ExpectedVersion"),
            PolicyVersion = GetValue(metadata, "policy_version", "policyVersion", "PolicyVersion"),
            DispatchIdempotencyKey = GetValue(metadata, "dispatch_idempotency_key", "dispatchIdempotencyKey", "DispatchIdempotencyKey"),
            ReservationFencingToken = GetValue(metadata, "reservation_fencing_token", "reservationFencingToken", "ReservationFencingToken"),
        };
    }

    private static string? GetValue(IReadOnlyDictionary<string, string>? metadata, params string[] keys)
    {
        if (metadata is null)
        {
            return null;
        }

        foreach (var key in keys)
        {
            if (metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static int? GetInt(IReadOnlyDictionary<string, string>? metadata, params string[] keys)
        => int.TryParse(GetValue(metadata, keys), out var value) ? value : null;
}
