using System.Data;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PuddingCode.Tasks;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Services.Tasks;

/// <summary>
/// TB-02: SQLite 实现的 <see cref="ITaskStore"/>。落地 workspace_tasks + task_events 两张表，
/// 含 CAS 乐观并发、Task+Event 原子提交、归档字段持久化与硬删语义。
/// <para>
/// 全部走原始 SQL（参照 SqliteAgentOrchestrationStore / SqliteExecutionJournal 惯例），
/// 表结构由 EF Core 模型（WorkspaceTaskEntity / TaskEventEntity）+ EnsureCreated 建表。
/// 枚举写 int、时间写 DateTimeOffset（TEXT ISO8601 "O" 格式）、列名 snake_case。
/// </para>
/// </summary>
public sealed class SqliteWorkspaceTaskStore(
    IDbContextFactory<PlatformDbContext> dbFactory) : ITaskStore
{
    private const IsolationLevel TxLevel = IsolationLevel.Serializable;
    private const int TaskCreated = (int)TaskEventType.TaskCreated;

    private const string TaskColumns = """
        task_id, workspace_id, title, description, acceptance_criteria, status, priority,
        execution_window, preferred_agent_id, active_assignment_id, not_before_utc, due_at_utc,
        next_eligible_at_utc, sort_order, progress_percent, progress_summary, blocker_kind,
        blocker_reason, failure_code, failure_reason, version, created_by, updated_by,
        created_at_utc, updated_at_utc, completed_at_utc, failed_at_utc, archived_at_utc
        """;

    /// <inheritdoc />
    public async Task<WorkspaceTask> CreateTaskAsync(CreateTaskRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var now = DateTimeOffset.UtcNow;

        var task = new WorkspaceTask
        {
            TaskId = Guid.NewGuid().ToString("N"),
            WorkspaceId = request.WorkspaceId,
            Title = request.Title,
            Description = request.Description,
            AcceptanceCriteria = request.AcceptanceCriteria,
            Status = WorkspaceTaskStatus.Backlog,
            Priority = request.Priority,
            ExecutionWindow = request.ExecutionWindow,
            PreferredAgentId = request.PreferredAgentId,
            NotBeforeUtc = request.NotBeforeUtc,
            DueAtUtc = request.DueAtUtc,
            SortOrder = request.SortOrder,
            Version = 1,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };

        var createdEvent = new TaskEvent
        {
            EventId = Guid.NewGuid().ToString("N"),
            TaskId = task.TaskId,
            WorkspaceId = request.WorkspaceId,
            Sequence = 1,
            EventType = TaskEventType.TaskCreated,
            CreatedAtUtc = now,
        };

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        await conn.OpenAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(TxLevel, ct);
        try
        {
            await InsertTaskAsync(conn, tx, task, ct);
            await InsertEventAsync(conn, tx, createdEvent, ct);
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }

        return task;
    }

    /// <inheritdoc />
    public async Task<WorkspaceTask?> GetTaskAsync(string workspaceId, string taskId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        await conn.OpenAsync(ct);
        return await ReadTaskAsync(conn, null, workspaceId, taskId, ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<WorkspaceTask>> QueryTasksAsync(TaskQuery query, CancellationToken ct = default)
        => await QueryTasksCoreAsync(query, statuses: null, ct);

    /// <summary>
    /// B2 boardColumn 过滤重载：<paramref name="statuses"/> 非空时生成 status IN (...) 子句，
    /// 与 <see cref="TaskQuery.Status"/>（单值）取交集；keyset 游标语义不变。不改变 ITaskStore 既有方法签名。
    /// </summary>
    public async Task<IReadOnlyList<WorkspaceTask>> QueryTasksAsync(
        TaskQuery query,
        IReadOnlyList<WorkspaceTaskStatus>? statuses,
        CancellationToken ct = default)
        => await QueryTasksCoreAsync(query, statuses, ct);

    private async Task<IReadOnlyList<WorkspaceTask>> QueryTasksCoreAsync(
        TaskQuery query,
        IReadOnlyList<WorkspaceTaskStatus>? statuses,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var limit = query.Limit <= 0 ? 100 : query.Limit;
        var cursor = ParseCursor(query.Cursor);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        var statusInSql = BuildStatusInClause(statuses, cmd);

        cmd.CommandText = $"""
            SELECT {TaskColumns}
            FROM workspace_tasks
            WHERE workspace_id = @workspaceId
              AND (@status IS NULL OR status = @status)
              AND {StatusInOrTrue(statusInSql)}
              AND (@agentId IS NULL OR preferred_agent_id = @agentId)
              AND (@priority IS NULL OR priority = @priority)
              AND (@cursorSort IS NULL OR sort_order > @cursorSort
                   OR (sort_order = @cursorSort AND task_id > @cursorTask))
            ORDER BY sort_order ASC, task_id ASC
            LIMIT @limit
            """;
        AddParam(cmd, "@workspaceId", query.WorkspaceId);
        AddParam(cmd, "@status", query.Status.HasValue ? (int)query.Status.Value : null);
        AddParam(cmd, "@agentId", query.AgentId);
        AddParam(cmd, "@priority", query.Priority.HasValue ? (int)query.Priority.Value : null);
        AddParam(cmd, "@cursorSort", cursor?.SortOrder);
        AddParam(cmd, "@cursorTask", cursor?.TaskId);
        AddParam(cmd, "@limit", limit);

        var results = new List<WorkspaceTask>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(MapTask(reader));
        }

        return results.AsReadOnly();
    }

    /// <inheritdoc />
    public async Task<WorkspaceTask> UpdateTaskAsync(UpdateTaskRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var now = DateTimeOffset.UtcNow;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        await conn.OpenAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(TxLevel, ct);
        try
        {
            var (sql, parameters) = BuildUpdateSql(request);

            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            foreach (var p in parameters)
            {
                AddParam(cmd, p.Name, p.Value);
            }

            AddParam(cmd, "@now", now.ToString("O"));
            AddParam(cmd, "@taskId", request.TaskId);
            AddParam(cmd, "@expectedVersion", request.ExpectedVersion);

            var affected = await cmd.ExecuteNonQueryAsync(ct);
            if (affected == 0)
            {
                var currentVersion = await ReadVersionAsync(conn, tx, request.TaskId, ct);
                if (currentVersion is null)
                {
                    throw new TaskStoreException(
                        TaskErrorCode.TaskNotFound,
                        $"Task '{request.TaskId}' not found.",
                        request.TaskId,
                        request.ExpectedVersion,
                        null);
                }

                throw new TaskStoreException(
                    TaskErrorCode.TaskVersionConflict,
                    $"Task '{request.TaskId}' version conflict: expected {request.ExpectedVersion}, actual {currentVersion}.",
                    request.TaskId,
                    request.ExpectedVersion,
                    currentVersion);
            }

            var updated = await ReadTaskAsync(conn, tx, null, request.TaskId, ct);
            var sequence = await NextSequenceAsync(conn, tx, request.TaskId, ct);

            var evt = new TaskEvent
            {
                EventId = Guid.NewGuid().ToString("N"),
                TaskId = request.TaskId,
                WorkspaceId = updated!.WorkspaceId,
                Sequence = sequence,
                EventType = TaskEventType.TaskUpdated,
                CreatedAtUtc = now,
            };
            await InsertEventAsync(conn, tx, evt, ct);
            await tx.CommitAsync(ct);

            return updated;
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<bool> HardDeleteTaskAsync(string workspaceId, string taskId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        await conn.OpenAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(TxLevel, ct);
        try
        {
            var task = await ReadTaskAsync(conn, tx, workspaceId, taskId, ct);
            if (task is null || task.Status != WorkspaceTaskStatus.Backlog)
            {
                await tx.CommitAsync(ct);
                return false;
            }

            var hasOtherEvents = await ExistsNonCreatedEventAsync(conn, tx, taskId, ct);
            if (hasOtherEvents)
            {
                await tx.CommitAsync(ct);
                return false;
            }

            await ExecuteAsync(
                conn, tx,
                "DELETE FROM task_events WHERE task_id = @taskId",
                ct,
                ("@taskId", taskId));
            await ExecuteAsync(
                conn, tx,
                "DELETE FROM workspace_tasks WHERE workspace_id = @workspaceId AND task_id = @taskId",
                ct,
                ("@workspaceId", workspaceId),
                ("@taskId", taskId));

            await tx.CommitAsync(ct);
            return true;
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task AppendEventAsync(TaskEvent evt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(evt);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        await conn.OpenAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(TxLevel, ct);
        try
        {
            var version = await ReadVersionAsync(conn, tx, evt.TaskId, ct);
            if (version is null)
            {
                throw new TaskStoreException(
                    TaskErrorCode.TaskNotFound,
                    $"Task '{evt.TaskId}' not found.",
                    evt.TaskId);
            }

            var sequence = evt.Sequence > 0
                ? evt.Sequence
                : await NextSequenceAsync(conn, tx, evt.TaskId, ct);

            await InsertEventAsync(conn, tx, evt with { Sequence = sequence }, ct);
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    // ── TB-11 comments ────────────────────────────────────────────

    /// <summary>新增任务评论/备注（单条 INSERT，返回领域记录 TaskComment）。</summary>
    public async Task<TaskComment> AddCommentAsync(
        string workspaceId,
        string taskId,
        TaskCommentAuthorKind authorKind,
        string? authorId,
        string content,
        CancellationToken ct = default)
    {
        var comment = new TaskComment
        {
            CommentId = Guid.NewGuid().ToString("N"),
            TaskId = taskId,
            WorkspaceId = workspaceId,
            AuthorKind = authorKind,
            AuthorId = authorId,
            Content = content,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO task_comments
              (comment_id, task_id, workspace_id, author_kind, author_id, content, created_at_utc)
            VALUES
              (@commentId, @taskId, @workspaceId, @authorKind, @authorId, @content, @createdAtUtc)
            """;
        AddParam(cmd, "@commentId", comment.CommentId);
        AddParam(cmd, "@taskId", comment.TaskId);
        AddParam(cmd, "@workspaceId", comment.WorkspaceId);
        AddParam(cmd, "@authorKind", (int)comment.AuthorKind);
        AddParam(cmd, "@authorId", comment.AuthorId);
        AddParam(cmd, "@content", comment.Content);
        AddParam(cmd, "@createdAtUtc", comment.CreatedAtUtc.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);

        return comment;
    }

    /// <summary>按创建时间升序返回任务评论/备注。</summary>
    public async Task<IReadOnlyList<TaskComment>> ListCommentsAsync(
        string workspaceId,
        string taskId,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT comment_id, task_id, workspace_id, author_kind, author_id, content, created_at_utc
            FROM task_comments
            WHERE workspace_id = @workspaceId AND task_id = @taskId
            ORDER BY created_at_utc ASC, id ASC
            """;
        AddParam(cmd, "@workspaceId", workspaceId);
        AddParam(cmd, "@taskId", taskId);

        var results = new List<TaskComment>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new TaskComment
            {
                CommentId = reader.GetString(0),
                TaskId = reader.GetString(1),
                WorkspaceId = reader.GetString(2),
                AuthorKind = (TaskCommentAuthorKind)reader.GetInt32(3),
                AuthorId = ReadStringNullable(reader, 4),
                Content = reader.GetString(5),
                CreatedAtUtc = ReadUtc(reader, 6),
            });
        }

        return results.AsReadOnly();
    }

    // ── SQL helpers ──────────────────────────────────────────────

    private static async Task InsertTaskAsync(
        SqliteConnection conn, SqliteTransaction tx, WorkspaceTask t, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO workspace_tasks
              (task_id, workspace_id, title, description, acceptance_criteria, status, priority,
               execution_window, preferred_agent_id, active_assignment_id, not_before_utc, due_at_utc,
               next_eligible_at_utc, sort_order, progress_percent, progress_summary, blocker_kind,
               blocker_reason, failure_code, failure_reason, version, created_by, updated_by,
               created_at_utc, updated_at_utc, completed_at_utc, failed_at_utc, archived_at_utc)
            VALUES
              (@taskId, @workspaceId, @title, @description, @acceptanceCriteria, @status, @priority,
               @executionWindow, @preferredAgentId, @activeAssignmentId, @notBeforeUtc, @dueAtUtc,
               @nextEligibleAtUtc, @sortOrder, @progressPercent, @progressSummary, @blockerKind,
               @blockerReason, @failureCode, @failureReason, @version, @createdBy, @updatedBy,
               @createdAtUtc, @updatedAtUtc, @completedAtUtc, @failedAtUtc, @archivedAtUtc)
            """;
        AddParam(cmd, "@taskId", t.TaskId);
        AddParam(cmd, "@workspaceId", t.WorkspaceId);
        AddParam(cmd, "@title", t.Title);
        AddParam(cmd, "@description", t.Description);
        AddParam(cmd, "@acceptanceCriteria", t.AcceptanceCriteria);
        AddParam(cmd, "@status", (int)t.Status);
        AddParam(cmd, "@priority", (int)t.Priority);
        AddParam(cmd, "@executionWindow", (int)t.ExecutionWindow);
        AddParam(cmd, "@preferredAgentId", t.PreferredAgentId);
        AddParam(cmd, "@activeAssignmentId", t.ActiveAssignmentId);
        AddParam(cmd, "@notBeforeUtc", t.NotBeforeUtc?.ToString("O"));
        AddParam(cmd, "@dueAtUtc", t.DueAtUtc?.ToString("O"));
        AddParam(cmd, "@nextEligibleAtUtc", t.NextEligibleAtUtc?.ToString("O"));
        AddParam(cmd, "@sortOrder", t.SortOrder);
        AddParam(cmd, "@progressPercent", t.ProgressPercent);
        AddParam(cmd, "@progressSummary", t.ProgressSummary);
        AddParam(cmd, "@blockerKind", t.BlockerKind);
        AddParam(cmd, "@blockerReason", t.BlockerReason);
        AddParam(cmd, "@failureCode", t.FailureCode);
        AddParam(cmd, "@failureReason", t.FailureReason);
        AddParam(cmd, "@version", t.Version);
        AddParam(cmd, "@createdBy", t.CreatedBy);
        AddParam(cmd, "@updatedBy", t.UpdatedBy);
        AddParam(cmd, "@createdAtUtc", t.CreatedAtUtc.ToString("O"));
        AddParam(cmd, "@updatedAtUtc", t.UpdatedAtUtc.ToString("O"));
        AddParam(cmd, "@completedAtUtc", t.CompletedAtUtc?.ToString("O"));
        AddParam(cmd, "@failedAtUtc", t.FailedAtUtc?.ToString("O"));
        AddParam(cmd, "@archivedAtUtc", t.ArchivedAtUtc?.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task InsertEventAsync(
        SqliteConnection conn, SqliteTransaction tx, TaskEvent e, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO task_events
              (event_id, task_id, workspace_id, sequence, event_type, assignment_id, agent_id,
               delivery_id, execution_id, session_id, origin, priority, decision_code,
               next_eligible_at_utc, trace_id, correlation_id, causation_id, created_at_utc)
            VALUES
              (@eventId, @taskId, @workspaceId, @sequence, @eventType, @assignmentId, @agentId,
               @deliveryId, @executionId, @sessionId, @origin, @priority, @decisionCode,
               @nextEligibleAtUtc, @traceId, @correlationId, @causationId, @createdAtUtc)
            """;
        AddParam(cmd, "@eventId", e.EventId);
        AddParam(cmd, "@taskId", e.TaskId);
        AddParam(cmd, "@workspaceId", e.WorkspaceId);
        AddParam(cmd, "@sequence", e.Sequence);
        AddParam(cmd, "@eventType", (int)e.EventType);
        AddParam(cmd, "@assignmentId", e.AssignmentId);
        AddParam(cmd, "@agentId", e.AgentId);
        AddParam(cmd, "@deliveryId", e.DeliveryId);
        AddParam(cmd, "@executionId", e.ExecutionId);
        AddParam(cmd, "@sessionId", e.SessionId);
        AddParam(cmd, "@origin", e.Origin.HasValue ? (int)e.Origin.Value : null);
        AddParam(cmd, "@priority", e.Priority.HasValue ? (int)e.Priority.Value : null);
        AddParam(cmd, "@decisionCode", e.DecisionCode);
        AddParam(cmd, "@nextEligibleAtUtc", e.NextEligibleAtUtc?.ToString("O"));
        AddParam(cmd, "@traceId", e.TraceId);
        AddParam(cmd, "@correlationId", e.CorrelationId);
        AddParam(cmd, "@causationId", e.CausationId);
        AddParam(cmd, "@createdAtUtc", e.CreatedAtUtc.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<int?> ReadVersionAsync(
        SqliteConnection conn, SqliteTransaction? tx, string taskId, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT version FROM workspace_tasks WHERE task_id = @taskId";
        AddParam(cmd, "@taskId", taskId);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is null or DBNull ? null : Convert.ToInt32(result);
    }

    private static async Task<long> NextSequenceAsync(
        SqliteConnection conn, SqliteTransaction tx, string taskId, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT COALESCE(MAX(sequence), 0) + 1 FROM task_events WHERE task_id = @taskId";
        AddParam(cmd, "@taskId", taskId);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
    }

    private static async Task<bool> ExistsNonCreatedEventAsync(
        SqliteConnection conn, SqliteTransaction tx, string taskId, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT 1 FROM task_events WHERE task_id = @taskId AND event_type != @taskCreated LIMIT 1";
        AddParam(cmd, "@taskId", taskId);
        AddParam(cmd, "@taskCreated", TaskCreated);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is not null and not DBNull;
    }

    private static async Task<WorkspaceTask?> ReadTaskAsync(
        SqliteConnection conn,
        SqliteTransaction? tx,
        string? workspaceId,
        string taskId,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"SELECT {TaskColumns} FROM workspace_tasks WHERE task_id = @taskId";
        if (workspaceId is not null)
        {
            cmd.CommandText += " AND workspace_id = @workspaceId";
        }

        AddParam(cmd, "@taskId", taskId);
        if (workspaceId is not null)
        {
            AddParam(cmd, "@workspaceId", workspaceId);
        }

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? MapTask(reader) : null;
    }

    private static async Task<int> ExecuteAsync(
        SqliteConnection conn,
        SqliteTransaction tx,
        string sql,
        CancellationToken ct,
        params (string Name, object? Value)[] parameters)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var p in parameters)
        {
            AddParam(cmd, p.Name, p.Value);
        }

        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private static (string Sql, List<(string Name, object? Value)> Parameters) BuildUpdateSql(
        UpdateTaskRequest request)
    {
        var sets = new List<string>();
        var parameters = new List<(string Name, object? Value)>();

        if (request.Title is not null)
        {
            sets.Add("title = @title");
            parameters.Add(("@title", request.Title));
        }

        if (request.Description is not null)
        {
            sets.Add("description = @description");
            parameters.Add(("@description", request.Description));
        }

        if (request.AcceptanceCriteria is not null)
        {
            sets.Add("acceptance_criteria = @acceptanceCriteria");
            parameters.Add(("@acceptanceCriteria", request.AcceptanceCriteria));
        }

        if (request.Priority.HasValue)
        {
            sets.Add("priority = @priority");
            parameters.Add(("@priority", (int)request.Priority.Value));
        }

        if (request.ExecutionWindow.HasValue)
        {
            sets.Add("execution_window = @executionWindow");
            parameters.Add(("@executionWindow", (int)request.ExecutionWindow.Value));
        }

        if (request.PreferredAgentId is not null)
        {
            sets.Add("preferred_agent_id = @preferredAgentId");
            parameters.Add(("@preferredAgentId", request.PreferredAgentId));
        }

        if (request.NotBeforeUtc.HasValue)
        {
            sets.Add("not_before_utc = @notBeforeUtc");
            parameters.Add(("@notBeforeUtc", request.NotBeforeUtc.Value.ToString("O")));
        }

        if (request.DueAtUtc.HasValue)
        {
            sets.Add("due_at_utc = @dueAtUtc");
            parameters.Add(("@dueAtUtc", request.DueAtUtc.Value.ToString("O")));
        }

        if (request.SortOrder.HasValue)
        {
            sets.Add("sort_order = @sortOrder");
            parameters.Add(("@sortOrder", request.SortOrder.Value));
        }

        // 恒定更新：版本 +1 与 updated_at_utc（CAS 语义）。
        sets.Add("version = version + 1");
        sets.Add("updated_at_utc = @now");

        var sql = $"""
            UPDATE workspace_tasks
            SET {string.Join(", ", sets)}
            WHERE task_id = @taskId AND version = @expectedVersion
            """;
        return (sql, parameters);
    }

    private static string? BuildStatusInClause(IReadOnlyList<WorkspaceTaskStatus>? statuses, SqliteCommand cmd)
    {
        if (statuses is null || statuses.Count == 0)
        {
            return null;
        }

        var names = new string[statuses.Count];
        for (var i = 0; i < statuses.Count; i++)
        {
            var name = $"@statusIn{i}";
            names[i] = name;
            AddParam(cmd, name, (int)statuses[i]);
        }

        return string.Join(", ", names);
    }

    private static string StatusInOrTrue(string? statusInSql)
        => statusInSql is null ? "1 = 1" : $"status IN ({statusInSql})";

    private static (long SortOrder, string TaskId)? ParseCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return null;
        }

        var parts = cursor.Split('|', 2);
        if (parts.Length != 2
            || !long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var sortOrder))
        {
            return null;
        }

        return (sortOrder, parts[1]);
    }

    private static WorkspaceTask MapTask(SqliteDataReader reader)
    {
        return new WorkspaceTask
        {
            TaskId = reader.GetString(0),
            WorkspaceId = reader.GetString(1),
            Title = reader.GetString(2),
            Description = ReadStringNullable(reader, 3),
            AcceptanceCriteria = ReadStringNullable(reader, 4),
            Status = (WorkspaceTaskStatus)reader.GetInt32(5),
            Priority = (TaskPriority)reader.GetInt32(6),
            ExecutionWindow = (TaskExecutionWindow)reader.GetInt32(7),
            PreferredAgentId = ReadStringNullable(reader, 8),
            ActiveAssignmentId = ReadStringNullable(reader, 9),
            NotBeforeUtc = ReadUtcNullable(reader, 10),
            DueAtUtc = ReadUtcNullable(reader, 11),
            NextEligibleAtUtc = ReadUtcNullable(reader, 12),
            SortOrder = reader.GetInt64(13),
            ProgressPercent = ReadIntNullable(reader, 14),
            ProgressSummary = ReadStringNullable(reader, 15),
            BlockerKind = ReadStringNullable(reader, 16),
            BlockerReason = ReadStringNullable(reader, 17),
            FailureCode = ReadStringNullable(reader, 18),
            FailureReason = ReadStringNullable(reader, 19),
            Version = reader.GetInt32(20),
            CreatedBy = ReadStringNullable(reader, 21),
            UpdatedBy = ReadStringNullable(reader, 22),
            CreatedAtUtc = ReadUtc(reader, 23),
            UpdatedAtUtc = ReadUtc(reader, 24),
            CompletedAtUtc = ReadUtcNullable(reader, 25),
            FailedAtUtc = ReadUtcNullable(reader, 26),
            ArchivedAtUtc = ReadUtcNullable(reader, 27),
        };
    }

    private static string? ReadStringNullable(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static int? ReadIntNullable(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

    private static DateTimeOffset ReadUtc(SqliteDataReader reader, int ordinal)
        => DateTimeOffset.Parse(reader.GetString(ordinal), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static DateTimeOffset? ReadUtcNullable(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal)
            ? null
            : DateTimeOffset.Parse(reader.GetString(ordinal), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static void AddParam(SqliteCommand cmd, string name, object? value)
        => cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
}
