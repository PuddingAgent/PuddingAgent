using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PuddingCode.Tasks;
using PuddingPlatform.Data;
using PuddingPlatform.Services.Tasks;

namespace PuddingPlatformTests.Services.Tasks;

/// <summary>
/// Stage 1（母/子层级地基，D1–D5）：parent_task_id 的持久化与<b>位置序回归</b>测试。
/// <para>
/// 核心风险：<c>SqliteWorkspaceTaskStore.TaskColumns</c> 是按序号读取的列清单，
/// <see cref="WorkspaceTask.ParentTaskId"/> 只能追加到清单末尾。本类用「靠后字段打哨兵值 +
/// 原始 SQL 按 TaskColumns 顺序写入」的方式，验证追加后 origin / version / sort_order /
/// auto_dispatch_enabled / archived_at_utc 等字段不错位。
/// </para>
/// </summary>
[TestClass]
public sealed class WorkspaceTaskHierarchyPersistenceTests
{
    // 位置序哨兵值：一旦 parent_task_id 被插到中间，这些断言会立即变红。
    private const long SentinelSortOrder = 4_242_424_242L;
    private const int SentinelVersion = 7;
    private const string SentinelTaskId = "legacy-row-1";
    private const string SentinelWorkspaceId = "ws-sentinel";
    private const string SentinelUpdatedAt = "2026-01-02T03:04:05.0000000+00:00";
    private const string SentinelArchivedAt = "2026-01-09T10:11:12.0000000+00:00";

    private string _testRoot = null!;
    private string _databasePath = null!;
    private PlatformDbContextFactory _dbFactory = null!;
    private SqliteWorkspaceTaskStore _store = null!;

    [TestInitialize]
    public async Task InitializeAsync()
    {
        _testRoot = Path.Combine(
            Path.GetTempPath(),
            "PuddingAgent",
            "task-hierarchy-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testRoot);
        _databasePath = Path.Combine(_testRoot, "platform.db");
        _dbFactory = new PlatformDbContextFactory(BuildOptions(_databasePath));
        await using var db = await _dbFactory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();

        _store = new SqliteWorkspaceTaskStore(_dbFactory);
    }

    [TestCleanup]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
    }

    private static DbContextOptions<PlatformDbContext> BuildOptions(string databasePath)
        => new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite($"Data Source={databasePath};Default Timeout=10")
            .Options;

    // ── 8a. 位置序回归（最高优先级）─────────────────────────────────

