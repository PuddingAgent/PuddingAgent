using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingCode.Tasks;
using PuddingPlatform.Data.Entities;
using PuddingTaskRecall.Cli;

namespace PuddingPlatformTests.Services.Tasks;

/// <summary>
/// 看板卡 4ed930e7 原子任务③：历史脏数据一次性诊断/修复引擎的单元测试。
/// fixture 为内存 SQLite 库（DDL 与 WorkspaceTaskSchemaBootstrapper / TaskDispatchSchemaBootstrapper /
/// TaskSchedulingSchemaBootstrapper 逐列一致），构造 A/B/C/D 四类脏数据，
/// 验证 dry-run 只读不落库、--apply 修复效果与单事务失败整体回滚。
/// </summary>
[TestClass]
public class TaskRecallAuditEngineTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);

    private const string T1 = "task-dirty1";       // Completed(8) 缺事实，completed_at 有值，有未释放 Assigned attempt
    private const string T2 = "task-dirty2";       // Completed(8) 缺事实，completed_at 缺省（回落 updated_at），无 attempt
    private const string T3 = "task-ok";           // Completed(8) 已有完成事实（正样本）
    private const string T4 = "task-live";         // InProgress(6) 存活：attempt/绑定均不得动
    private const string T5 = "task-cancelled";    // Cancelled(10) 终态：Reserved attempt 释放为 Failed
    private const string T6 = "task-binddone";     // Completed(8) 终态：空绑定回填 no-execution
    private const string T7 = "task-status4";      // Completed(8)：status=4 attempt 仅列人工裁决清单

    private const string A1 = "attempt-a1";        // T1: Assigned(1) 未释放 → 释放为 Completed(3)
    private const string A3 = "attempt-a3";        // T4: Assigned(1) 未释放 → 跳过（任务存活）
    private const string A4 = "attempt-a4";        // T5: Reserved(0) 未释放 → 释放为 Failed(4)
    private const string A5 = "attempt-a5";        // T7: status=4 未释放 → 仅裁决清单

    private const string CompletedAtT1 = "2026-08-20T10:00:00.0000000+00:00";
    private const string UpdatedAtT2 = "2026-08-22T08:30:00.0000000+00:00";

    [TestMethod]
    public void DryRun_Reports_All_Four_Categories_And_Never_Writes()
    {
        using var connection = CreateSeededFixture();
        var eventsBefore = ScalarLong(connection, "SELECT COUNT(*) FROM task_events");

        var result = TaskRecallAuditEngine.Analyze(connection, ":memory:", Now);

        Assert.IsFalse(result.Applied);
        Assert.IsNull(result.ApplyResult);

        // 快照（运行时重跑，非静态数字）
        Assert.IsTrue(result.TaskStatusDistribution.Any(item => item.Status == (int)WorkspaceTaskStatus.Completed && item.Count == 5));
        Assert.IsTrue(result.TaskStatusDistribution.Any(item => item.Status == (int)WorkspaceTaskStatus.InProgress && item.Count == 1));
        Assert.AreEqual(2, result.AttemptStatusDistribution.Single(item => item.Status == 1).Count);
        Assert.AreEqual(2, result.AttemptStatusDistribution.Single(item => item.Status == 1).UnreleasedCount);
        Assert.AreEqual(1, result.AttemptStatusDistribution.Single(item => item.Status == 0).UnreleasedCount);
        Assert.AreEqual(1, result.AttemptStatusDistribution.Single(item => item.Status == 4).UnreleasedCount);

        // A：缺完成事实 = T1、T2；正样本 = T3 的完成事件
        Assert.AreEqual(2, result.MissingCompletedFacts.Count);
        var t1 = result.MissingCompletedFacts.Single(item => item.TaskId == T1);
        Assert.AreEqual(2, t1.Sequence);                       // TaskCreated(seq=1) 之后
        Assert.AreEqual($"recall-backfill-{T1}-3", t1.EventId);
        Assert.AreEqual(CompletedAtT1, t1.CreatedAtUtc);
        Assert.AreEqual("completed_at_utc", t1.CreatedAtSource);
        Assert.AreEqual(A1, t1.AssignmentId);
        Assert.AreEqual("agent-x", t1.AgentId);
        var t2 = result.MissingCompletedFacts.Single(item => item.TaskId == T2);
        Assert.AreEqual(1, t2.Sequence);
        Assert.AreEqual(UpdatedAtT2, t2.CreatedAtUtc);
        Assert.AreEqual("updated_at_utc", t2.CreatedAtSource);
        Assert.IsNull(t2.AssignmentId);
        Assert.AreEqual(1, result.PositiveSamples.Count);
        Assert.AreEqual(T3, result.PositiveSamples[0].TaskId);

        // B：空绑定 3 行（B1/B2 终态修复，B3 存活跳过）
        Assert.AreEqual(3, result.BindingNormalizations.Count);
        Assert.AreEqual(2, result.RepairableBindings);
        var b3 = result.BindingNormalizations.Single(item => item.TaskId == T4);
        Assert.IsFalse(b3.WillRepair);
        Assert.AreEqual("task_live_pending_claim", b3.Reason);
        Assert.AreEqual(3, result.Bindings.AnyEmpty);
        Assert.AreEqual(2, result.Bindings.BothEmpty);
        Assert.AreEqual(2, result.Bindings.RepairableOnTerminalTask);

        // C：未释放 Reserved/Assigned = A1（修复→Completed）、A3（跳过）、A4（修复→Failed）
        Assert.AreEqual(3, result.AttemptReleases.Count);
        Assert.AreEqual(2, result.RepairableAttempts);
        var a1 = result.AttemptReleases.Single(item => item.AttemptId == A1);
        Assert.AreEqual((int)AssignmentAttemptStatus.Completed, a1.StatusTo);
        Assert.AreEqual((int)AssignmentAttemptStatus.Assigned, a1.StatusFrom);
        var a4 = result.AttemptReleases.Single(item => item.AttemptId == A4);
        Assert.AreEqual((int)AssignmentAttemptStatus.Failed, a4.StatusTo);   // 任务 Cancelled → Failed
        var a3 = result.AttemptReleases.Single(item => item.AttemptId == A3);
        Assert.IsFalse(a3.WillRepair);
        Assert.AreEqual("task_live_skip", a3.Reason);

        // status=4 仅列裁决清单
        Assert.AreEqual(1, result.Status4Adjudication.Count);
        Assert.AreEqual(A5, result.Status4Adjudication[0].AttemptId);
        Assert.AreEqual("Failed", result.Status4Adjudication[0].StatusName);

        // D：过期投影 3 行 —— P1 删除、P2 跳过（挂靠任务 InProgress）、P4 删除；P3 未过期不列
        Assert.AreEqual(3, result.ProjectionCleanups.Count);
        Assert.AreEqual(2, result.DeletableProjections);
        var p2 = result.ProjectionCleanups.Single(item => item.AgentId.Contains("6a8"));
        Assert.IsFalse(p2.WillDelete);
        Assert.AreEqual("active_task_live_InProgress", p2.Reason);
        Assert.AreEqual(1, result.Projections.SkippedLiveTask);

        // dry-run 零写入
        Assert.AreEqual(eventsBefore, ScalarLong(connection, "SELECT COUNT(*) FROM task_events"));
        Assert.AreEqual(4, ScalarLong(connection, "SELECT COUNT(*) FROM agent_availability_projection"));
    }

    [TestMethod]
    public async Task Apply_Repairs_All_Categories_Then_Reanalyze_Is_Clean()
    {
        using var connection = CreateSeededFixture();
        var result = TaskRecallAuditEngine.Analyze(connection, ":memory:", Now);
        var outcome = await TaskRecallAuditEngine.ApplyAsync(connection, result, Now);

        Assert.IsTrue(outcome.Committed, outcome.Error);
        Assert.AreEqual(2, outcome.EventsInserted);
        Assert.AreEqual(2, outcome.BindingsUpdated);
        Assert.AreEqual(2, outcome.AttemptsReleased);
        Assert.AreEqual(2, outcome.ProjectionsDeleted);

        // A：T1 补写的完成事件字段结构
        var row = QueryRow(connection, $"""
            SELECT sequence, event_type, assignment_id, agent_id, execution_id, session_id, decision_code,
                   correlation_id, causation_id, created_at_utc
            FROM task_events WHERE task_id = '{T1}' AND event_type = {(int)TaskEventType.TaskCompleted}
            """);
        Assert.AreEqual(2L, row[0]);
        Assert.AreEqual((long)TaskEventType.TaskCompleted, row[1]);
        Assert.AreEqual(A1, row[2]);
        Assert.AreEqual("agent-x", row[3]);
        Assert.AreEqual(DBNull.Value, row[4]);              // 不臆造 execution_id
        Assert.AreEqual(DBNull.Value, row[5]);              // 不臆造 session_id
        Assert.AreEqual(TaskRecallAuditEngine.DecisionCode, row[6]);
        Assert.AreEqual(T1, row[7]);
        Assert.AreEqual(A1, row[8]);
        Assert.AreEqual(CompletedAtT1, row[9]);             // created_at 取任务行 completed_at_utc

        // B：终态任务空绑定回填 no-execution；非空字段保留
        var b1 = QueryRow(connection, $"SELECT execution_id, session_id FROM task_execution_bindings WHERE task_id = '{T6}' AND delivery_id = 'd1'");
        Assert.AreEqual(TaskRecallAuditEngine.NoExecutionPlaceholder, b1[0]);
        Assert.AreEqual(TaskRecallAuditEngine.NoExecutionPlaceholder, b1[1]);
        var b2 = QueryRow(connection, $"SELECT execution_id, session_id FROM task_execution_bindings WHERE task_id = '{T6}' AND delivery_id = 'd2'");
        Assert.AreEqual("run-123", b2[0]);
        Assert.AreEqual(TaskRecallAuditEngine.NoExecutionPlaceholder, b2[1]);
        var b3 = QueryRow(connection, $"SELECT execution_id, session_id FROM task_execution_bindings WHERE task_id = '{T4}'");
        Assert.AreEqual(DBNull.Value, b3[0]);               // 存活任务不动
        Assert.AreEqual(DBNull.Value, b3[1]);

        // C：A1→Completed 已释放；A4→Failed 已释放；A3 存活不动；A5(status=4) 不自动修
        var a1 = QueryRow(connection, $"SELECT status, released_at_utc FROM task_assignment_attempts WHERE attempt_id = '{A1}'");
        Assert.AreEqual((long)AssignmentAttemptStatus.Completed, a1[0]);
        Assert.AreNotEqual(DBNull.Value, a1[1]);
        var a4 = QueryRow(connection, $"SELECT status, released_at_utc FROM task_assignment_attempts WHERE attempt_id = '{A4}'");
        Assert.AreEqual((long)AssignmentAttemptStatus.Failed, a4[0]);
        Assert.AreNotEqual(DBNull.Value, a4[1]);
        var a3 = QueryRow(connection, $"SELECT status, released_at_utc FROM task_assignment_attempts WHERE attempt_id = '{A3}'");
        Assert.AreEqual((long)AssignmentAttemptStatus.Assigned, a3[0]);
        Assert.AreEqual(DBNull.Value, a3[1]);
        var a5 = QueryRow(connection, $"SELECT status, released_at_utc FROM task_assignment_attempts WHERE attempt_id = '{A5}'");
        Assert.AreEqual(4L, a5[0]);
        Assert.AreEqual(DBNull.Value, a5[1]);

        // D：P1/P4 删除，P2（挂靠存活）与 P3（未过期）保留
        Assert.AreEqual(2, ScalarLong(connection, "SELECT COUNT(*) FROM agent_availability_projection"));
        Assert.AreEqual(1, ScalarLong(connection, $"SELECT COUNT(*) FROM agent_availability_projection WHERE agent_id LIKE '%6a8%'"));

        // 复跑：终态脏数据清零，仅剩存活观察项
        var second = TaskRecallAuditEngine.Analyze(connection, ":memory:", Now);
        Assert.AreEqual(0, second.MissingCompletedFacts.Count);
        Assert.AreEqual(1, second.BindingNormalizations.Count);
        Assert.AreEqual(0, second.RepairableBindings);
        Assert.AreEqual(1, second.AttemptReleases.Count);
        Assert.AreEqual(0, second.RepairableAttempts);
        Assert.AreEqual(1, second.ProjectionCleanups.Count);
        Assert.AreEqual(0, second.DeletableProjections);
        Assert.AreEqual(1, second.Status4Adjudication.Count);
    }

    [TestMethod]
    public async Task Apply_Failure_Rolls_Back_The_Whole_Transaction()
    {
        using var connection = CreateSeededFixture();

        // 注入与引擎将生成的 event_id 冲突的既有行（event_id 唯一索引）→ 插入中途失败
        Exec(connection, $"""
            INSERT INTO task_events (event_id, task_id, workspace_id, sequence, event_type, created_at_utc)
            VALUES ('recall-backfill-{T1}-3', '{T3}', 'ws1', 99, {(int)TaskEventType.TaskUpdated}, '{Now:O}')
            """);

        var result = TaskRecallAuditEngine.Analyze(connection, ":memory:", Now);
        var outcome = await TaskRecallAuditEngine.ApplyAsync(connection, result, Now);

        Assert.IsFalse(outcome.Committed);
        Assert.IsNotNull(outcome.Error);
        StringAssert.Contains(outcome.Error, "UNIQUE");

        // 整体回滚：四类修复一条都没落库
        Assert.AreEqual((long)AssignmentAttemptStatus.Assigned,
            QueryRow(connection, $"SELECT status FROM task_assignment_attempts WHERE attempt_id = '{A1}'")[0]);
        Assert.AreEqual(DBNull.Value,
            QueryRow(connection, $"SELECT released_at_utc FROM task_assignment_attempts WHERE attempt_id = '{A1}'")[0]);
        Assert.AreEqual(DBNull.Value,
            QueryRow(connection, $"SELECT execution_id FROM task_execution_bindings WHERE task_id = '{T6}' AND delivery_id = 'd1'")[0]);
        Assert.AreEqual(4, ScalarLong(connection, "SELECT COUNT(*) FROM agent_availability_projection"));
        Assert.AreEqual(4, ScalarLong(connection, "SELECT COUNT(*) FROM task_events"));  // 仅含种子 + 注入行
    }

    // ─────────────────────── fixture ───────────────────────

    private static SqliteConnection CreateSeededFixture()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        foreach (var ddl in Schema)
        {
            Exec(connection, ddl);
        }

        InsertTask(connection, T1, (int)WorkspaceTaskStatus.Completed, version: 3, completedAt: CompletedAtT1, updatedAt: CompletedAtT1, origin: 1);
        InsertEvent(connection, "seed-created-1", T1, sequence: 1, TaskEventType.TaskCreated);
        InsertAttempt(connection, A1, T1, (int)AssignmentAttemptStatus.Assigned, "agent-x");

        InsertTask(connection, T2, (int)WorkspaceTaskStatus.Completed, version: 2, completedAt: null, updatedAt: UpdatedAtT2);
        // T2 无事件、无 attempt

        InsertTask(connection, T3, (int)WorkspaceTaskStatus.Completed, version: 4, completedAt: "2026-08-25T09:00:00.0000000+00:00", updatedAt: "2026-08-25T09:05:00.0000000+00:00");
        InsertEvent(connection, "seed-created-3", T3, sequence: 1, TaskEventType.TaskCreated);
        InsertEvent(connection, "seed-completed-3", T3, sequence: 2, TaskEventType.TaskCompleted,
            assignmentId: "attempt-ok", agentId: "agent-ok", executionId: "run-ok", sessionId: "sess-ok",
            decisionCode: "settlement_backfill_completed");

        InsertTask(connection, T4, (int)WorkspaceTaskStatus.InProgress, version: 2, completedAt: null, updatedAt: "2026-08-28T08:00:00.0000000+00:00");
        InsertAttempt(connection, A3, T4, (int)AssignmentAttemptStatus.Assigned, "agent-live");
        Exec(connection, $"INSERT INTO task_execution_bindings (task_id, assignment_id, delivery_id, execution_id, session_id, bound_at_utc) VALUES ('{T4}', 'at4', 'd-live', NULL, NULL, '{Now:O}')");

        InsertTask(connection, T5, (int)WorkspaceTaskStatus.Cancelled, version: 2, completedAt: null, updatedAt: "2026-08-18T08:00:00.0000000+00:00");
        InsertAttempt(connection, A4, T5, (int)AssignmentAttemptStatus.Reserved, "agent-y");

        InsertTask(connection, T6, (int)WorkspaceTaskStatus.Completed, version: 2, completedAt: "2026-08-19T08:00:00.0000000+00:00", updatedAt: "2026-08-19T08:05:00.0000000+00:00");
        Exec(connection, $"INSERT INTO task_execution_bindings (task_id, assignment_id, delivery_id, execution_id, session_id, bound_at_utc) VALUES ('{T6}', 'at6', 'd1', NULL, NULL, '{Now:O}')");
        Exec(connection, $"INSERT INTO task_execution_bindings (task_id, assignment_id, delivery_id, execution_id, session_id, bound_at_utc) VALUES ('{T6}', 'at6', 'd2', 'run-123', '', '{Now:O}')");

        InsertTask(connection, T7, (int)WorkspaceTaskStatus.Completed, version: 2, completedAt: "2026-08-21T08:00:00.0000000+00:00", updatedAt: "2026-08-21T08:05:00.0000000+00:00");
        InsertAttempt(connection, A5, T7, 4, "agent-z");

        // 投影：P1 陈旧子代理（删）、P2 6a8 挂靠存活任务（跳过）、P3 未过期（不列）、P4 挂靠终态任务（删）
        Exec(connection, $"""
            INSERT INTO agent_availability_projection (workspace_id, agent_id, state, activity_reason, version, observed_at_utc, valid_until_utc, reason_code)
            VALUES ('default', '206a9b48-sub-e39dbeb6', 0, 10, 1, '2026-08-27T10:25:28.0000000+00:00', '2026-08-27T10:25:28.0000000+00:00', 'agent_configuration_missing')
            """);
        Exec(connection, $"""
            INSERT INTO agent_availability_projection (workspace_id, agent_id, state, activity_reason, version, observed_at_utc, valid_until_utc, active_task_id, reason_code)
            VALUES ('default', 'default.global_general-assistant.6a8', 4, 3, 9, '2026-09-02T10:40:19.0000000+00:00', '2026-09-02T10:40:19.0000000+00:00', '{T4}', 'active_task_owned')
            """);
        Exec(connection, $"""
            INSERT INTO agent_availability_projection (workspace_id, agent_id, state, activity_reason, version, observed_at_utc, valid_until_utc, reason_code)
            VALUES ('default', 'default.global_general-assistant.258', 2, 0, 5, '2026-09-02T12:00:19.0000000+00:00', '2026-09-02T13:00:00.0000000+00:00', 'idle_confirmed')
            """);
        Exec(connection, $"""
            INSERT INTO agent_availability_projection (workspace_id, agent_id, state, activity_reason, version, observed_at_utc, valid_until_utc, active_task_id, reason_code)
            VALUES ('default', 'default.old-agent', 0, 0, 2, '2026-08-29T00:00:00.0000000+00:00', '2026-08-30T00:00:00.0000000+00:00', '{T3}', 'availability_unknown')
            """);

        return connection;
    }

    private static void InsertTask(SqliteConnection connection, string taskId, int status, long version, string? completedAt, string updatedAt, long? origin = null)
        => Exec(connection, $"""
            INSERT INTO workspace_tasks (task_id, workspace_id, title, status, priority, execution_window,
                origin, version, created_at_utc, updated_at_utc, completed_at_utc)
            VALUES ('{taskId}', 'ws1', 'title-{taskId}', {status}, 1, 0, {origin?.ToString(CultureInfo.InvariantCulture) ?? "NULL"},
                {version}, '2026-08-01T00:00:00.0000000+00:00', '{updatedAt}', {QuoteOrNull(completedAt)})
            """);

    private static void InsertEvent(SqliteConnection connection, string eventId, string taskId, long sequence, TaskEventType eventType,
        string? assignmentId = null, string? agentId = null, string? executionId = null, string? sessionId = null, string? decisionCode = null)
        => Exec(connection, $"""
            INSERT INTO task_events (event_id, task_id, workspace_id, sequence, event_type, assignment_id, agent_id,
                execution_id, session_id, decision_code, created_at_utc)
            VALUES ('{eventId}', '{taskId}', 'ws1', {sequence}, {(int)eventType}, {QuoteOrNull(assignmentId)}, {QuoteOrNull(agentId)},
                {QuoteOrNull(executionId)}, {QuoteOrNull(sessionId)}, {QuoteOrNull(decisionCode)}, '2026-08-25T09:00:00.0000000+00:00')
            """);

    private static void InsertAttempt(SqliteConnection connection, string attemptId, string taskId, int status, string agentId)
        => Exec(connection, $"""
            INSERT INTO task_assignment_attempts (attempt_id, task_id, workspace_id, agent_id, attempt_number, status,
                created_at_utc, updated_at_utc, active_at_utc, released_at_utc)
            VALUES ('{attemptId}', '{taskId}', 'ws1', '{agentId}', 1, {status},
                '2026-08-10T00:00:00.0000000+00:00', '2026-08-10T00:00:00.0000000+00:00', '2026-08-10T00:00:00.0000000+00:00', NULL)
            """);

    private static string QuoteOrNull(string? value) => value is null ? "NULL" : $"'{value}'";

    private static void Exec(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static long ScalarLong(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return (long)(cmd.ExecuteScalar() ?? 0L);
    }

    private static object?[] QueryRow(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        Assert.IsTrue(reader.Read(), $"未查到行：{sql}");
        var values = new object?[reader.FieldCount];
        reader.GetValues(values!);
        return values;
    }

    // DDL 与生产 bootstrapper 逐列一致：
    // WorkspaceTaskSchemaBootstrapper（workspace_tasks/task_events/task_assignment_attempts）、
    // TaskDispatchSchemaBootstrapper（task_execution_bindings）、
    // TaskSchedulingSchemaBootstrapper（agent_availability_projection）。
    private static readonly string[] Schema =
    [
        """
        CREATE TABLE workspace_tasks (
            task_id              TEXT    NOT NULL,
            workspace_id         TEXT    NOT NULL,
            title                TEXT    NOT NULL,
            description          TEXT,
            acceptance_criteria  TEXT,
            status               INTEGER NOT NULL,
            priority             INTEGER NOT NULL,
            execution_window     INTEGER NOT NULL,
            preferred_agent_id   TEXT,
            task_type            TEXT    NOT NULL DEFAULT 'general',
            required_capabilities_json TEXT NOT NULL DEFAULT '[]',
            required_provider_id TEXT,
            required_model_id    TEXT,
            allow_agent_fallback INTEGER NOT NULL DEFAULT 0,
            auto_dispatch_enabled INTEGER NOT NULL DEFAULT 0,
            active_assignment_id TEXT,
            not_before_utc       TEXT,
            due_at_utc           TEXT,
            next_eligible_at_utc TEXT,
            sort_order           INTEGER NOT NULL,
            progress_percent     INTEGER,
            progress_summary     TEXT,
            blocker_kind         TEXT,
            blocker_reason       TEXT,
            failure_code         TEXT,
            failure_reason       TEXT,
            origin               INTEGER,
            version              INTEGER NOT NULL DEFAULT 1,
            created_by           TEXT,
            updated_by           TEXT,
            created_at_utc       TEXT    NOT NULL,
            updated_at_utc       TEXT    NOT NULL,
            completed_at_utc     TEXT,
            failed_at_utc        TEXT,
            archived_at_utc      TEXT,
            PRIMARY KEY (task_id)
        );
        """,
        """
        CREATE TABLE task_events (
            id                   INTEGER PRIMARY KEY AUTOINCREMENT,
            event_id             TEXT    NOT NULL,
            task_id              TEXT    NOT NULL,
            workspace_id         TEXT    NOT NULL,
            sequence             INTEGER NOT NULL,
            event_type           INTEGER NOT NULL,
            assignment_id        TEXT,
            agent_id             TEXT,
            delivery_id          TEXT,
            execution_id         TEXT,
            session_id           TEXT,
            origin               INTEGER,
            priority             INTEGER,
            decision_code        TEXT,
            next_eligible_at_utc TEXT,
            trace_id             TEXT,
            correlation_id       TEXT,
            causation_id         TEXT,
            created_at_utc       TEXT    NOT NULL
        );
        """,
        "CREATE UNIQUE INDEX UX_task_events_task_sequence ON task_events(task_id, sequence);",
        "CREATE UNIQUE INDEX UX_task_events_event_id ON task_events(event_id);",
        """
        CREATE TABLE task_assignment_attempts (
            attempt_id      TEXT    NOT NULL,
            task_id         TEXT    NOT NULL,
            workspace_id    TEXT    NOT NULL,
            agent_id        TEXT    NOT NULL,
            attempt_number  INTEGER NOT NULL DEFAULT 1,
            status          INTEGER NOT NULL,
            window_decision TEXT,
            created_at_utc  TEXT    NOT NULL,
            updated_at_utc  TEXT    NOT NULL,
            active_at_utc   TEXT,
            released_at_utc TEXT,
            PRIMARY KEY (attempt_id)
        );
        """,
        "CREATE UNIQUE INDEX UX_task_assignment_attempts_task_active ON task_assignment_attempts(task_id) WHERE released_at_utc IS NULL;",
        """
        CREATE TABLE task_execution_bindings (
            id             INTEGER PRIMARY KEY AUTOINCREMENT,
            task_id        TEXT    NOT NULL,
            assignment_id  TEXT    NOT NULL,
            delivery_id    TEXT    NOT NULL,
            execution_id   TEXT,
            session_id     TEXT,
            bound_at_utc   TEXT    NOT NULL
        );
        """,
        """
        CREATE TABLE agent_availability_projection (
            id                       INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            workspace_id             TEXT    NOT NULL,
            agent_id                 TEXT    NOT NULL,
            state                    INTEGER NOT NULL,
            activity_reason          INTEGER NOT NULL,
            version                  INTEGER NOT NULL,
            observed_at_utc          TEXT    NOT NULL,
            valid_until_utc          TEXT    NOT NULL,
            idle_since_utc           TEXT,
            main_conversation_id     TEXT,
            active_turn_id           TEXT,
            active_execution_id      TEXT,
            active_task_id           TEXT,
            active_goal_run_id       TEXT,
            active_sub_agent_run_id  TEXT,
            reservation_id           TEXT,
            cooldown_until_utc       TEXT,
            reason_code              TEXT    NOT NULL
        );
        """,
    ];
}
