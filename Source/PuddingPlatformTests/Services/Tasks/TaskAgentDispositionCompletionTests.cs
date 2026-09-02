using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Scheduling;
using PuddingCode.Tasks;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services;
using PuddingPlatform.Services.Scheduling;
using PuddingPlatform.Services.Tasks;

namespace PuddingPlatformTests.Services.Tasks;

/// <summary>
/// 4ed930e7 统一完成事实生产接线：canonical 完成路径（ApplyDispositionAsync(Completed)）
/// 必须与 TaskCompletionSettlementService 的事实闭环一致——完成事务内释放该 Task 的 active
/// AgentExecutionReservation，提交后 best-effort 重建 Agent availability。
/// PATCH 旁路（执行中任务直写 Completed 必须拒绝）由 TaskCommandServiceTests 覆盖。
/// </summary>
[TestClass]
public sealed class TaskAgentDispositionCompletionTests
{
    private const string WorkspaceId = "ws-1";
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-01T08:00:00Z");

    private string _testRoot = null!;
    private PlatformDbContextFactory _dbFactory = null!;
    private RecordingAvailabilityStore _availability = null!;
    private TaskAgentCommandService _service = null!;

    [TestInitialize]
    public async Task InitializeAsync()
    {
        _testRoot = Path.Combine(
            Path.GetTempPath(),
            "PuddingAgent",
            "disposition-completion-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testRoot);
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite($"Data Source={Path.Combine(_testRoot, "platform.db")};Default Timeout=10")
            .Options;
        _dbFactory = new PlatformDbContextFactory(options);
        await using var db = await _dbFactory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();

        _availability = new RecordingAvailabilityStore(CreateInnerAvailabilityStore());
        _service = new TaskAgentCommandService(
            _dbFactory,
            new ManualAlwaysAllowFence(),
            _availability,
            NullLogger<TaskAgentCommandService>.Instance);
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

    [TestMethod]
    public async Task CompletedDisposition_ReleasesActiveReservationAndRebuildsAvailability()
    {
        await SeedAsync();

        var result = await _service.ApplyDispositionAsync(new TaskAgentUpdateRequest
        {
            WorkspaceId = WorkspaceId,
            TaskId = "task-1",
            AssignmentId = "assignment-1",
            ExpectedVersion = 1,
            AgentId = "agent-1",
            Disposition = "completed",
        });

        Assert.AreEqual("Completed", result.Status);

        await using var db = await _dbFactory.CreateDbContextAsync();
        var task = await db.WorkspaceTasks.SingleAsync(t => t.TaskId == "task-1");
        Assert.AreEqual(WorkspaceTaskStatus.Completed, task.Status);
        Assert.IsNull(task.ActiveAssignmentId);
        Assert.IsNotNull(task.CompletedAtUtc);

        var attempt = await db.TaskAssignmentAttempts.SingleAsync(a => a.AttemptId == "assignment-1");
        Assert.AreEqual(AssignmentAttemptStatus.Completed, attempt.Status);
        Assert.IsNotNull(attempt.ReleasedAtUtc);

        // 完成事实与 deterministic settlement 同一闭环：active reservation 必须同事务释放。
        var reservation = await db.AgentExecutionReservations.SingleAsync(r => r.ReservationId == "reservation-1");
        Assert.AreEqual("released", reservation.Status);
        Assert.AreEqual("task_completed", reservation.ReleaseReason);
        Assert.IsNotNull(reservation.ReleasedAtUtc);

        var evt = await db.TaskEvents
            .Where(e => e.TaskId == "task-1")
            .OrderByDescending(e => e.Sequence)
            .FirstAsync();
        Assert.AreEqual(TaskEventType.TaskCompleted, evt.EventType);

        // 提交后 availability 重建（best-effort）必须发生。
        CollectionAssert.Contains(
            _availability.Rebuilt.Select(t => (t.WorkspaceId, t.AgentId)).ToList(),
            (WorkspaceId, "agent-1"));
    }

    [TestMethod]
    public async Task CompletedDisposition_WithoutReservation_StillCompletesAndRebuilds()
    {
        await SeedAsync(withReservation: false);

        var result = await _service.ApplyDispositionAsync(new TaskAgentUpdateRequest
        {
            WorkspaceId = WorkspaceId,
            TaskId = "task-1",
            AssignmentId = "assignment-1",
            ExpectedVersion = 1,
            AgentId = "agent-1",
            Disposition = "completed",
        });

        Assert.AreEqual("Completed", result.Status);
        Assert.AreEqual(
            (WorkspaceId, "agent-1"),
            (_availability.Rebuilt[^1].WorkspaceId, _availability.Rebuilt[^1].AgentId));

        await using var db = await _dbFactory.CreateDbContextAsync();
        Assert.AreEqual(0, await db.AgentExecutionReservations.CountAsync());
    }

    [TestMethod]
    public async Task CompletedDisposition_AvailabilityRebuildFailure_DoesNotRollbackCompletion()
    {
        await SeedAsync();
        var throwing = new TaskAgentCommandService(
            _dbFactory,
            new ManualAlwaysAllowFence(),
            new ThrowingAvailabilityStore(),
            NullLogger<TaskAgentCommandService>.Instance);

        var result = await throwing.ApplyDispositionAsync(new TaskAgentUpdateRequest
        {
            WorkspaceId = WorkspaceId,
            TaskId = "task-1",
            AssignmentId = "assignment-1",
            ExpectedVersion = 1,
            AgentId = "agent-1",
            Disposition = "completed",
        });

        // 已提交的完成事实不被 best-effort rebuild 失败回滚（与 settlement 语义一致）。
        Assert.AreEqual("Completed", result.Status);
        await using var db = await _dbFactory.CreateDbContextAsync();
        Assert.AreEqual(
            WorkspaceTaskStatus.Completed,
            (await db.WorkspaceTasks.SingleAsync(t => t.TaskId == "task-1")).Status);
        var reservation = await db.AgentExecutionReservations.SingleAsync(r => r.ReservationId == "reservation-1");
        Assert.AreEqual("released", reservation.Status);
    }

    // ── infrastructure ──────────────────────────────────────

    private async Task SeedAsync(bool withReservation = true)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        db.WorkspaceTasks.Add(new WorkspaceTaskEntity
        {
            TaskId = "task-1",
            WorkspaceId = WorkspaceId,
            Title = "completion wiring task",
            Description = "completion wiring task",
            Status = WorkspaceTaskStatus.InProgress,
            Priority = TaskPriority.P1,
            ExecutionWindow = TaskExecutionWindow.Anytime,
            ActiveAssignmentId = "assignment-1",
            Version = 1,
            CreatedAtUtc = Now.AddMinutes(-60),
            UpdatedAtUtc = Now.AddMinutes(-60),
        });
        db.TaskAssignmentAttempts.Add(new TaskAssignmentAttemptEntity
        {
            AttemptId = "assignment-1",
            TaskId = "task-1",
            WorkspaceId = WorkspaceId,
            AgentId = "agent-1",
            AttemptNumber = 1,
            Status = AssignmentAttemptStatus.InProgress,
            CreatedAtUtc = Now.AddMinutes(-60),
            UpdatedAtUtc = Now.AddMinutes(-60),
            ActiveAtUtc = Now.AddMinutes(-60),
        });
        db.TaskExecutionBindings.Add(new TaskExecutionBindingEntity
        {
            TaskId = "task-1",
            AssignmentId = "assignment-1",
            DeliveryId = "delivery-1",
            ExecutionId = "run-1",
            SessionId = null,
            BoundAtUtc = Now.AddMinutes(-60),
        });
        if (withReservation)
        {
            db.AgentExecutionReservations.Add(new AgentExecutionReservationEntity
            {
                ReservationId = "reservation-1",
                WorkspaceId = WorkspaceId,
                AgentId = "agent-1",
                TaskId = "task-1",
                GoalRunId = "goal-1",
                OwnerId = "scheduler",
                Status = "active",
                LeaseUntilUtc = Now.AddHours(1),
                CreatedAtUtc = Now.AddMinutes(-60),
                UpdatedAtUtc = Now.AddMinutes(-60),
            });
        }

        await db.SaveChangesAsync();
    }

    private AgentAvailabilityProjectionStore CreateInnerAvailabilityStore() => new(
        _dbFactory,
        new Catalog([Agent("agent-1", "conv-1")]),
        new FixedTimeProvider(Now),
        NullLogger<AgentAvailabilityProjectionStore>.Instance);

    private static WorkspaceAgentDto Agent(string id, string sessionId) => new(
        id,
        id,
        null,
        id,
        null,
        null,
        "general-assistant",
        sessionId,
        null,
        null,
        null,
        true,
        false,
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow);

    private sealed class Catalog(IReadOnlyList<WorkspaceAgentDto> agents) : IWorkspaceAgentCatalog
    {
        public Task<IReadOnlyList<WorkspaceAgentDto>> ListAgentsAsync(
            string workspaceId,
            CancellationToken ct = default) => Task.FromResult(agents);
    }

    /// <summary>Records post-commit rebuild calls and delegates to a real projection store.</summary>
    private sealed class RecordingAvailabilityStore(IAgentAvailabilityProjectionStore inner) : IAgentAvailabilityProjectionStore
    {
        public List<(string WorkspaceId, string AgentId)> Rebuilt { get; } = [];

        public Task<AgentAvailabilitySnapshot> GetAsync(
            string workspaceId, string agentId, CancellationToken ct = default)
            => inner.GetAsync(workspaceId, agentId, ct);

        public Task<AgentAvailabilitySnapshot> RebuildAsync(
            string workspaceId, string agentId, CancellationToken ct = default)
        {
            Rebuilt.Add((workspaceId, agentId));
            return inner.RebuildAsync(workspaceId, agentId, ct);
        }
    }

    private sealed class ThrowingAvailabilityStore : IAgentAvailabilityProjectionStore
    {
        public Task<AgentAvailabilitySnapshot> GetAsync(
            string workspaceId, string agentId, CancellationToken ct = default)
            => throw new InvalidOperationException("injected availability failure");

        public Task<AgentAvailabilitySnapshot> RebuildAsync(
            string workspaceId, string agentId, CancellationToken ct = default)
            => throw new InvalidOperationException("injected availability failure");
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