    [TestMethod]
    public async Task MapTask_AfterParentTaskIdAppended_ReadsAllLegacyFieldsByOrdinal()
    {
        // 原始 SQL 按 TaskColumns 的**同一顺序**写入（35 个既有列 + 末尾 parent_task_id），
        // 若 MapTask 的序号与 TaskColumns 不一致（例如 parent 被插在中间），下面的哨兵断言必红。
        await ExecuteRawAsync($"""
            INSERT INTO workspace_tasks
              (task_id, workspace_id, title, description, acceptance_criteria, status, priority,
               execution_window, preferred_agent_id, task_type, required_capabilities_json,
               required_provider_id, required_model_id, allow_agent_fallback, auto_dispatch_enabled,
               active_assignment_id, not_before_utc, due_at_utc,
               next_eligible_at_utc, sort_order, progress_percent, progress_summary, blocker_kind,
               blocker_reason, failure_code, failure_reason, version, created_by, updated_by,
               created_at_utc, updated_at_utc, completed_at_utc, failed_at_utc, archived_at_utc, origin,
               parent_task_id)
            VALUES
              ('{SentinelTaskId}', '{SentinelWorkspaceId}', 'sentinel-title', NULL, NULL,
               {(int)WorkspaceTaskStatus.Blocked}, {(int)TaskPriority.P1},
               {(int)TaskExecutionWindow.OffPeakOnly}, 'agent-sentinel', 'implementation', '["code"]',
               'provider-sentinel', 'model-sentinel', 1, 1,
               'assignment-sentinel', NULL, NULL,
               NULL, {SentinelSortOrder}, 66, 'progress-sentinel', 'blocker-kind-sentinel',
               'blocker-reason-sentinel', 'failure-code-sentinel', 'failure-reason-sentinel',
               {SentinelVersion}, 'creator-sentinel', 'updater-sentinel',
               '2026-01-01T00:00:00.0000000+00:00', '{SentinelUpdatedAt}', NULL, NULL, '{SentinelArchivedAt}',
               {(int)TaskOrigin.Auto},
               NULL);
            """);

        var task = await _store.GetTaskAsync(SentinelWorkspaceId, SentinelTaskId);

        Assert.IsNotNull(task);
        // 前置字段
        Assert.AreEqual(SentinelTaskId, task!.TaskId);
        Assert.AreEqual("sentinel-title", task.Title);
        Assert.AreEqual(WorkspaceTaskStatus.Blocked, task.Status);
        Assert.AreEqual(TaskPriority.P1, task.Priority);
        Assert.AreEqual(TaskExecutionWindow.OffPeakOnly, task.ExecutionWindow);
        Assert.AreEqual("agent-sentinel", task.PreferredAgentId);
        Assert.AreEqual("implementation", task.TaskType);
        CollectionAssert.AreEqual(new[] { "code" }, task.RequiredCapabilityIds.ToArray());
        Assert.IsTrue(task.AllowAgentFallback);
        Assert.IsTrue(task.AutoDispatchEnabled);
        Assert.AreEqual("assignment-sentinel", task.ActiveAssignmentId);
        // 靠后字段：排序/版本/进度/失败/归档/来源——位置序错位时首当其冲
        Assert.AreEqual(SentinelSortOrder, task.SortOrder);
        Assert.AreEqual(66, task.ProgressPercent);
        Assert.AreEqual("progress-sentinel", task.ProgressSummary);
        Assert.AreEqual("blocker-kind-sentinel", task.BlockerKind);
        Assert.AreEqual("failure-code-sentinel", task.FailureCode);
        Assert.AreEqual(SentinelVersion, task.Version);
        Assert.AreEqual("creator-sentinel", task.CreatedBy);
        Assert.AreEqual("updater-sentinel", task.UpdatedBy);
        Assert.AreEqual(DateTimeOffset.Parse(SentinelUpdatedAt), task.UpdatedAtUtc);
        Assert.AreEqual(DateTimeOffset.Parse(SentinelArchivedAt), task.ArchivedAtUtc);
        Assert.AreEqual(TaskOrigin.Auto, task.Origin);
        // 末尾新列
        Assert.IsNull(task.ParentTaskId);
    }

    [TestMethod]
    public async Task PhysicalColumnOrder_AppendsParentTaskIdLast_AndLegacyColumnsRemain()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var columns = await ReadWorkspaceTaskColumnsAsync(db);

        Assert.AreEqual(36, columns.Count, "workspace_tasks 应为 35 个既有列 + 1 个新增列。");
        Assert.AreEqual("parent_task_id", columns[^1], "新列必须物理追加在表末尾。");
        Assert.AreEqual(1, columns.Count(c => c == "parent_task_id"));

        // 既有 35 列一个都不能少（不改名、不删除）。
        string[] legacyColumns =
        [
            "task_id", "workspace_id", "title", "description", "acceptance_criteria", "status",
            "priority", "execution_window", "preferred_agent_id", "task_type",
            "required_capabilities_json", "required_provider_id", "required_model_id",
            "allow_agent_fallback", "auto_dispatch_enabled", "active_assignment_id",
            "not_before_utc", "due_at_utc", "next_eligible_at_utc", "sort_order",
            "progress_percent", "progress_summary", "blocker_kind", "blocker_reason",
            "failure_code", "failure_reason", "version", "created_by", "updated_by",
            "created_at_utc", "updated_at_utc", "completed_at_utc", "failed_at_utc",
            "archived_at_utc", "origin",
        ];
        foreach (var legacy in legacyColumns)
        {
            CollectionAssert.Contains(columns, legacy);
        }

