using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PuddingCode.Tasks;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services.Tasks;

namespace PuddingPlatformTests.Services.Tasks;

/// <summary>
/// TB-03: TaskCommandService 单元测试（SQLite 文件临时库，EnsureCreated 建表，真实 Store + 真实 Service）。
/// 覆盖契约§八的 Command 语义（Assign/RunNow/Reopen/Archive/Cancel）、CAS/非法转换/未找到、wire 映射。
/// </summary>
[TestClass]
public sealed class TaskCommandServiceTests
{
    private const string WorkspaceId = "ws-1";

    private string _testRoot = null!;
    private PlatformDbContextFactory _dbFactory = null!;
    private SqliteWorkspaceTaskStore _store = null!;
    private TaskCommandService _service = null!;

    [TestInitialize]
    public async Task InitializeAsync()
    {
        _testRoot = Path.Combine(
            Path.GetTempPath(),
            "PuddingAgent",
            "task-command-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testRoot);
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite($"Data Source={Path.Combine(_testRoot, "platform.db")};Default Timeout=10")
            .Options;
        _dbFactory = new PlatformDbContextFactory(options);
        await using var db = await _dbFactory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();

        _store = new SqliteWorkspaceTaskStore(_dbFactory);
        _service = new TaskCommandService(_store, _dbFactory);
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

    // ── 5. Assign：Ready→Reserved + 建 Assignment + active_assignment_id ──

    [TestMethod]
    public async Task Assign_ReadyToReserved_CreatesReservedAssignmentAndSetsActiveId()
    {
        var task = await CreateTaskAsync();
        await SetStatusAsync(task.TaskId, WorkspaceTaskStatus.Ready);

        var result = await _service.ApplyCommandAsync(
            WorkspaceId, task.TaskId, TaskCommand.Assign, expectedVersion: 1, agentId: "agent-1");

        Assert.AreEqual(WorkspaceTaskStatus.Reserved, result.Status);
        Assert.AreEqual(2, result.Version);
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.ActiveAssignmentId));

        var attempts = await GetAttemptsAsync(task.TaskId);
        Assert.AreEqual(1, attempts.Count);
        Assert.AreEqual(AssignmentAttemptStatus.Reserved, attempts[0].Status);
        Assert.AreEqual("agent-1", attempts[0].AgentId);
        Assert.AreEqual(1, attempts[0].AttemptNumber);
        Assert.IsNull(attempts[0].ReleasedAtUtc);
        Assert.AreEqual(result.ActiveAssignmentId, attempts[0].AttemptId);

        var events = await GetEventsAsync(task.TaskId);
        Assert.AreEqual(TaskEventType.TaskReserved, events[^1].EventType);
        Assert.AreEqual(result.ActiveAssignmentId, events[^1].AssignmentId);
        Assert.AreEqual("agent-1", events[^1].AgentId);
    }

    // ── 6. RunNow：Deferred→Reserved + 建 Assignment + windowDecision ──

    [TestMethod]
    public async Task RunNow_DeferredToReserved_RecordsWindowDecision()
    {
        var task = await CreateTaskAsync();
        await SetStatusAsync(task.TaskId, WorkspaceTaskStatus.Deferred);

        var result = await _service.ApplyCommandAsync(
            WorkspaceId, task.TaskId, TaskCommand.RunNow, expectedVersion: 1,
            agentId: "agent-2", windowDecision: "allowed_user_direct");

        Assert.AreEqual(WorkspaceTaskStatus.Reserved, result.Status);

        var attempt = (await GetAttemptsAsync(task.TaskId)).Single();
        Assert.AreEqual(AssignmentAttemptStatus.Reserved, attempt.Status);
        Assert.AreEqual("allowed_user_direct", attempt.WindowDecision);
        Assert.AreEqual("agent-2", attempt.AgentId);
    }

    // ── 7. Reopen：Failed→Ready + version 递增 + task.reopened 事件 ──

    [TestMethod]
    public async Task Reopen_FailedToReady_IncrementsVersionAndAppendsReopenedEvent()
    {
        var task = await CreateTaskAsync();
        await SetStatusAsync(task.TaskId, WorkspaceTaskStatus.Failed);

        var result = await _service.ApplyCommandAsync(
            WorkspaceId, task.TaskId, TaskCommand.Reopen, expectedVersion: 1);

        Assert.AreEqual(WorkspaceTaskStatus.Ready, result.Status);
        Assert.AreEqual(2, result.Version);

        var events = await GetEventsAsync(task.TaskId);
        Assert.AreEqual(TaskEventType.TaskReopened, events[^1].EventType);
        Assert.AreEqual(2, events[^1].Sequence);
    }

    // ── 8. Archive：Completed→Archived ──

    [TestMethod]
    public async Task Archive_CompletedToArchived_SetsArchivedAt()
    {
        var task = await CreateTaskAsync();
        await SetStatusAsync(task.TaskId, WorkspaceTaskStatus.Completed);

        var result = await _service.ApplyCommandAsync(
            WorkspaceId, task.TaskId, TaskCommand.Archive, expectedVersion: 1);

        Assert.AreEqual(WorkspaceTaskStatus.Archived, result.Status);
        Assert.AreEqual(2, result.Version);
        Assert.IsNotNull(result.ArchivedAtUtc);
    }

    // ── 9. Cancel：InProgress→Cancelled + 释放 active Assignment ──

