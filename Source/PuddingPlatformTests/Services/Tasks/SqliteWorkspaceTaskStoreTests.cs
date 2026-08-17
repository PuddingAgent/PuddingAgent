using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PuddingCode.Tasks;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services.Tasks;

namespace PuddingPlatformTests.Services.Tasks;

/// <summary>
/// TB-02: SqliteWorkspaceTaskStore 单元测试（SQLite 文件临时库，EnsureCreated 建表）。
/// 覆盖契约§八 9 项场景。
/// </summary>
[TestClass]
public sealed class SqliteWorkspaceTaskStoreTests
{
    private string _testRoot = null!;
    private PlatformDbContextFactory _dbFactory = null!;
    private SqliteWorkspaceTaskStore _store = null!;

    [TestInitialize]
    public async Task InitializeAsync()
    {
        _testRoot = Path.Combine(
            Path.GetTempPath(),
            "PuddingAgent",
            "task-store-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testRoot);
        var databasePath = Path.Combine(_testRoot, "platform.db");
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite($"Data Source={databasePath};Default Timeout=10")
            .Options;
        _dbFactory = new PlatformDbContextFactory(options);
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

    // ── 1. CreateTaskAsync ──────────────────────────────────────────

    [TestMethod]
    public async Task CreateTaskAsync_CreatesBacklogTaskWithSingleCreatedEvent()
    {
        var created = await _store.CreateTaskAsync(
            NewRequest(title: "My Task", priority: TaskPriority.P1, sortOrder: 5));

        Assert.IsFalse(string.IsNullOrWhiteSpace(created.TaskId));
        Assert.AreEqual("ws-1", created.WorkspaceId);
        Assert.AreEqual("My Task", created.Title);
        Assert.AreEqual(WorkspaceTaskStatus.Backlog, created.Status);
        Assert.AreEqual(TaskPriority.P1, created.Priority);
        Assert.AreEqual(5, created.SortOrder);
        Assert.AreEqual(1, created.Version);

        var fetched = await _store.GetTaskAsync("ws-1", created.TaskId);
        Assert.IsNotNull(fetched);
        Assert.AreEqual(WorkspaceTaskStatus.Backlog, fetched!.Status);

        Assert.AreEqual(1, await CountEventsAsync(created.TaskId));
        Assert.AreEqual(TaskEventType.TaskCreated, await GetLastEventTypeAsync(created.TaskId));
        CollectionAssert.AreEqual(new long[] { 1 }, await GetSequencesAsync(created.TaskId));
    }

    // ── 2. GetTaskAsync ─────────────────────────────────────────────

    [TestMethod]
    public async Task GetTaskAsync_ReturnsTaskOrNull()
    {
        var created = await _store.CreateTaskAsync(NewRequest());

        var hit = await _store.GetTaskAsync("ws-1", created.TaskId);
        Assert.IsNotNull(hit);
        Assert.AreEqual(created.TaskId, hit!.TaskId);

        Assert.IsNull(await _store.GetTaskAsync("ws-1", "nonexistent"));
        Assert.IsNull(await _store.GetTaskAsync("ws-other", created.TaskId));
    }

    // ── 3. UpdateTaskAsync 成功 ─────────────────────────────────────

    [TestMethod]
    public async Task UpdateTaskAsync_Succeeds_IncrementsVersionAndAppendsEvent()
    {
        var created = await _store.CreateTaskAsync(
            NewRequest(title: "Old Title", priority: TaskPriority.P3));

        var updated = await _store.UpdateTaskAsync(new UpdateTaskRequest
        {
            TaskId = created.TaskId,
            ExpectedVersion = 1,
            Title = "New Title",
            Priority = TaskPriority.P0,
            SortOrder = 42,
        });

        Assert.AreEqual(2, updated.Version);
        Assert.AreEqual("New Title", updated.Title);
        Assert.AreEqual(TaskPriority.P0, updated.Priority);
        Assert.AreEqual(42, updated.SortOrder);
        // 未提供的字段保持不变
        Assert.AreEqual(created.WorkspaceId, updated.WorkspaceId);
        Assert.AreEqual(created.Description, updated.Description);

        Assert.AreEqual(2, await CountEventsAsync(created.TaskId));
        Assert.AreEqual(TaskEventType.TaskUpdated, await GetLastEventTypeAsync(created.TaskId));
        CollectionAssert.AreEqual(new long[] { 1, 2 }, await GetSequencesAsync(created.TaskId));
    }

    // ── 4. UpdateTaskAsync CAS 冲突 ─────────────────────────────────

    [TestMethod]
    public async Task UpdateTaskAsync_VersionConflict_ThrowsWithActualVersion()
    {
        var created = await _store.CreateTaskAsync(NewRequest());

        await _store.UpdateTaskAsync(new UpdateTaskRequest
        {
            TaskId = created.TaskId,
            ExpectedVersion = 1,
            Title = "v2",
        });

        var ex = await Assert.ThrowsExactlyAsync<TaskStoreException>(() =>
            _store.UpdateTaskAsync(new UpdateTaskRequest
            {
                TaskId = created.TaskId,
                ExpectedVersion = 1,
                Title = "stale",
            }));

        Assert.AreEqual(TaskErrorCode.TaskVersionConflict, ex.ErrorCode);
        Assert.AreEqual(created.TaskId, ex.TaskId);
        Assert.AreEqual(1, ex.ExpectedVersion);
        Assert.AreEqual(2, ex.ActualVersion);
    }

    // ── 5. UpdateTaskAsync 任务不存在 ───────────────────────────────

    [TestMethod]
    public async Task UpdateTaskAsync_NotFound_Throws()
    {
        var ex = await Assert.ThrowsExactlyAsync<TaskStoreException>(() =>
            _store.UpdateTaskAsync(new UpdateTaskRequest
            {
                TaskId = "missing",
                ExpectedVersion = 1,
            }));

        Assert.AreEqual(TaskErrorCode.TaskNotFound, ex.ErrorCode);
        Assert.AreEqual("missing", ex.TaskId);
    }

    // ── 6. QueryTasksAsync 过滤 + keyset 分页 ───────────────────────

    [TestMethod]
    public async Task QueryTasksAsync_FiltersAndPaginatesByKeyset()
    {
        var p0 = await _store.CreateTaskAsync(NewRequest(title: "t-p0", priority: TaskPriority.P0, sortOrder: 10));
        var p1 = await _store.CreateTaskAsync(NewRequest(title: "t-p1", priority: TaskPriority.P1, sortOrder: 20));
        var p1b = await _store.CreateTaskAsync(NewRequest(title: "t-p1b", priority: TaskPriority.P1, sortOrder: 30));

        // priority 过滤
        var p1Results = await _store.QueryTasksAsync(new TaskQuery
        {
            WorkspaceId = "ws-1",
            Priority = TaskPriority.P1,
        });
        CollectionAssert.AreEqual(new[] { p1.TaskId, p1b.TaskId }, p1Results.Select(t => t.TaskId).ToArray());

        // status 过滤：先改 p0 为 Ready，验证 Backlog/Ready 各返回正确集合
        await SetStatusAsync(p0.TaskId, WorkspaceTaskStatus.Ready);
        var backlog = await _store.QueryTasksAsync(new TaskQuery
        {
            WorkspaceId = "ws-1",
            Status = WorkspaceTaskStatus.Backlog,
        });
        CollectionAssert.AreEqual(new[] { p1.TaskId, p1b.TaskId }, backlog.Select(t => t.TaskId).ToArray());

        var ready = await _store.QueryTasksAsync(new TaskQuery
        {
            WorkspaceId = "ws-1",
            Status = WorkspaceTaskStatus.Ready,
        });
        CollectionAssert.AreEqual(new[] { p0.TaskId }, ready.Select(t => t.TaskId).ToArray());

        // keyset 分页：稳定排序 (sort_order, task_id)，limit=2 翻页遍历全量无重复
        var all = new List<WorkspaceTask>();
        string? cursor = null;
        while (true)
        {
            var page = await _store.QueryTasksAsync(new TaskQuery
            {
                WorkspaceId = "ws-1",
                Cursor = cursor,
                Limit = 2,
            });
            if (page.Count == 0)
            {
                break;
            }

            all.AddRange(page);
            cursor = $"{page[^1].SortOrder}|{page[^1].TaskId}";
        }

        Assert.AreEqual(3, all.Count);
        Assert.AreEqual(3, all.Select(t => t.TaskId).Distinct().Count());
        CollectionAssert.AreEqual(new[] { p0.TaskId, p1.TaskId, p1b.TaskId }, all.Select(t => t.TaskId).ToArray());
    }

    // ── 7. HardDeleteTaskAsync ──────────────────────────────────────

    [TestMethod]
    public async Task HardDeleteTaskAsync_DeletesOnlyHistoryFreeBacklog()
    {
        // 无历史 Backlog（仅 1 条 task.created）→ 可删
        var fresh = await _store.CreateTaskAsync(NewRequest(title: "fresh"));
        Assert.IsTrue(await _store.HardDeleteTaskAsync("ws-1", fresh.TaskId));
        Assert.IsNull(await _store.GetTaskAsync("ws-1", fresh.TaskId));
        Assert.AreEqual(0, await CountEventsAsync(fresh.TaskId));

        // 有历史（多条事件）→ 不可删
        var withHistory = await _store.CreateTaskAsync(NewRequest(title: "with-history"));
        await _store.UpdateTaskAsync(new UpdateTaskRequest
        {
            TaskId = withHistory.TaskId,
            ExpectedVersion = 1,
            Title = "updated",
        });
        Assert.IsFalse(await _store.HardDeleteTaskAsync("ws-1", withHistory.TaskId));
        Assert.IsNotNull(await _store.GetTaskAsync("ws-1", withHistory.TaskId));

        // 非 Backlog → 不可删
        var nonBacklog = await _store.CreateTaskAsync(NewRequest(title: "non-backlog"));
        await SetStatusAsync(nonBacklog.TaskId, WorkspaceTaskStatus.Ready);
        Assert.IsFalse(await _store.HardDeleteTaskAsync("ws-1", nonBacklog.TaskId));

        // 不存在 → false
        Assert.IsFalse(await _store.HardDeleteTaskAsync("ws-1", "missing"));
    }

    // ── 8. AppendEventAsync 自动单调 sequence ───────────────────────

    [TestMethod]
    public async Task AppendEventAsync_AutoGeneratesMonotonicSequence()
    {
        var task = await _store.CreateTaskAsync(NewRequest());

        for (var i = 0; i < 3; i++)
        {
            await _store.AppendEventAsync(new TaskEvent
            {
                EventId = $"e-{i}",
                TaskId = task.TaskId,
                WorkspaceId = "ws-1",
                Sequence = 0,
                EventType = TaskEventType.TaskReady,
            });
        }

        // task.created = 1，随后 3 条追加 = 2,3,4（单调递增）。
        CollectionAssert.AreEqual(new long[] { 1, 2, 3, 4 }, await GetSequencesAsync(task.TaskId));
    }

    // ── 9. 原子性：事件插入失败 → task 与 event 均不残留 ─────────────

    [TestMethod]
    public async Task CreateTaskAsync_IsAtomic_NoPartialStateOnEventFailure()
    {
        // 用 BEFORE INSERT 触发器强制 task_events 插入失败，验证 workspace_tasks 插入被回滚。
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            var conn = (SqliteConnection)db.Database.GetDbConnection();
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "CREATE TRIGGER fail_task_events BEFORE INSERT ON task_events " +
                "BEGIN SELECT RAISE(ABORT, 'forced failure'); END";
            await cmd.ExecuteNonQueryAsync();
        }

        Exception? caught = null;
        try
        {
            await _store.CreateTaskAsync(NewRequest());
        }
        catch (Exception ex)
        {
            caught = ex;
        }

        Assert.IsNotNull(caught);
        Assert.AreEqual(0, await CountTasksAsync());
        Assert.AreEqual(0, await CountAllEventsAsync());
    }

    // ── 10. Comments 增查（TB-11）──────────────────────────────────

    [TestMethod]
    public async Task Comments_AddAndList_RoundTripsInCreatedOrder()
    {
        var task = await _store.CreateTaskAsync(NewRequest(title: "comment-target"));

        var first = await _store.AddCommentAsync(
            "ws-1", task.TaskId, TaskCommentAuthorKind.User, "user-1", "第一条", CancellationToken.None);
        var second = await _store.AddCommentAsync(
            "ws-1", task.TaskId, TaskCommentAuthorKind.Agent, "agent-2", "第二条", CancellationToken.None);

        Assert.IsFalse(string.IsNullOrWhiteSpace(first.CommentId));
        Assert.AreEqual("ws-1", first.WorkspaceId);
        Assert.AreEqual(task.TaskId, first.TaskId);

        var comments = await _store.ListCommentsAsync("ws-1", task.TaskId, CancellationToken.None);
        Assert.AreEqual(2, comments.Count);
        Assert.AreEqual(first.CommentId, comments[0].CommentId);
        Assert.AreEqual(TaskCommentAuthorKind.User, comments[0].AuthorKind);
        Assert.AreEqual("user-1", comments[0].AuthorId);
        Assert.AreEqual("第一条", comments[0].Content);
        Assert.AreEqual(second.CommentId, comments[1].CommentId);
        Assert.AreEqual(TaskCommentAuthorKind.Agent, comments[1].AuthorKind);
        Assert.AreEqual("第二条", comments[1].Content);

        // 工作区/任务隔离
        Assert.AreEqual(0, (await _store.ListCommentsAsync("ws-other", task.TaskId, CancellationToken.None)).Count);
        Assert.AreEqual(0, (await _store.ListCommentsAsync("ws-1", "missing", CancellationToken.None)).Count);
    }

    // ── helpers ─────────────────────────────────────────────────────

    private static CreateTaskRequest NewRequest(
        string workspaceId = "ws-1",
        string title = "Task",
        TaskPriority priority = TaskPriority.P3,
        long sortOrder = 0)
        => new CreateTaskRequest
        {
            WorkspaceId = workspaceId,
            Title = title,
            Priority = priority,
            SortOrder = sortOrder,
        };

    private async Task<long> CountTasksAsync()
        => await ExecuteScalarInt64Async("SELECT COUNT(*) FROM workspace_tasks");

    private async Task<long> CountAllEventsAsync()
        => await ExecuteScalarInt64Async("SELECT COUNT(*) FROM task_events");

    private async Task<long> CountEventsAsync(string taskId)
        => await ExecuteScalarInt64Async(
            "SELECT COUNT(*) FROM task_events WHERE task_id = @taskId",
            ("@taskId", taskId));

    private async Task<TaskEventType> GetLastEventTypeAsync(string taskId)
        => (TaskEventType)await ExecuteScalarInt64Async(
            "SELECT event_type FROM task_events WHERE task_id = @taskId ORDER BY sequence DESC LIMIT 1",
            ("@taskId", taskId));

    private async Task<long[]> GetSequencesAsync(string taskId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT sequence FROM task_events WHERE task_id = @taskId ORDER BY sequence ASC";
        cmd.Parameters.AddWithValue("@taskId", taskId);

        var list = new List<long>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(reader.GetInt64(0));
        }

        return list.ToArray();
    }

    private async Task SetStatusAsync(string taskId, WorkspaceTaskStatus status)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE workspace_tasks SET status = @status WHERE task_id = @taskId";
        cmd.Parameters.AddWithValue("@status", (int)status);
        cmd.Parameters.AddWithValue("@taskId", taskId);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<long> ExecuteScalarInt64Async(string sql, params (string Name, object? Value)[] parameters)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var p in parameters)
        {
            cmd.Parameters.AddWithValue(p.Name, p.Value ?? DBNull.Value);
        }

        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }
}
