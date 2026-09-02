using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PuddingCode.Scheduling;
using PuddingCode.Tasks;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services;
using PuddingPlatform.Services.Scheduling;
using PuddingPlatform.Services.Tasks;

namespace PuddingPlatformTests.Services.Tasks;

[TestClass]
public sealed class TaskCompletionSettlementServiceTests
{
    private string _root = null!;
    private PlatformDbContextFactory _factory = null!;
    private readonly DateTimeOffset _now = DateTimeOffset.Parse("2026-09-01T08:00:00Z");
    private static readonly TimeSpan Grace = TimeSpan.FromMinutes(10);

    [TestInitialize]
    public async Task InitializeAsync()
    {
        _root = Path.Combine(
            Path.GetTempPath(),
            "PuddingAgent",
            "completion-settlement-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _factory = CreateFactory(interceptor: null);
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
    }

    [TestCleanup]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [TestMethod]
    public async Task SucceededRun_BeyondGrace_BlocksAndReleasesOwnership()
    {
        await SeedAsync();

        var result = await CreateService().SettleAsync("ws", "task-1");

        Assert.IsTrue(result.Settled);
        Assert.AreEqual("blocked", result.Action);
        await using var db = await _factory.CreateDbContextAsync();
        var task = await db.WorkspaceTasks.SingleAsync();
        Assert.AreEqual(WorkspaceTaskStatus.Blocked, task.Status);
        Assert.AreEqual("execution_terminal_without_task_settlement", task.BlockerKind);
        Assert.IsNull(task.ActiveAssignmentId);
        Assert.AreEqual(3, task.Version);
        var attempt = await db.TaskAssignmentAttempts.SingleAsync();
        Assert.IsNotNull(attempt.ReleasedAtUtc);
        Assert.AreEqual(AssignmentAttemptStatus.Failed, attempt.Status);
        var binding = await db.TaskExecutionBindings.SingleAsync();
        Assert.AreEqual("run-1", binding.ExecutionId);
        Assert.AreEqual("conv-1", binding.SessionId);
        var evt = await db.TaskEvents.SingleAsync();
        Assert.AreEqual(TaskEventType.TaskBlocked, evt.EventType);
        Assert.AreEqual("assignment-1", evt.CausationId);
        Assert.AreEqual("run-1", evt.CorrelationId);
        Assert.AreEqual("execution_terminal_without_task_settlement", evt.DecisionCode);
    }

    [TestMethod]
    public async Task FailedRun_BeyondGrace_FailsTask()
    {
        await SeedAsync(runStatus: "failed");

        var result = await CreateService().SettleAsync("ws", "task-1");

        Assert.IsTrue(result.Settled);
        Assert.AreEqual("failed", result.Action);
        await using var db = await _factory.CreateDbContextAsync();
        var task = await db.WorkspaceTasks.SingleAsync();
        Assert.AreEqual(WorkspaceTaskStatus.Failed, task.Status);
        Assert.AreEqual("execution_run_failed", task.FailureCode);
        Assert.IsNull(task.ActiveAssignmentId);
        var evt = await db.TaskEvents.SingleAsync();
        Assert.AreEqual(TaskEventType.TaskFailed, evt.EventType);
    }

    [TestMethod]
    public async Task WithinGrace_Noop()
    {
        await SeedAsync(runCompletedMinutesAgo: 2);

        var result = await CreateService().SettleAsync("ws", "task-1");

        Assert.IsFalse(result.Settled);
        Assert.AreEqual("within_settlement_grace", result.Code);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.AreEqual(WorkspaceTaskStatus.InProgress, (await db.WorkspaceTasks.SingleAsync()).Status);
        Assert.AreEqual(0, await db.TaskEvents.CountAsync());
    }

    [TestMethod]
    public async Task ReleasedAssignment_StaleFence_Noop()
    {
        await SeedAsync(attemptReleased: true);

        var result = await CreateService().SettleAsync("ws", "task-1");

        Assert.IsFalse(result.Settled);
        Assert.AreEqual("assignment_already_released", result.Code);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.AreEqual(0, await db.TaskEvents.CountAsync());
    }

    [TestMethod]
    public async Task RepeatedSettlement_IsIdempotent_SingleCanonicalTerminal()
    {
        await SeedAsync();
        var service = CreateService();

        var first = await service.SettleAsync("ws", "task-1");
        var second = await service.SettleAsync("ws", "task-1");

        Assert.IsTrue(first.Settled);
        Assert.IsFalse(second.Settled);
        Assert.AreEqual("no_active_assignment", second.Code);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.AreEqual(1, await db.TaskEvents.CountAsync());
        Assert.AreEqual(WorkspaceTaskStatus.Blocked, (await db.WorkspaceTasks.SingleAsync()).Status);
        Assert.IsNull((await db.WorkspaceTasks.SingleAsync()).ActiveAssignmentId);
    }

    [TestMethod]
    public async Task FaultInjection_MidTransaction_NoHalfState()
    {
        await SeedAsync(bindingExecutionId: null, deliveryClaimExecutionId: "run-1");
        var faultFactory = CreateFactory(new FailOnWriteCommandInterceptor());

        var result = await CreateService(faultFactory).SettleAsync("ws", "task-1");

        Assert.IsFalse(result.Settled);
        Assert.AreEqual("settlement_conflict", result.Code);
        await using var db = await _factory.CreateDbContextAsync();
        var task = await db.WorkspaceTasks.SingleAsync();
        Assert.AreEqual(WorkspaceTaskStatus.InProgress, task.Status);
        Assert.AreEqual("assignment-1", task.ActiveAssignmentId);
        Assert.AreEqual(2, task.Version);
        Assert.IsNull((await db.TaskAssignmentAttempts.SingleAsync()).ReleasedAtUtc);
        Assert.IsNull((await db.TaskExecutionBindings.SingleAsync()).ExecutionId);
        Assert.AreEqual(0, await db.TaskEvents.CountAsync());
    }

    [TestMethod]
    public async Task LegacyCompletedTask_BackfillsFactsAndAvailabilityGoesIdle()
    {
        await SeedAsync(
            taskStatus: WorkspaceTaskStatus.Completed,
            withGoalBinding: true);

        var result = await CreateService().SettleAsync("ws", "task-1");

        Assert.IsTrue(result.Settled);
        Assert.AreEqual("completed_backfill", result.Action);
        await using var db = await _factory.CreateDbContextAsync();
        var task = await db.WorkspaceTasks.SingleAsync();
        Assert.AreEqual(WorkspaceTaskStatus.Completed, task.Status);
        Assert.IsNotNull(task.CompletedAtUtc);
        Assert.IsNull(task.ActiveAssignmentId);
        Assert.AreEqual(1, await db.TaskEvents.CountAsync(e => e.EventType == TaskEventType.TaskCompleted));
        var attempt = await db.TaskAssignmentAttempts.SingleAsync();
        Assert.AreEqual(AssignmentAttemptStatus.Completed, attempt.Status);
        Assert.IsNotNull(attempt.ReleasedAtUtc);
        Assert.AreEqual("terminal", (await db.TaskGoalBindings.SingleAsync()).Status);
        Assert.AreEqual("released", (await db.AgentExecutionReservations.SingleAsync()).Status);

        var availability = await CreateAvailabilityStore().RebuildAsync("ws", "agent-1");
        Assert.AreEqual(AgentAvailabilityState.Idle, availability.State);
    }

    [TestMethod]
    public async Task ClaimResolvedFromDeliveryClaim_WhenBindingExecutionIdEmpty()
    {
        await SeedAsync(bindingExecutionId: null, deliveryClaimExecutionId: "run-1");

        var result = await CreateService().SettleAsync("ws", "task-1");

        Assert.IsTrue(result.Settled);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.AreEqual("run-1", (await db.TaskExecutionBindings.SingleAsync()).ExecutionId);
    }

    [TestMethod]
    public async Task AvailabilityRebuildFailure_DoesNotBreakCommittedSettlement()
    {
        await SeedAsync();

        var result = await CreateService(availabilityStore: new ThrowingAvailabilityStore())
            .SettleAsync("ws", "task-1");

        Assert.IsTrue(result.Settled);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.AreEqual(WorkspaceTaskStatus.Blocked, (await db.WorkspaceTasks.SingleAsync()).Status);
    }

    // ── infrastructure ──────────────────────────────────────

    private TaskCompletionSettlementService CreateService(
        IDbContextFactory<PlatformDbContext>? factory = null,
        IAgentAvailabilityProjectionStore? availabilityStore = null)
        => new(
            factory ?? _factory,
            availabilityStore ?? CreateAvailabilityStore(),
            Options.Create(new CompletionSettlementOptions { Grace = Grace }),
            new FixedTimeProvider(_now),
            NullLogger<TaskCompletionSettlementService>.Instance);

    private AgentAvailabilityProjectionStore CreateAvailabilityStore() => new(
        _factory,
        new Catalog([Agent("agent-1", "conv-1")]),
        new FixedTimeProvider(_now),
        NullLogger<AgentAvailabilityProjectionStore>.Instance);

    private PlatformDbContextFactory CreateFactory(DbCommandInterceptor? interceptor)
    {
        var builder = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite($"Data Source={Path.Combine(_root, "platform.db")};Default Timeout=10");
        if (interceptor is not null)
            builder.AddInterceptors(interceptor);
        return new PlatformDbContextFactory(builder.Options);
    }

    private async Task SeedAsync(
        WorkspaceTaskStatus taskStatus = WorkspaceTaskStatus.InProgress,
        string runStatus = "succeeded",
        int runCompletedMinutesAgo = 30,
        string? bindingExecutionId = "run-1",
        string? deliveryClaimExecutionId = null,
        bool withGoalBinding = false,
        bool attemptReleased = false)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var created = _now.AddMinutes(-60);
        db.WorkspaceTasks.Add(new WorkspaceTaskEntity
        {
            TaskId = "task-1",
            WorkspaceId = "ws",
            Title = "settlement task",
            Description = "settlement task",
            Status = taskStatus,
            Priority = TaskPriority.P1,
            ExecutionWindow = TaskExecutionWindow.Anytime,
            ActiveAssignmentId = "assignment-1",
            CompletedAtUtc = taskStatus == WorkspaceTaskStatus.Completed ? created : null,
            Version = 2,
            CreatedAtUtc = created,
            UpdatedAtUtc = created,
        });
        db.TaskAssignmentAttempts.Add(new TaskAssignmentAttemptEntity
        {
            AttemptId = "assignment-1",
            TaskId = "task-1",
            WorkspaceId = "ws",
            AgentId = "agent-1",
            AttemptNumber = 1,
            Status = AssignmentAttemptStatus.InProgress,
            CreatedAtUtc = created,
            UpdatedAtUtc = created,
            ActiveAtUtc = created,
            ReleasedAtUtc = attemptReleased ? _now.AddMinutes(-5) : null,
        });
        db.TaskExecutionBindings.Add(new TaskExecutionBindingEntity
        {
            TaskId = "task-1",
            AssignmentId = "assignment-1",
            DeliveryId = "delivery-1",
            ExecutionId = bindingExecutionId,
            SessionId = null,
            BoundAtUtc = created,
        });
        db.ExecutionRuns.Add(new ExecutionRunEntity
        {
            RunId = "run-1",
            CommandId = "cmd-1",
            ConversationId = "conv-1",
            Attempt = 1,
            Status = runStatus,
            CompletedAt = _now.AddMinutes(-runCompletedMinutesAgo).ToUnixTimeMilliseconds(),
            TerminalSequence = 1,
        });
        if (deliveryClaimExecutionId is not null)
        {
            db.MessageDeliveries.Add(new MessageDeliveryEntity
            {
                DeliveryId = "delivery-1",
                MessageId = "msg-1",
                WorkspaceId = "ws",
                TargetKind = "agent",
                TargetId = "agent-1",
                Status = "delivered",
                ClaimedByExecutionId = deliveryClaimExecutionId,
                CreatedAt = created.ToUnixTimeMilliseconds(),
            });
        }

        if (withGoalBinding)
        {
            var reservation = new AgentExecutionReservationEntity
            {
                ReservationId = "reservation-1",
                WorkspaceId = "ws",
                AgentId = "agent-1",
                TaskId = "task-1",
                GoalRunId = "goal-1",
                OwnerId = "scheduler",
                Status = "active",
                LeaseUntilUtc = _now.AddHours(1),
                CreatedAtUtc = created,
                UpdatedAtUtc = created,
            };
            db.AgentExecutionReservations.Add(reservation);
            await db.SaveChangesAsync();
            db.TaskGoalBindings.Add(new TaskGoalBindingEntity
            {
                BindingId = "binding-1",
                WorkspaceId = "ws",
                TaskId = "task-1",
                AssignmentId = "assignment-1",
                ExpectedTaskVersion = 2,
                GoalRunId = "goal-1",
                AgentInstanceId = "agent-1",
                ReservationId = "reservation-1",
                ReservationFencingToken = reservation.FencingToken,
                Status = "active",
                IdempotencyKey = "task-goal:ws:task-1:1",
                CreatedAtUtc = created,
            });
        }

        await db.SaveChangesAsync();
    }

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

    private sealed class ThrowingAvailabilityStore : IAgentAvailabilityProjectionStore
    {
        public Task<AgentAvailabilitySnapshot> GetAsync(
            string workspaceId, string agentId, CancellationToken ct = default)
            => throw new InvalidOperationException("injected availability failure");

        public Task<AgentAvailabilitySnapshot> RebuildAsync(
            string workspaceId, string agentId, CancellationToken ct = default)
            => throw new InvalidOperationException("injected availability failure");
    }

    /// <summary>Fails the first write command (INSERT/UPDATE/DELETE) to prove transaction atomicity.</summary>
    private sealed class FailOnWriteCommandInterceptor : DbCommandInterceptor
    {
        private static bool IsWrite(string sql) =>
            sql.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase)
            || sql.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase)
            || sql.StartsWith("DELETE", StringComparison.OrdinalIgnoreCase);

        private static void ThrowIfWrite(DbCommand command)
        {
            if (IsWrite(command.CommandText))
                throw new InvalidOperationException("injected settlement failure");
        }

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result)
        {
            ThrowIfWrite(command);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            ThrowIfWrite(command);
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            ThrowIfWrite(command);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            ThrowIfWrite(command);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
