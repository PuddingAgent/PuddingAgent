using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using PuddingCode.Scheduling;
using PuddingCode.Tasks;
using PuddingPlatform.Data.Entities;

namespace PuddingTaskRecall.Cli;

// ═════════════════════════ 结果模型 ═════════════════════════

public sealed record TaskStatusCount(int Status, string StatusName, int Count);

public sealed record AttemptStatusCount(int Status, string StatusName, int Count, int UnreleasedCount);

public sealed record BindingSnapshot(
    int Total,
    int AnyEmpty,
    int ExecutionIdEmpty,
    int SessionIdEmpty,
    int BothEmpty,
    int RepairableOnTerminalTask,
    int PendingClaimOnLiveTask);

public sealed record ProjectionSnapshot(int Total, int Expired, int WouldDelete, int SkippedLiveTask);

public sealed record CompletedEventSample(
    string TaskId,
    long Sequence,
    string? AssignmentId,
    string? AgentId,
    string? ExecutionId,
    string? SessionId,
    long? Origin,
    string? DecisionCode,
    string? CorrelationId,
    string? CausationId,
    string CreatedAtUtc);

/// <summary>A 类修复明细：Completed(status=8) 但缺 TaskCompleted(event_type=10) 事实的任务。</summary>
public sealed record TaskCompletedBackfill
{
    public required string TaskId { get; init; }
    public required string WorkspaceId { get; init; }
    public required string Title { get; init; }
    public required long Version { get; init; }
    public required long Sequence { get; init; }
    public required string EventId { get; init; }
    public required string CreatedAtUtc { get; init; }
    /// <summary>created_at 取值依据：completed_at_utc | updated_at_utc | backfill_now。</summary>
    public required string CreatedAtSource { get; init; }
    public string? AssignmentId { get; init; }
    public string? AgentId { get; init; }
    public long? Origin { get; init; }
}

/// <summary>B 类明细：task_execution_bindings 中 execution_id/session_id 为空（NULL 或 ''）的行。</summary>
public sealed record BindingNormalization
{
    public required long Id { get; init; }
    public required string TaskId { get; init; }
    public required string AssignmentId { get; init; }
    public required string DeliveryId { get; init; }
    public required string? ExecutionIdFrom { get; init; }
    public required string? SessionIdFrom { get; init; }
    public required string TaskStatusName { get; init; }
    public required bool WillRepair { get; init; }
    public string Reason { get; init; } = "";
}

/// <summary>C 类明细：task_assignment_attempts 中 status IN (Reserved=0, Assigned=1) 且未释放的行。</summary>
public sealed record AttemptRelease
{
    public required string AttemptId { get; init; }
    public required string TaskId { get; init; }
    public required string AgentId { get; init; }
    public required int StatusFrom { get; init; }
    public required string StatusFromName { get; init; }
    public required int StatusTo { get; init; }
    public required string StatusToName { get; init; }
    public required string TaskStatusName { get; init; }
    public required bool WillRepair { get; init; }
    public string Reason { get; init; } = "";
}

/// <summary>attempts 观察行（status=4 人工裁决清单 / 其他未释放观察）。</summary>
public sealed record AttemptRow(
    string AttemptId,
    string TaskId,
    string AgentId,
    int Status,
    string StatusName,
    string? ReleasedAtUtc,
    string TaskStatusName);

/// <summary>D 类明细：agent_availability_projection 中 valid_until 已过期的陈旧投影。</summary>
public sealed record ProjectionCleanup
{
    public required long Id { get; init; }
    public required string WorkspaceId { get; init; }
    public required string AgentId { get; init; }
    public required int State { get; init; }
    public required string StateName { get; init; }
    public required int ActivityReason { get; init; }
    public required string ActivityReasonName { get; init; }
    public required string ReasonCode { get; init; }
    public required string ValidUntilUtc { get; init; }
    public string? ActiveTaskId { get; init; }
    public required bool WillDelete { get; init; }
    public string Reason { get; init; } = "";
}

public sealed record ApplyOutcome
{
    public required bool Committed { get; init; }
    public required string? BackupPath { get; init; }
    public required int EventsInserted { get; init; }
    public required int BindingsUpdated { get; init; }
    public required int AttemptsReleased { get; init; }
    public required int ProjectionsDeleted { get; init; }
    public string? Error { get; init; }
}

public sealed record TaskRecallResult
{
    public required DateTimeOffset RunAtUtc { get; init; }
    public required string DatabasePath { get; init; }
    public required bool Applied { get; init; }
    public required IReadOnlyList<TaskStatusCount> TaskStatusDistribution { get; init; }
    public required IReadOnlyList<AttemptStatusCount> AttemptStatusDistribution { get; init; }
    public required BindingSnapshot Bindings { get; init; }
    public required ProjectionSnapshot Projections { get; init; }
    public required IReadOnlyList<CompletedEventSample> PositiveSamples { get; init; }
    public required IReadOnlyList<TaskCompletedBackfill> MissingCompletedFacts { get; init; }
    public required IReadOnlyList<BindingNormalization> BindingNormalizations { get; init; }
    public required IReadOnlyList<AttemptRelease> AttemptReleases { get; init; }
    public required IReadOnlyList<ProjectionCleanup> ProjectionCleanups { get; init; }
    public required IReadOnlyList<AttemptRow> Status4Adjudication { get; init; }
    public required IReadOnlyList<AttemptRow> OtherUnreleasedObservations { get; init; }
    public ApplyOutcome? ApplyResult { get; init; }