    [TestMethod]
    public async Task Cancel_InProgressToCancelled_ReleasesActiveAssignment()
    {
        var task = await CreateTaskAsync();
        var attemptId = await SeedActiveAssignmentAsync(task.TaskId, "agent-3");

        var result = await _service.ApplyCommandAsync(
            WorkspaceId, task.TaskId, TaskCommand.Cancel, expectedVersion: 1);

        Assert.AreEqual(WorkspaceTaskStatus.Cancelled, result.Status);
        Assert.IsNull(result.ActiveAssignmentId);

        var attempt = (await GetAttemptsAsync(task.TaskId)).Single();
        Assert.AreEqual(attemptId, attempt.AttemptId);
        Assert.IsNotNull(attempt.ReleasedAtUtc);

        var events = await GetEventsAsync(task.TaskId);
        Assert.AreEqual(TaskEventType.TaskCancelled, events[^1].EventType);
    }

    // ── 非法转换 / CAS 冲突 / 未找到 ──

    [TestMethod]
    public async Task ApplyCommand_InvalidTransition_Throws()
    {
        var task = await CreateTaskAsync(); // Backlog

        var ex = await Assert.ThrowsExactlyAsync<TaskStoreException>(() =>
            _service.ApplyCommandAsync(WorkspaceId, task.TaskId, TaskCommand.Assign, expectedVersion: 1, agentId: "agent-1"));

        Assert.AreEqual(TaskErrorCode.TaskInvalidTransition, ex.ErrorCode);
    }

    [TestMethod]
    public async Task ApplyCommand_VersionConflict_ThrowsWithActualVersion()
    {
        var task = await CreateTaskAsync();
        await SetStatusAsync(task.TaskId, WorkspaceTaskStatus.Ready);

        var ex = await Assert.ThrowsExactlyAsync<TaskStoreException>(() =>
            _service.ApplyCommandAsync(WorkspaceId, task.TaskId, TaskCommand.Assign, expectedVersion: 99, agentId: "agent-1"));

        Assert.AreEqual(TaskErrorCode.TaskVersionConflict, ex.ErrorCode);
        Assert.AreEqual(99, ex.ExpectedVersion);
        Assert.AreEqual(1, ex.ActualVersion);
    }

    [TestMethod]
    public async Task ApplyCommand_NotFound_Throws()
    {
        var ex = await Assert.ThrowsExactlyAsync<TaskStoreException>(() =>
            _service.ApplyCommandAsync(WorkspaceId, "missing", TaskCommand.Cancel, expectedVersion: 1));

        Assert.AreEqual(TaskErrorCode.TaskNotFound, ex.ErrorCode);
    }

    // ── 10. wire 映射：枚举 ↔ wire 双向一致（含未知 fail-closed）──

    [TestMethod]
    public void WireMaps_StatusRoundTrip_AllValues()
    {
        foreach (WorkspaceTaskStatus status in Enum.GetValues<WorkspaceTaskStatus>())
        {
            var wire = TaskWireMaps.StatusToString(status);
            Assert.AreEqual(status, TaskWireMaps.StatusFromString(wire));
        }
    }

    [TestMethod]
    public void WireMaps_PriorityAndWindowRoundTrip()
    {
        foreach (TaskPriority p in Enum.GetValues<TaskPriority>())
        {
            Assert.AreEqual(p, TaskWireMaps.PriorityFromString(TaskWireMaps.PriorityToString(p)));
        }

        foreach (TaskExecutionWindow w in Enum.GetValues<TaskExecutionWindow>())
        {
            Assert.AreEqual(w, TaskWireMaps.ExecutionWindowFromString(TaskWireMaps.ExecutionWindowToString(w)));
        }
    }

    [TestMethod]
    public void WireMaps_UnknownValue_FailsClosed()
    {
        var statusEx = Assert.ThrowsExactly<TaskStoreException>(() => TaskWireMaps.StatusFromString("Bogus"));
        Assert.AreEqual(TaskErrorCode.TaskInvalidTransition, statusEx.ErrorCode);

        Assert.ThrowsExactly<TaskStoreException>(() => TaskWireMaps.PriorityFromString("p9"));
        Assert.ThrowsExactly<TaskStoreException>(() => TaskWireMaps.ExecutionWindowFromString("weekends"));
    }

    // ── helpers ─────────────────────────────────────────────

    private async Task<WorkspaceTask> CreateTaskAsync(string title = "Task")
        => await _store.CreateTaskAsync(new CreateTaskRequest
        {
            WorkspaceId = WorkspaceId,
            Title = title,
        });

    private async Task SetStatusAsync(string taskId, WorkspaceTaskStatus status)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var entity = await db.WorkspaceTasks.SingleAsync(t => t.TaskId == taskId);
        entity.Status = status;
        await db.SaveChangesAsync();
    }

    private async Task<string> SeedActiveAssignmentAsync(string taskId, string agentId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var attempt = new TaskAssignmentAttemptEntity
        {
            AttemptId = "attempt-seed",
            TaskId = taskId,
            WorkspaceId = WorkspaceId,
            AgentId = agentId,
            AttemptNumber = 1,
            Status = AssignmentAttemptStatus.InProgress,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            ActiveAtUtc = DateTimeOffset.UtcNow,
            ReleasedAtUtc = null,
        };
        db.TaskAssignmentAttempts.Add(attempt);
        var entity = await db.WorkspaceTasks.SingleAsync(t => t.TaskId == taskId);
        entity.Status = WorkspaceTaskStatus.InProgress;
        entity.ActiveAssignmentId = attempt.AttemptId;
        await db.SaveChangesAsync();
        return attempt.AttemptId;
    }

    private async Task<List<TaskEventEntity>> GetEventsAsync(string taskId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.TaskEvents
            .Where(e => e.TaskId == taskId)
            .OrderBy(e => e.Sequence)
            .ToListAsync();
    }

    private async Task<List<TaskAssignmentAttemptEntity>> GetAttemptsAsync(string taskId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.TaskAssignmentAttempts
            .Where(a => a.TaskId == taskId)
            .OrderBy(a => a.AttemptNumber)
            .ToListAsync();
    }
}
