using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PuddingCode.Tasks;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services.Tasks;

namespace PuddingPlatformTests.Services.Tasks;

/// <summary>
/// TB-05: Dispatch Outbox Store + Assign 原子提交测试（SQLite 文件临时库）。
/// 覆盖 §7.2：Outbox 与 Assignment/状态/Event 原子提交、幂等键唯一、领取/恢复、绑定查询。
/// </summary>
[TestClass]
public sealed class TaskDispatchStoreTests
{
    private const string WorkspaceId = "ws-1";
    private const string AgentId = "agent-1";

    private string _testRoot = null!;
    private PlatformDbContextFactory _dbFactory = null!;
    private SqliteWorkspaceTaskStore _store = null!;
    private TaskCommandService _commands = null!;
    private TaskDispatchOutboxStore _outbox = null!;

    [TestInitialize]
    public async Task InitializeAsync()
    {
        _testRoot = Path.Combine(
            Path.GetTempPath(),
            "PuddingAgent",
            "task-dispatch-store-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testRoot);
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite($"Data Source={Path.Combine(_testRoot, "platform.db")};Default Timeout=10")
            .Options;
        _dbFactory = new PlatformDbContextFactory(options);
        await using var db = await _dbFactory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();

        _store = new SqliteWorkspaceTaskStore(_dbFactory);
        _commands = new TaskCommandService(_store, _dbFactory);
        _outbox = new TaskDispatchOutboxStore(_dbFactory);
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

    // ── 1. Assign 同事务写 Outbox（不变量 #6）──────────────────────

    [TestMethod]
    public async Task Assign_AppendsOutboxWithUniqueIdempotencyKey_InSameTransaction()
    {
        var task = await CreateReadyTaskAsync();
        var result = await _commands.ApplyCommandAsync(
            WorkspaceId, task.TaskId, TaskCommand.Assign, expectedVersion: 1, agentId: AgentId);

        var outbox = (await _outbox.PeekPendingOutboxAsync(DateTimeOffset.UtcNow)).Single();
        Assert.AreEqual(result.ActiveAssignmentId, outbox.AssignmentId);
        Assert.AreEqual(task.TaskId, outbox.TaskId);
        Assert.AreEqual(AgentId, outbox.AgentId);
        Assert.AreEqual(TaskDispatchOutboxStatuses.Pending, outbox.Status);
        Assert.AreEqual(TaskDispatchIds.BuildIdempotencyKey(task.TaskId, outbox.AssignmentId), outbox.IdempotencyKey);
        Assert.AreEqual(TaskInstructionEnvelope.OriginTaskManual, outbox.Origin);

        // envelope 反序列化正确
        Assert.AreEqual(task.Title, outbox.Envelope.Title);
        Assert.AreEqual(TaskWireMaps.PriorityToString(TaskPriority.P3), outbox.Envelope.Priority);
        Assert.AreEqual(TaskWireMaps.ExecutionWindowToString(TaskExecutionWindow.Inherit), outbox.Envelope.ExecutionWindow);

        // 同事务证据：Assignment + 状态 + Event 均已提交
        Assert.AreEqual(WorkspaceTaskStatus.Reserved, (await _store.GetTaskAsync(WorkspaceId, task.TaskId))!.Status);
        Assert.AreEqual(1, await CountAssignmentsAsync(task.TaskId));
        Assert.AreEqual(TaskEventType.TaskReserved, await GetLastEventTypeAsync(task.TaskId));
    }

    [TestMethod]
    public async Task Assign_OutboxInsertFails_AllChangesRollBack()
    {
        var task = await CreateReadyTaskAsync();

        await ExecuteSqlAsync(
            "CREATE TRIGGER fail_outbox BEFORE INSERT ON task_dispatch_outbox " +
            "BEGIN SELECT RAISE(ABORT, 'forced outbox failure'); END");

        Exception? caught = null;
        try
        {
            await _commands.ApplyCommandAsync(
                WorkspaceId, task.TaskId, TaskCommand.Assign, expectedVersion: 1, agentId: AgentId);
        }
        catch (Exception ex)
        {
            caught = ex;
        }

        Assert.IsNotNull(caught);
        // 全部回滚：Task 仍 Ready（version 未变）、无 Assignment、无 task.reserved、无 Outbox
        var after = await _store.GetTaskAsync(WorkspaceId, task.TaskId);
        Assert.AreEqual(WorkspaceTaskStatus.Ready, after!.Status);
        Assert.AreEqual(1, after.Version);
        Assert.AreEqual(0, await CountAssignmentsAsync(task.TaskId));
        Assert.AreEqual(0, await CountOutboxAsync());
        Assert.AreEqual(TaskEventType.TaskCreated, await GetLastEventTypeAsync(task.TaskId));
    }

    // ── 2. Claim / Recover 生命周期（不变量 #9）────────────────────

    [TestMethod]
    public async Task ClaimAndRecover_ExpiredLeaseBecomesReclaimable()
    {
        var task = await CreateReadyTaskAsync();
        await _commands.ApplyCommandAsync(
            WorkspaceId, task.TaskId, TaskCommand.Assign, expectedVersion: 1, agentId: AgentId);

        var now = DateTimeOffset.UtcNow;
        var pending = (await _outbox.PeekPendingOutboxAsync(now)).Single();

        // 领取：lease 设置、attempt_count +1
        var claimed = await _outbox.ClaimOutboxAsync(pending.Id, now, now.AddMinutes(2));
        Assert.IsNotNull(claimed);
        Assert.AreEqual(1, claimed!.AttemptCount);
        Assert.IsNotNull(claimed.LeaseUntilUtc);

        // 未过期 lease 不可再领取
        Assert.IsNull(await _outbox.ClaimOutboxAsync(pending.Id, now, now.AddMinutes(3)));
        Assert.AreEqual(0, (await _outbox.PeekPendingOutboxAsync(now)).Count);

        // 过期 lease 被恢复后重新可领取
        var later = now.AddMinutes(5);
        Assert.AreEqual(1, await _outbox.RecoverPendingOutboxAsync(later));
        var rePending = (await _outbox.PeekPendingOutboxAsync(later)).Single();
        Assert.AreEqual(pending.Id, rePending.Id);

        var reClaimed = await _outbox.ClaimOutboxAsync(rePending.Id, later, later.AddMinutes(2));
        Assert.IsNotNull(reClaimed);
        Assert.AreEqual(2, reClaimed!.AttemptCount);
    }

    // ── 3. 失败/死信标记 ─────────────────────────────────────────

    [TestMethod]
    public async Task MarkFailedAndDead_TransitionStatus()
    {
        var task = await CreateReadyTaskAsync();
        await _commands.ApplyCommandAsync(
            WorkspaceId, task.TaskId, TaskCommand.Assign, expectedVersion: 1, agentId: AgentId);
        var outbox = (await _outbox.PeekPendingOutboxAsync(DateTimeOffset.UtcNow)).Single();

        await _outbox.MarkOutboxFailedAsync(outbox.Id, "boom");
        var failed = await _outbox.GetOutboxAsync(outbox.Id);
        Assert.AreEqual(TaskDispatchOutboxStatuses.Failed, failed!.Status);
        Assert.AreEqual("boom", failed.LastError);
        Assert.IsNull(failed.LeaseUntilUtc);

        await _outbox.MarkOutboxDeadAsync(outbox.Id, "exhausted");
        var dead = await _outbox.GetOutboxAsync(outbox.Id);
        Assert.AreEqual(TaskDispatchOutboxStatuses.Dead, dead!.Status);
        Assert.AreEqual("exhausted", dead.LastError);
    }

    // ── helpers ─────────────────────────────────────────────

    private async Task<WorkspaceTask> CreateReadyTaskAsync()
    {
        var task = await _store.CreateTaskAsync(new CreateTaskRequest
        {
            WorkspaceId = WorkspaceId,
            Title = "Task",
        });
        await SetStatusAsync(task.TaskId, WorkspaceTaskStatus.Ready);
        return task;
    }

    private async Task SetStatusAsync(string taskId, WorkspaceTaskStatus status)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var entity = await db.WorkspaceTasks.SingleAsync(t => t.TaskId == taskId);
        entity.Status = status;
        await db.SaveChangesAsync();
    }

    private async Task<long> CountOutboxAsync()
        => await ExecuteScalarInt64Async("SELECT COUNT(*) FROM task_dispatch_outbox");

    private async Task<long> CountAssignmentsAsync(string taskId)
        => await ExecuteScalarInt64Async(
            "SELECT COUNT(*) FROM task_assignment_attempts WHERE task_id = @taskId", ("@taskId", taskId));

    private async Task<TaskEventType> GetLastEventTypeAsync(string taskId)
        => (TaskEventType)await ExecuteScalarInt64Async(
            "SELECT event_type FROM task_events WHERE task_id = @taskId ORDER BY sequence DESC LIMIT 1",
            ("@taskId", taskId));

    private async Task ExecuteSqlAsync(string sql)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
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