    public int RepairableBindings => BindingNormalizations.Count(item => item.WillRepair);
    public int RepairableAttempts => AttemptReleases.Count(item => item.WillRepair);
    public int DeletableProjections => ProjectionCleanups.Count(item => item.WillDelete);
}

// ═════════════════════════ 引擎 ═════════════════════════

/// <summary>
/// 看板卡 4ed930e7 原子任务③：历史脏数据一次性诊断/修复引擎（默认 dry-run）。
/// <para>
/// 四类对象（枚举值域以源码为准，绝不按旧四值硬编码）：
/// A. workspace_tasks status=Completed(8) 且无 TaskCompleted(event_type=10) 事件 → 按
///    TaskCompletionSettlementService.settlement_backfill_completed 的字段结构补写事件；
/// B. task_execution_bindings 空 execution_id/session_id → 任务已终态时回填显式占位值
///    "no-execution"（写入侧惯例是 ??= 幂等回填，占位值不会与真实 RunId 冲突）；
/// C. task_assignment_attempts status IN (Reserved=0, Assigned=1) 未释放 → 按
///    TaskExecutionRepairCoordinator 的终态释放语义（任务 Completed→Completed，其他终态→Failed）；
/// D. agent_availability_projection valid_until 已过期 → 删除；但 active_task_id 指向
///    仍存活（非终态）任务的投影运行时动态校验后跳过。
/// </para>
/// </summary>
public static class TaskRecallAuditEngine
{
    public const string DecisionCode = "recall_backfill_completed";
    public const string NoExecutionPlaceholder = "no-execution";
    public const string CausationFallback = "recall-4ed930e7";

    private const int TaskCompletedStatus = (int)WorkspaceTaskStatus.Completed;
    private const int TaskCompletedEvent = (int)TaskEventType.TaskCompleted;

    /// <summary>只读诊断：重新执行侦查查询取最新快照（不依赖任何静态数字），不做任何写操作。</summary>
    public static TaskRecallResult Analyze(SqliteConnection connection, string databasePath, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var taskStatusDistribution = ReadTaskStatusDistribution(connection);
        var attemptStatusDistribution = ReadAttemptStatusDistribution(connection);

        var positiveSamples = ReadPositiveSamples(connection);
        var missingCompletedFacts = ReadMissingCompletedFacts(connection);
        var bindingNormalizations = ReadBindingNormalizations(connection);
        var attemptReleases = ReadAttemptReleases(connection);
        var status4 = ReadAdjudicationRows(connection, status: 4);
        var otherUnreleased = ReadOtherUnreleased(connection);
        var projectionCleanups = ReadProjectionCleanups(connection, nowUtc);

        var bindingsEmpty = bindingNormalizations.Where(item => IsEmpty(item.ExecutionIdFrom) || IsEmpty(item.SessionIdFrom)).ToList();
        var bindingSnapshot = new BindingSnapshot(
            Total: CountBindings(connection),
            AnyEmpty: bindingsEmpty.Count,
            ExecutionIdEmpty: bindingsEmpty.Count(item => IsEmpty(item.ExecutionIdFrom)),
            SessionIdEmpty: bindingsEmpty.Count(item => IsEmpty(item.SessionIdFrom)),
            BothEmpty: bindingsEmpty.Count(item => IsEmpty(item.ExecutionIdFrom) && IsEmpty(item.SessionIdFrom)),
            RepairableOnTerminalTask: bindingsEmpty.Count(item => item.WillRepair),
            PendingClaimOnLiveTask: bindingsEmpty.Count(item => !item.WillRepair));

        return new TaskRecallResult
        {
            RunAtUtc = nowUtc,
            DatabasePath = databasePath,
            Applied = false,
            TaskStatusDistribution = taskStatusDistribution,
            AttemptStatusDistribution = attemptStatusDistribution,
            Bindings = bindingSnapshot,
            Projections = new ProjectionSnapshot(
                Total: CountProjections(connection),
                Expired: projectionCleanups.Count,
                WouldDelete: projectionCleanups.Count(item => item.WillDelete),
                SkippedLiveTask: projectionCleanups.Count(item => !item.WillDelete)),
            PositiveSamples = positiveSamples,
            MissingCompletedFacts = missingCompletedFacts,
            BindingNormalizations = bindingsEmpty,
            AttemptReleases = attemptReleases,
            ProjectionCleanups = projectionCleanups,
            Status4Adjudication = status4,
            OtherUnreleasedObservations = otherUnreleased,
        };
    }