        // bootstrap 幂等：再次执行不改变列序、不新增列。
        await WorkspaceTaskSchemaBootstrapper.EnsureCreatedAsync(db);
        var afterBootstrap = await ReadWorkspaceTaskColumnsAsync(db);
        CollectionAssert.AreEqual(columns, afterBootstrap);
    }

    // ── 8b. 旧库升级（不含 parent_task_id 的老表结构）────────────────

    [TestMethod]
    public async Task LegacyDatabase_WithoutParentTaskId_IsUpgradedInPlaceWithDataIntact()
    {
        var legacyPath = Path.Combine(_testRoot, "legacy.db");
        await CreateLegacyDatabaseAsync(legacyPath);

        var legacyFactory = new PlatformDbContextFactory(BuildOptions(legacyPath));
        var legacyStore = new SqliteWorkspaceTaskStore(legacyFactory);

        // 升级前：老表没有 parent_task_id，store 读不回（SELECT 会报 no such column）——这正是升级要解决的。
        await using (var pre = await legacyFactory.CreateDbContextAsync())
        {
            CollectionAssert.DoesNotContain(await ReadWorkspaceTaskColumnsAsync(pre), "parent_task_id");
        }

        // 跑 bootstrap（旧库补列路径）。
        await using (var db = await legacyFactory.CreateDbContextAsync())
        {
            await WorkspaceTaskSchemaBootstrapper.EnsureCreatedAsync(db);
        }

        await using (var post = await legacyFactory.CreateDbContextAsync())
        {
            var columns = await ReadWorkspaceTaskColumnsAsync(post);
            Assert.AreEqual("parent_task_id", columns[^1], "旧库补列必须追加在末尾。");
        }

        // 既有数据完整可读，且新列 NULL（不做任何回填）。
        var task = await legacyStore.GetTaskAsync(SentinelWorkspaceId, SentinelTaskId);
        Assert.IsNotNull(task);
        Assert.IsNull(task!.ParentTaskId);
        Assert.AreEqual(SentinelVersion, task.Version);
        Assert.AreEqual(SentinelSortOrder, task.SortOrder);
        Assert.AreEqual(TaskOrigin.Auto, task.Origin);
        Assert.AreEqual(DateTimeOffset.Parse(SentinelUpdatedAt), task.UpdatedAtUtc);
        Assert.AreEqual(DateTimeOffset.Parse(SentinelArchivedAt), task.ArchivedAtUtc);
        Assert.IsTrue(task.AutoDispatchEnabled);

        // 新库路径下写入的子卡仍可落 parent_task_id，老行为不受影响。
        await ExecuteRawOnFileAsync(
            legacyPath,
            "UPDATE workspace_tasks SET parent_task_id = NULL WHERE task_id = @id",
            ("@id", SentinelTaskId));
        Assert.IsNull((await legacyStore.GetTaskAsync(SentinelWorkspaceId, SentinelTaskId))!.ParentTaskId);
    }

    // ── 7/8d. parent_task_id 写入原语（UPDATE 路径）与默认值 ─────────

    [TestMethod]
    public async Task SetParentTaskIdAsync_PersistsParentAndCanDetachWithCas()
    {
        var parent = await _store.CreateTaskAsync(NewRequest("parent"));
        var child = await _store.CreateTaskAsync(NewRequest("child"));

        Assert.IsNull(parent.ParentTaskId, "新建任务默认无父（既有语义：顶层/独立任务）。");
        Assert.IsNull(child.ParentTaskId);

        var attached = await _store.SetParentTaskIdAsync(child.TaskId, parent.TaskId, child.Version, "manager-1");

        Assert.AreEqual(parent.TaskId, attached.ParentTaskId);
        Assert.AreEqual(child.Version + 1, attached.Version, "CAS：version 必须 +1。");
        Assert.AreEqual("manager-1", attached.UpdatedBy);
        Assert.AreEqual(WorkspaceTaskStatus.Backlog, attached.Status, "D3：挂父不得改动 status。");

        var reloaded = await _store.GetTaskAsync("ws-1", child.TaskId);
        Assert.AreEqual(parent.TaskId, reloaded!.ParentTaskId);
        Assert.IsNull((await _store.GetTaskAsync("ws-1", parent.TaskId))!.ParentTaskId, "母卡自身不得有父（单层）。");

        // 脱挂回顶层（null → NULL）。
        var detached = await _store.SetParentTaskIdAsync(child.TaskId, null, attached.Version, "manager-1");
        Assert.IsNull(detached.ParentTaskId);
    }

    [TestMethod]
    public async Task SetParentTaskIdAsync_ReportsNotFoundAndVersionConflict()
    {
        var task = await _store.CreateTaskAsync(NewRequest("cas"));

        var conflict = await Assert.ThrowsExactlyAsync<TaskStoreException>(
            () => _store.SetParentTaskIdAsync(task.TaskId, "parent-1", task.Version + 5));
        Assert.AreEqual(TaskErrorCode.TaskVersionConflict, conflict.ErrorCode);

        var missing = await Assert.ThrowsExactlyAsync<TaskStoreException>(
            () => _store.SetParentTaskIdAsync("no-such-task", "parent-1", 1));
        Assert.AreEqual(TaskErrorCode.TaskNotFound, missing.ErrorCode);
    }

    // ── helpers ────────────────────────────────────────────────────

    private static CreateTaskRequest NewRequest(string title)
        => new() { WorkspaceId = "ws-1", Title = title, SortOrder = 1 };

    private async Task ExecuteRawAsync(string sql)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await db.Database.ExecuteSqlRawAsync(sql);
    }

    private static async Task ExecuteRawOnFileAsync(string databasePath, string sql, params (string Name, object? Value)[] parameters)
    {
        await using var raw = new SqliteConnection($"Data Source={databasePath};Default Timeout=10");
        await raw.OpenAsync();
        await using var cmd = raw.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<List<string>> ReadWorkspaceTaskColumnsAsync(PlatformDbContext db)
    {
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(workspace_tasks)";
        var columns = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            columns.Add(reader.GetString(1));
        }

        return columns;
    }

    /// <summary>建一个 Stage 1 之前的 workspace_tasks（35 列，无 parent_task_id）并写入一行哨兵数据。</summary>
    private static async Task CreateLegacyDatabaseAsync(string databasePath)
    {
        await using var conn = new SqliteConnection($"Data Source={databasePath};Default Timeout=10");
        await conn.OpenAsync();
        await using var ddl = conn.CreateCommand();
        ddl.CommandText = """
            CREATE TABLE IF NOT EXISTS workspace_tasks (
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
            """;
        await ddl.ExecuteNonQueryAsync();

        await using var insert = conn.CreateCommand();
        insert.CommandText = """
            INSERT INTO workspace_tasks
              (task_id, workspace_id, title, status, priority, execution_window, task_type,
               required_capabilities_json, allow_agent_fallback, auto_dispatch_enabled, sort_order,
               version, origin, created_at_utc, updated_at_utc, archived_at_utc)
            VALUES
              (@taskId, @workspaceId, 'legacy-title', @status, @priority, @window, 'general',
               '[]', 1, 1, @sortOrder,
               @version, @origin, '2026-01-01T00:00:00.0000000+00:00', @updatedAt, @archivedAt)
            """;
        insert.Parameters.AddWithValue("@taskId", SentinelTaskId);
        insert.Parameters.AddWithValue("@workspaceId", SentinelWorkspaceId);
        insert.Parameters.AddWithValue("@status", (int)WorkspaceTaskStatus.InProgress);
        insert.Parameters.AddWithValue("@priority", (int)TaskPriority.P2);
        insert.Parameters.AddWithValue("@window", (int)TaskExecutionWindow.Anytime);
        insert.Parameters.AddWithValue("@sortOrder", SentinelSortOrder);
        insert.Parameters.AddWithValue("@version", SentinelVersion);
        insert.Parameters.AddWithValue("@origin", (int)TaskOrigin.Auto);
        insert.Parameters.AddWithValue("@updatedAt", SentinelUpdatedAt);
        insert.Parameters.AddWithValue("@archivedAt", SentinelArchivedAt);
        await insert.ExecuteNonQueryAsync();
    }
}