    /// <summary>
    /// --apply 写库：全部修复包在单个 Serializable 事务中，任一语句失败整体回滚。
    /// 连接必须以可写模式打开；调用方负责先完成 db 文件备份。
    /// </summary>
    public static async Task<ApplyOutcome> ApplyAsync(SqliteConnection connection, TaskRecallResult report, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(report);

        var nowText = nowUtc.ToString("O", CultureInfo.InvariantCulture);
        var eventsInserted = 0;
        var bindingsUpdated = 0;
        var attemptsReleased = 0;
        var projectionsDeleted = 0;

        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        try
        {
            foreach (var item in report.MissingCompletedFacts)
            {
                using var cmd = connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO task_events (event_id, task_id, workspace_id, sequence, event_type,
                        assignment_id, agent_id, delivery_id, execution_id, session_id, origin, priority,
                        decision_code, next_eligible_at_utc, trace_id, correlation_id, causation_id, created_at_utc)
                    VALUES (@eventId, @taskId, @workspaceId, @sequence, @eventType,
                        @assignmentId, @agentId, NULL, NULL, NULL, @origin, NULL,
                        @decisionCode, NULL, NULL, @correlationId, @causationId, @createdAt)
                    """;
                Add(cmd, "@eventId", item.EventId);
                Add(cmd, "@taskId", item.TaskId);
                Add(cmd, "@workspaceId", item.WorkspaceId);
                Add(cmd, "@sequence", item.Sequence);
                Add(cmd, "@eventType", TaskCompletedEvent);
                Add(cmd, "@assignmentId", item.AssignmentId);
                Add(cmd, "@agentId", item.AgentId);
                Add(cmd, "@origin", item.Origin.HasValue ? item.Origin.Value : null);
                Add(cmd, "@decisionCode", DecisionCode);
                Add(cmd, "@correlationId", item.TaskId);
                Add(cmd, "@causationId", string.IsNullOrEmpty(item.AssignmentId) ? CausationFallback : item.AssignmentId);
                Add(cmd, "@createdAt", item.CreatedAtUtc);
                eventsInserted += cmd.ExecuteNonQuery();
            }

            foreach (var item in report.BindingNormalizations.Where(item => item.WillRepair))
            {
                using var cmd = connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    UPDATE task_execution_bindings SET
                        execution_id = CASE WHEN execution_id IS NULL OR execution_id = '' THEN @placeholder ELSE execution_id END,
                        session_id   = CASE WHEN session_id   IS NULL OR session_id   = '' THEN @placeholder ELSE session_id END
                    WHERE id = @id
                    """;
                Add(cmd, "@placeholder", NoExecutionPlaceholder);
                Add(cmd, "@id", item.Id);
                bindingsUpdated += cmd.ExecuteNonQuery();
            }

            foreach (var item in report.AttemptReleases.Where(item => item.WillRepair))
            {
                using var cmd = connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    UPDATE task_assignment_attempts
                    SET status = @status, released_at_utc = @now, updated_at_utc = @now
                    WHERE attempt_id = @attemptId
                    """;
                Add(cmd, "@status", item.StatusTo);
                Add(cmd, "@now", nowText);
                Add(cmd, "@attemptId", item.AttemptId);
                attemptsReleased += cmd.ExecuteNonQuery();
            }

            foreach (var item in report.ProjectionCleanups.Where(item => item.WillDelete))
            {
                using var cmd = connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM agent_availability_projection WHERE id = @id";
                Add(cmd, "@id", item.Id);
                projectionsDeleted += cmd.ExecuteNonQuery();
            }

            await tx.CommitAsync();
            return new ApplyOutcome
            {
                Committed = true,
                BackupPath = null,
                EventsInserted = eventsInserted,
                BindingsUpdated = bindingsUpdated,
                AttemptsReleased = attemptsReleased,
                ProjectionsDeleted = projectionsDeleted,
            };
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            return new ApplyOutcome
            {
                Committed = false,
                BackupPath = null,
                EventsInserted = 0,
                BindingsUpdated = 0,
                AttemptsReleased = 0,
                ProjectionsDeleted = 0,
                Error = $"{ex.GetType().Name}: {ex.Message}",
            };
        }
    }

    // ─────────────────────── 侦查查询 ───────────────────────

    private static IReadOnlyList<TaskStatusCount> ReadTaskStatusDistribution(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT status, COUNT(*) FROM workspace_tasks GROUP BY status ORDER BY status";
        using var reader = cmd.ExecuteReader();
        var results = new List<TaskStatusCount>();
        while (reader.Read())
        {
            var status = reader.GetInt32(0);
            results.Add(new TaskStatusCount(status, TaskStatusName(status), reader.GetInt32(1)));
        }
        return results;
    }

    private static IReadOnlyList<AttemptStatusCount> ReadAttemptStatusDistribution(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT status, COUNT(*), SUM(CASE WHEN released_at_utc IS NULL THEN 1 ELSE 0 END)
            FROM task_assignment_attempts GROUP BY status ORDER BY status
            """;
        using var reader = cmd.ExecuteReader();
        var results = new List<AttemptStatusCount>();
        while (reader.Read())
        {
            var status = reader.GetInt32(0);
            results.Add(new AttemptStatusCount(status, AttemptStatusName(status), reader.GetInt32(1), (int)reader.GetInt64(2)));
        }
        return results;
    }

    private static IReadOnlyList<CompletedEventSample> ReadPositiveSamples(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT task_id, sequence, assignment_id, agent_id, execution_id, session_id, origin,
                   decision_code, correlation_id, causation_id, created_at_utc
            FROM task_events WHERE event_type = @eventType
            ORDER BY created_at_utc DESC LIMIT 3
            """;
        Add(cmd, "@eventType", TaskCompletedEvent);
        using var reader = cmd.ExecuteReader();
        var results = new List<CompletedEventSample>();
        while (reader.Read())
        {
            results.Add(new CompletedEventSample(
                reader.GetString(0),
                reader.GetInt64(1),
                ReadNullableString(reader, 2),
                ReadNullableString(reader, 3),
                ReadNullableString(reader, 4),
                ReadNullableString(reader, 5),
                ReadNullableLong(reader, 6),
                ReadNullableString(reader, 7),
                ReadNullableString(reader, 8),
                ReadNullableString(reader, 9),
                reader.GetString(10)));
        }
        return results;
    }

    private static IReadOnlyList<TaskCompletedBackfill> ReadMissingCompletedFacts(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT t.task_id, t.workspace_id, t.title, t.origin, t.version, t.completed_at_utc, t.updated_at_utc
            FROM workspace_tasks t
            WHERE t.status = @completed
              AND NOT EXISTS (SELECT 1 FROM task_events e WHERE e.task_id = t.task_id AND e.event_type = @completedEvent)
            ORDER BY t.created_at_utc
            """;
        Add(cmd, "@completed", TaskCompletedStatus);
        Add(cmd, "@completedEvent", TaskCompletedEvent);

        // 先读完全部行再逐任务补充 sequence/attempt（同一连接不支持并行 DataReader）。
        var rawRows = new List<(string TaskId, string WorkspaceId, string Title, long? Origin, long Version, string? CompletedAt, string? UpdatedAt)>();
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                rawRows.Add((
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    ReadNullableLong(reader, 3),
                    reader.GetInt64(4),
                    ReadNullableString(reader, 5),
                    ReadNullableString(reader, 6)));
            }
        }

        var results = new List<TaskCompletedBackfill>();
        foreach (var (taskId, workspaceId, title, origin, version, completedAt, updatedAt) in rawRows)
        {
            // created_at 取值依据：优先 completed_at_utc，缺省回落 updated_at_utc，再缺省用回填时间（报告逐行标注）。
            var (createdAt, source) = !IsEmpty(completedAt)
                ? (completedAt!, "completed_at_utc")
                : !IsEmpty(updatedAt)
                    ? (updatedAt!, "updated_at_utc")
                    : (DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture), "backfill_now");

            results.Add(new TaskCompletedBackfill
            {
                TaskId = taskId,
                WorkspaceId = workspaceId,
                Title = title,
                Origin = origin,
                Version = version,
                Sequence = MaxSequence(connection, taskId) + 1,
                EventId = $"recall-backfill-{taskId}-{version}",
                CreatedAtUtc = createdAt,
                CreatedAtSource = source,
                AssignmentId = FindActiveAttemptId(connection, taskId),
                AgentId = FindActiveAttemptAgentId(connection, taskId),
            });
        }
        return results;
    }

    private static IReadOnlyList<BindingNormalization> ReadBindingNormalizations(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT b.id, b.task_id, b.assignment_id, b.delivery_id, b.execution_id, b.session_id, t.status
            FROM task_execution_bindings b
            LEFT JOIN workspace_tasks t ON t.task_id = b.task_id
            WHERE b.execution_id IS NULL OR b.execution_id = '' OR b.session_id IS NULL OR b.session_id = ''
            ORDER BY b.id
            """;
        using var reader = cmd.ExecuteReader();
        var results = new List<BindingNormalization>();
        while (reader.Read())
        {
            var taskStatus = ReadNullableInt(reader, 6);
            var taskStatusName = taskStatus.HasValue ? TaskStatusName(taskStatus.Value) : "<task_missing>";
            var terminal = taskStatus.HasValue && IsTerminalTask(taskStatus.Value);
            results.Add(new BindingNormalization
            {
                Id = reader.GetInt64(0),
                TaskId = reader.GetString(1),
                AssignmentId = reader.GetString(2),
                DeliveryId = reader.GetString(3),
                ExecutionIdFrom = ReadNullableString(reader, 4),
                SessionIdFrom = ReadNullableString(reader, 5),
                TaskStatusName = taskStatusName,
                WillRepair = terminal,
                Reason = terminal
                    ? "task_terminal_backfill_no_execution"
                    : taskStatus.HasValue ? "task_live_pending_claim" : "task_missing_manual_review",
            });
        }
        return results;
    }

    private static IReadOnlyList<AttemptRelease> ReadAttemptReleases(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT a.attempt_id, a.task_id, a.agent_id, a.status, t.status
            FROM task_assignment_attempts a
            LEFT JOIN workspace_tasks t ON t.task_id = a.task_id
            WHERE a.status IN (@reserved, @assigned) AND a.released_at_utc IS NULL
            ORDER BY a.created_at_utc
            """;
        Add(cmd, "@reserved", (int)AssignmentAttemptStatus.Reserved);
        Add(cmd, "@assigned", (int)AssignmentAttemptStatus.Assigned);
        using var reader = cmd.ExecuteReader();
        var results = new List<AttemptRelease>();
        while (reader.Read())
        {
            var taskStatus = ReadNullableInt(reader, 4);
            var taskStatusName = taskStatus.HasValue ? TaskStatusName(taskStatus.Value) : "<task_missing>";
            var terminal = taskStatus.HasValue && IsTerminalTask(taskStatus.Value);
            var statusFrom = reader.GetInt32(3);
            // 终态释放语义与 TaskExecutionRepairCoordinator 一致：任务 Completed → Completed，其余终态 → Failed。
            var statusTo = taskStatus == TaskCompletedStatus
                ? (int)AssignmentAttemptStatus.Completed
                : (int)AssignmentAttemptStatus.Failed;
            results.Add(new AttemptRelease
            {
                AttemptId = reader.GetString(0),
                TaskId = reader.GetString(1),
                AgentId = reader.GetString(2),
                StatusFrom = statusFrom,
                StatusFromName = AttemptStatusName(statusFrom),
                StatusTo = statusTo,
                StatusToName = AttemptStatusName(statusTo),
                TaskStatusName = taskStatusName,
                WillRepair = terminal,
                Reason = terminal
                    ? "task_terminal_release"
                    : taskStatus.HasValue ? "task_live_skip" : "task_missing_manual_review",
            });
        }
        return results;
    }

    private static IReadOnlyList<AttemptRow> ReadAdjudicationRows(SqliteConnection connection, int status)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT a.attempt_id, a.task_id, a.agent_id, a.status, a.released_at_utc, t.status
            FROM task_assignment_attempts a
            LEFT JOIN workspace_tasks t ON t.task_id = a.task_id
            WHERE a.status = @status
            ORDER BY a.created_at_utc
            """;
        Add(cmd, "@status", status);
        using var reader = cmd.ExecuteReader();
        var results = new List<AttemptRow>();
        while (reader.Read())
        {
            var attemptStatus = reader.GetInt32(3);
            var taskStatus = ReadNullableInt(reader, 5);
            results.Add(new AttemptRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                attemptStatus,
                AttemptStatusName(attemptStatus),
                ReadNullableString(reader, 4),
                taskStatus.HasValue ? TaskStatusName(taskStatus.Value) : "<task_missing>"));
        }
        return results;
    }

    private static IReadOnlyList<AttemptRow> ReadOtherUnreleased(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT a.attempt_id, a.task_id, a.agent_id, a.status, a.released_at_utc, t.status
            FROM task_assignment_attempts a
            LEFT JOIN workspace_tasks t ON t.task_id = a.task_id
            WHERE a.released_at_utc IS NULL AND a.status NOT IN (@reserved, @assigned, @failed)
            ORDER BY a.created_at_utc
            """;
        Add(cmd, "@reserved", (int)AssignmentAttemptStatus.Reserved);
        Add(cmd, "@assigned", (int)AssignmentAttemptStatus.Assigned);
        Add(cmd, "@failed", 4);
        using var reader = cmd.ExecuteReader();
        var results = new List<AttemptRow>();
        while (reader.Read())
        {
            var attemptStatus = reader.GetInt32(3);
            var taskStatus = ReadNullableInt(reader, 5);
            results.Add(new AttemptRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                attemptStatus,
                AttemptStatusName(attemptStatus),
                ReadNullableString(reader, 4),
                taskStatus.HasValue ? TaskStatusName(taskStatus.Value) : "<task_missing>"));
        }
        return results;
    }

    private static IReadOnlyList<ProjectionCleanup> ReadProjectionCleanups(SqliteConnection connection, DateTimeOffset nowUtc)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, workspace_id, agent_id, state, activity_reason, reason_code, valid_until_utc, active_task_id
            FROM agent_availability_projection
            ORDER BY id
            """;
        // 先读完全部行再逐行分类（ClassifyProjection 需要另开命令，同一连接不支持并行 DataReader）。
        var rawRows = new List<(long Id, string WorkspaceId, string AgentId, int State, int ActivityReason, string ReasonCode, string ValidUntil, string? ActiveTaskId)>();
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                var validUntilText = reader.GetString(6);
                if (!(TryParseTimestamp(validUntilText, out var validUntil) && validUntil <= nowUtc))
                    continue;
                rawRows.Add((
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetInt32(3),
                    reader.GetInt32(4),
                    reader.GetString(5),
                    validUntilText,
                    ReadNullableString(reader, 7)));
            }
        }

        var results = new List<ProjectionCleanup>();
        foreach (var (id, workspaceId, agentId, state, activityReason, reasonCode, validUntil, activeTaskId) in rawRows)
        {
            var (willDelete, reason) = ClassifyProjection(connection, activeTaskId);
            results.Add(new ProjectionCleanup
            {
                Id = id,
                WorkspaceId = workspaceId,
                AgentId = agentId,
                State = state,
                StateName = AvailabilityStateName(state),
                ActivityReason = activityReason,
                ActivityReasonName = ActivityReasonName(activityReason),
                ReasonCode = reasonCode,
                ValidUntilUtc = validUntil,
                ActiveTaskId = activeTaskId,
                WillDelete = willDelete,
                Reason = reason,
            });
        }
        return results;
    }

    /// <summary>运行时动态校验挂靠任务实时状态：任务仍存活（非终态）→ 跳过；缺失/已终态 → 删除。</summary>
    private static (bool WillDelete, string Reason) ClassifyProjection(SqliteConnection connection, string? activeTaskId)
    {
        if (IsEmpty(activeTaskId))
            return (true, "expired_no_active_task");

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT status FROM workspace_tasks WHERE task_id = @taskId";
        Add(cmd, "@taskId", activeTaskId);
        var status = ReadNullableInt(cmd.ExecuteScalar());
        if (status is null)
            return (true, "expired_active_task_missing");
        return IsTerminalTask(status.Value)
            ? (true, "expired_active_task_terminal")
            : (false, $"active_task_live_{TaskStatusName(status.Value)}");
    }

    // ─────────────────────── 辅助 ───────────────────────

    private static long MaxSequence(SqliteConnection connection, string taskId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(sequence), 0) FROM task_events WHERE task_id = @taskId";
        Add(cmd, "@taskId", taskId);
        return (long)(cmd.ExecuteScalar() ?? 0L);
    }

    private static string? FindActiveAttemptId(SqliteConnection connection, string taskId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT attempt_id FROM task_assignment_attempts WHERE task_id = @taskId AND released_at_utc IS NULL ORDER BY rowid LIMIT 1";
        Add(cmd, "@taskId", taskId);
        return ReadNullableString(cmd.ExecuteScalar());
    }

    private static string? FindActiveAttemptAgentId(SqliteConnection connection, string taskId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT agent_id FROM task_assignment_attempts WHERE task_id = @taskId AND released_at_utc IS NULL ORDER BY rowid LIMIT 1";
        Add(cmd, "@taskId", taskId);
        return ReadNullableString(cmd.ExecuteScalar());
    }

    private static int CountBindings(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM task_execution_bindings";
        return (int)(long)(cmd.ExecuteScalar() ?? 0L);
    }

    private static int CountProjections(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM agent_availability_projection";
        return (int)(long)(cmd.ExecuteScalar() ?? 0L);
    }

    private static bool IsTerminalTask(int status)
        => TaskStateMachine.IsTerminal((WorkspaceTaskStatus)status);

    internal static string TaskStatusName(int status)
    {
        var value = (WorkspaceTaskStatus)status;
        return Enum.IsDefined(value) ? value.ToString() : $"Unknown({status})";
    }

    internal static string AttemptStatusName(int status)
    {
        var value = (AssignmentAttemptStatus)status;
        return Enum.IsDefined(value) ? value.ToString() : $"Unknown({status})";
    }

    private static string AvailabilityStateName(int state)
    {
        var value = (AgentAvailabilityState)state;
        return Enum.IsDefined(value) ? value.ToString() : $"Unknown({state})";
    }

    private static string ActivityReasonName(int reason)
    {
        var value = (AgentActivityReason)reason;
        return Enum.IsDefined(value) ? value.ToString() : $"Unknown({reason})";
    }

    private static bool IsEmpty(string? value) => value is null || value.Length == 0;

    private static bool TryParseTimestamp(string? text, out DateTimeOffset value)
        => DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out value);

    private static void Add(SqliteCommand command, string name, object? value)
        => command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static string? ReadNullableString(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static long? ReadNullableLong(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    private static int? ReadNullableInt(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

    private static int? ReadNullableInt(object? scalar)
        => scalar is null or DBNull ? null : Convert.ToInt32(scalar);

    private static string? ReadNullableString(object? scalar)
        => scalar is null or DBNull ? null : Convert.ToString(scalar);
}

// ═════════════════════════ 报告输出 ═════════════════════════

public static class TaskRecallReportWriter
{
    public static string BuildMarkdown(TaskRecallResult result)
    {
        var mode = result.Applied ? "APPLY（写库）" : "DRY-RUN（只读诊断，未写库）";
        var sb = new StringBuilder(8 * 1024);
        sb.AppendLine("# 历史脏数据一次性诊断/修复报告（看板卡 4ed930e7 原子任务③）");
        sb.AppendLine();
        sb.AppendLine($"- 运行时间（UTC）：{result.RunAtUtc:O}");
        sb.AppendLine($"- 目标库：`{result.DatabasePath}`");
        sb.AppendLine($"- 模式：**{mode}**");
        sb.AppendLine($"- 枚举依据：WorkspaceTaskStatus/TaskEventType/TaskStateMachine（PuddingCore.Tasks）、AssignmentAttemptStatus（PuddingPlatform.Data.Entities）、AgentAvailabilityState/AgentActivityReason（PuddingCode.Scheduling）");
        sb.AppendLine();

        sb.AppendLine("## 〇、运行时快照（当次重跑侦查查询，非静态数字）");
        sb.AppendLine();
        sb.AppendLine("### workspace_tasks 状态分布");
        sb.AppendLine();
        sb.AppendLine("| status | 名称 | 任务数 |");
        sb.AppendLine("|---|---|---|");
        foreach (var item in result.TaskStatusDistribution)
            sb.AppendLine($"| {item.Status} | {item.StatusName} | {item.Count} |");
        sb.AppendLine();

        sb.AppendLine("### task_assignment_attempts 状态分布");
        sb.AppendLine();
        sb.AppendLine("| status | 名称 | 行数 | 未释放(released_at IS NULL) |");
        sb.AppendLine("|---|---|---|---|");
        foreach (var item in result.AttemptStatusDistribution)
            sb.AppendLine($"| {item.Status} | {item.StatusName} | {item.Count} | {item.UnreleasedCount} |");
        sb.AppendLine();

        sb.AppendLine("### task_execution_bindings 空值快照");
        sb.AppendLine();
        sb.AppendLine($"- 总行数：{result.Bindings.Total}；任一为空：{result.Bindings.AnyEmpty}（execution_id 空 {result.Bindings.ExecutionIdEmpty} / session_id 空 {result.Bindings.SessionIdEmpty} / 两者皆空 {result.Bindings.BothEmpty}）");
        sb.AppendLine($"- 终态任务可修复：{result.Bindings.RepairableOnTerminalTask}；存活任务观察（不修）：{result.Bindings.PendingClaimOnLiveTask}");
        sb.AppendLine();

        sb.AppendLine("### agent_availability_projection 快照");
        sb.AppendLine();
        sb.AppendLine($"- 总行数：{result.Projections.Total}；已过期：{result.Projections.Expired}；将删除：{result.Projections.WouldDelete}；跳过（挂靠任务存活）：{result.Projections.SkippedLiveTask}");
        sb.AppendLine();

        sb.AppendLine("## 一、A 类：Completed(status=8) 缺 TaskCompleted(event_type=10) 事实");
        sb.AppendLine();
        if (result.PositiveSamples.Count > 0)
        {
            sb.AppendLine("### 正样本（库内真实完成事件，schema 参照）");
            sb.AppendLine();
            sb.AppendLine("| task_id | seq | assignment_id | agent_id | execution_id | session_id | decision_code | created_at |");
            sb.AppendLine("|---|---|---|---|---|---|---|---|");
            foreach (var sample in result.PositiveSamples)
                sb.AppendLine($"| {sample.TaskId} | {sample.Sequence} | {sample.AssignmentId ?? "−"} | {sample.AgentId ?? "−"} | {sample.ExecutionId ?? "−"} | {sample.SessionId ?? "−"} | {sample.DecisionCode ?? "−"} | {sample.CreatedAtUtc} |");
            sb.AppendLine();
        }

        sb.AppendLine($"将补写完成事件：**{result.MissingCompletedFacts.Count}** 条");
        sb.AppendLine();
        if (result.MissingCompletedFacts.Count > 0)
        {
            sb.AppendLine("| task_id | title | event_id | seq | assignment_id | agent_id | created_at（来源） |");
            sb.AppendLine("|---|---|---|---|---|---|---|");
            foreach (var item in result.MissingCompletedFacts)
                sb.AppendLine($"| {item.TaskId} | {Escape(item.Title)} | {item.EventId} | {item.Sequence} | {item.AssignmentId ?? "−"} | {item.AgentId ?? "−"} | {item.CreatedAtUtc}（{item.CreatedAtSource}） |");
            sb.AppendLine();
            sb.AppendLine("- 事件字段结构对齐 TaskCompletionSettlementService 的 `settlement_backfill_completed` 路径：event_type=10、sequence=MAX+1、decision_code=`recall_backfill_completed`、correlation_id=task_id、causation_id=assignment_id（缺省 `recall-4ed930e7`）；execution_id/session_id 不臆造，保持 NULL。");
            sb.AppendLine("- created_at 取值依据逐行标注：优先任务行 `completed_at_utc`，缺省回落 `updated_at_utc`，再缺省标注为回填时间 `backfill_now`。");
        }
        sb.AppendLine();

        sb.AppendLine("## 二、B 类：task_execution_bindings 空 execution_id/session_id");
        sb.AppendLine();
        if (result.BindingNormalizations.Count == 0)
        {
            sb.AppendLine("无空值行。");
        }
        else
        {
            sb.AppendLine("| id | task_id | assignment_id | execution_id | session_id | task_status | 处置 | 原因 |");
            sb.AppendLine("|---|---|---|---|---|---|---|---|");
            foreach (var item in result.BindingNormalizations)
                sb.AppendLine($"| {item.Id} | {item.TaskId} | {item.AssignmentId} | {Display(item.ExecutionIdFrom)} | {Display(item.SessionIdFrom)} | {item.TaskStatusName} | {(item.WillRepair ? $"回填 `{TaskRecallAuditEngine.NoExecutionPlaceholder}`" : "跳过")} | {item.Reason} |");
            sb.AppendLine();
            sb.AppendLine($"- 修复语义：仅当挂靠任务已终态（不再可能被 execution claim）时空值回填显式占位值 `{TaskRecallAuditEngine.NoExecutionPlaceholder}`（写入侧 `??=` 幂等回填惯例的显式化）；存活任务的空绑定是合法的 pending-claim 状态，不动。禁止臆造 UUID。");
        }
        sb.AppendLine();

        sb.AppendLine("## 三、C 类：task_assignment_attempts 未释放（status IN Reserved=0/Assigned=1）");
        sb.AppendLine();
        if (result.AttemptReleases.Count == 0)
        {
            sb.AppendLine("无未释放行。");
        }
        else
        {
            sb.AppendLine("| attempt_id | task_id | agent_id | status 从→到 | task_status | 处置 | 原因 |");
            sb.AppendLine("|---|---|---|---|---|---|---|");
            foreach (var item in result.AttemptReleases)
                sb.AppendLine($"| {item.AttemptId} | {item.TaskId} | {item.AgentId} | {item.StatusFromName}({item.StatusFrom}) → {item.StatusToName}({item.StatusTo}) | {item.TaskStatusName} | {(item.WillRepair ? "释放" : "跳过")} | {item.Reason} |");
            sb.AppendLine();
            sb.AppendLine("- 释放语义与 TaskExecutionRepairCoordinator 一致：任务 Completed → attempt Completed；其余终态 → Failed；写 released_at_utc + updated_at_utc。");
        }
        sb.AppendLine();

        sb.AppendLine("### status=4（Failed）明细 —— 仅列清单供人工裁决，本轮不自动修");
        sb.AppendLine();
        if (result.Status4Adjudication.Count == 0)
        {
            sb.AppendLine("无 status=4 行。");
        }
        else
        {
            sb.AppendLine($"共 {result.Status4Adjudication.Count} 行。注意：按当前源码 AssignmentAttemptStatus，4=Failed（终态）；下列行中 `released_at=−` 者为「终态但未释放」的阻塞型异常（会顶住 partial unique index），需人工裁决。");
            sb.AppendLine();
            sb.AppendLine("| attempt_id | task_id | agent_id | status | released_at | task_status |");
            sb.AppendLine("|---|---|---|---|---|---|");
            foreach (var item in result.Status4Adjudication)
                sb.AppendLine($"| {item.AttemptId} | {item.TaskId} | {item.AgentId} | {item.StatusName}({item.Status}) | {Display(item.ReleasedAtUtc)} | {item.TaskStatusName} |");
        }
        sb.AppendLine();

        if (result.OtherUnreleasedObservations.Count > 0)
        {
            sb.AppendLine("### 其他未释放观察（status ∉ {0,1,4}，不在本轮修复范围）");
            sb.AppendLine();
            sb.AppendLine("| attempt_id | task_id | agent_id | status | released_at | task_status |");
            sb.AppendLine("|---|---|---|---|---|---|");
            foreach (var item in result.OtherUnreleasedObservations)
                sb.AppendLine($"| {item.AttemptId} | {item.TaskId} | {item.AgentId} | {item.StatusName}({item.Status}) | {Display(item.ReleasedAtUtc)} | {item.TaskStatusName} |");
            sb.AppendLine();
        }

        sb.AppendLine("## 四、D 类：agent_availability_projection 过期陈旧投影");
        sb.AppendLine();
        if (result.ProjectionCleanups.Count == 0)
        {
            sb.AppendLine("无过期投影。");
        }
        else
        {
            sb.AppendLine("| id | agent_id | state | activity_reason | reason_code | valid_until(UTC) | active_task_id | 处置 | 原因 |");
            sb.AppendLine("|---|---|---|---|---|---|---|---|---|");
            foreach (var item in result.ProjectionCleanups)
                sb.AppendLine($"| {item.Id} | {item.AgentId} | {item.StateName}({item.State}) | {item.ActivityReasonName}({item.ActivityReason}) | {item.ReasonCode} | {item.ValidUntilUtc} | {Display(item.ActiveTaskId)} | {(item.WillDelete ? "删除" : "跳过")} | {item.Reason} |");
            sb.AppendLine();
            sb.AppendLine("- 删除条件：valid_until 已过期 且（无 active_task_id 或挂靠任务缺失/已终态）。挂靠任务仍存活（非终态，如 InProgress）的投影运行时动态校验后跳过，绝不硬编码跳过名单。");
        }
        sb.AppendLine();

        sb.AppendLine("## 五、修复总量与执行结果");
        sb.AppendLine();
        sb.AppendLine($"- A 补写完成事件：{result.MissingCompletedFacts.Count} 条");
        sb.AppendLine($"- B 回填 no-execution 占位：{result.RepairableBindings} / {result.BindingNormalizations.Count} 行");
        sb.AppendLine($"- C 释放未释放 attempt：{result.RepairableAttempts} / {result.AttemptReleases.Count} 行");
        sb.AppendLine($"- D 删除过期投影：{result.DeletableProjections} / {result.ProjectionCleanups.Count} 行");
        sb.AppendLine($"- status=4 人工裁决清单：{result.Status4Adjudication.Count} 行（不自动修）");
        if (result.ApplyResult is { } outcome)
        {
            sb.AppendLine();
            sb.AppendLine(outcome.Committed
                ? $"- ✅ APPLY 已提交：插入事件 {outcome.EventsInserted}、更新绑定 {outcome.BindingsUpdated}、释放 attempt {outcome.AttemptsReleased}、删除投影 {outcome.ProjectionsDeleted}；备份：{outcome.BackupPath ?? "−"}"
                : $"- ❌ APPLY 失败已整体回滚：{outcome.Error}");
        }
        return sb.ToString();
    }

    private static string Display(string? value) => string.IsNullOrEmpty(value) ? "−" : value;

    private static string Escape(string value) => value.Replace("|", "\\|").ReplaceLineEndings(" ");
}
