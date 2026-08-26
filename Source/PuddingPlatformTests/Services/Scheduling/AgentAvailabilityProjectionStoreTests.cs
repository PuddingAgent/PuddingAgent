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

namespace PuddingPlatformTests.Services.Scheduling;

[TestClass]
public sealed class AgentAvailabilityProjectionStoreTests
{
    private string _root = null!;
    private PlatformDbContextFactory _factory = null!;

    [TestInitialize]
    public async Task InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), "PuddingAgent", "availability-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite($"Data Source={Path.Combine(_root, "platform.db")};Default Timeout=10")
            .Options;
        _factory = new PlatformDbContextFactory(options);
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
    public async Task MissingProjection_IsUnknown_AndCannotAcceptAutomaticTask()
    {
        var now = DateTimeOffset.Parse("2026-08-26T00:00:00Z");
        var store = CreateStore([], now);

        var snapshot = await store.GetAsync("ws", "agent-1");

        Assert.AreEqual(AgentAvailabilityState.Unknown, snapshot.State);
        Assert.IsFalse(snapshot.CanAcceptAutomaticTask(now));
    }

    [TestMethod]
    public async Task Rebuild_WithNoActiveFacts_ProducesFreshIdleProjection()
    {
        var now = DateTimeOffset.Parse("2026-08-26T00:00:00Z");
        var store = CreateStore([Agent("agent-1", "conv-1")], now);

        var snapshot = await store.RebuildAsync("ws", "agent-1");

        Assert.AreEqual(AgentAvailabilityState.Idle, snapshot.State);
        Assert.AreEqual(AgentActivityReason.None, snapshot.ActivityReason);
        Assert.AreEqual("idle_confirmed", snapshot.ReasonCode);
        Assert.IsTrue(snapshot.CanAcceptAutomaticTask(now));
        Assert.AreEqual("conv-1", snapshot.MainConversationId);
    }

    [TestMethod]
    public async Task RunningSubAgent_IsBusyWaitingSubAgent_NotIdle()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.SessionSubAgents.Add(new SessionSubAgentEntity
            {
                ParentSessionId = "conv-1",
                ParentAgentId = "agent-1",
                SubSessionId = "sub-1",
                Status = "running",
                TaskSummary = "research",
                SpawnedAt = "2026-08-26T00:00:00Z",
            });
            await db.SaveChangesAsync();
        }

        var now = DateTimeOffset.Parse("2026-08-26T00:00:00Z");
        var snapshot = await CreateStore([Agent("agent-1", "conv-1")], now)
            .RebuildAsync("ws", "agent-1");

        Assert.AreEqual(AgentAvailabilityState.Busy, snapshot.State);
        Assert.AreEqual(AgentActivityReason.WaitingSubAgent, snapshot.ActivityReason);
        Assert.AreEqual("sub-1", snapshot.ActiveSubAgentRunId);
        Assert.IsFalse(snapshot.CanAcceptAutomaticTask(now));
    }

    [TestMethod]
    public async Task StaleRunningProjection_WithTerminalLatestRun_DoesNotKeepAgentBusy()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.SessionSubAgents.Add(new SessionSubAgentEntity
            {
                ParentSessionId = "conv-1",
                ParentAgentId = "agent-1",
                SubSessionId = "sub-1",
                Status = "running",
                TaskSummary = "research",
                SpawnedAt = "2026-08-26T00:00:00Z",
            });
            db.SubAgentRuns.Add(new SubAgentRunEntity
            {
                RunId = "run-1",
                ParentSessionId = "conv-1",
                SubSessionId = "sub-1",
                WorkspaceId = "ws",
                AgentInstanceId = "agent-1",
                TemplateId = "general-assistant",
                Status = "completed",
                StartedAt = "2026-08-26T00:00:00Z",
                CompletedAt = "2026-08-26T00:05:00Z",
                ArchivePath = "archive/run-1",
            });
            await db.SaveChangesAsync();
        }

        var now = DateTimeOffset.Parse("2026-08-26T00:10:00Z");
        var snapshot = await CreateStore([Agent("agent-1", "conv-1")], now)
            .RebuildAsync("ws", "agent-1");

        Assert.AreEqual(AgentAvailabilityState.Idle, snapshot.State);
        Assert.AreEqual(AgentActivityReason.None, snapshot.ActivityReason);
    }

    [TestMethod]
    public async Task ActiveAssignment_RemainsBusyWhileRuntimeExecutionSlotIsFree()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.WorkspaceTasks.Add(Task("task-1", WorkspaceTaskStatus.InProgress));
            db.TaskAssignmentAttempts.Add(new TaskAssignmentAttemptEntity
            {
                AttemptId = "assignment-1",
                TaskId = "task-1",
                WorkspaceId = "ws",
                AgentId = "agent-1",
                Status = AssignmentAttemptStatus.InProgress,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var now = DateTimeOffset.Parse("2026-08-26T00:00:00Z");
        var snapshot = await CreateStore([Agent("agent-1", "conv-1")], now)
            .RebuildAsync("ws", "agent-1");

        Assert.AreEqual(AgentAvailabilityState.Busy, snapshot.State);
        Assert.AreEqual(AgentActivityReason.TaskExecution, snapshot.ActivityReason);
        Assert.AreEqual("task-1", snapshot.ActiveTaskId);
    }

    [TestMethod]
    public async Task PendingUserTurn_TakesPriorityOverAutomaticWork()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.ChatExecutionCommands.Add(new ChatExecutionCommandEntity
            {
                CommandId = "cmd-1",
                BatchId = "batch-1",
                WorkspaceId = "ws",
                SessionId = "conv-1",
                UserMessageId = "msg-1",
                TurnId = "turn-1",
                AgentInstanceId = "agent-1",
                Status = "pending",
                CreatedAt = 1,
            });
            await db.SaveChangesAsync();
        }

        var now = DateTimeOffset.Parse("2026-08-26T00:00:00Z");
        var snapshot = await CreateStore([Agent("agent-1", "conv-1")], now)
            .RebuildAsync("ws", "agent-1");

        Assert.AreEqual(AgentAvailabilityState.Busy, snapshot.State);
        Assert.AreEqual(AgentActivityReason.RuntimeExecution, snapshot.ActivityReason);
        Assert.AreEqual("queued_turn", snapshot.ReasonCode);
    }

    private AgentAvailabilityProjectionStore CreateStore(
        IReadOnlyList<WorkspaceAgentDto> agents,
        DateTimeOffset now) => new(
            _factory,
            new Catalog(agents),
            new FixedTimeProvider(now),
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

    private static WorkspaceTaskEntity Task(string id, WorkspaceTaskStatus status) => new()
    {
        TaskId = id,
        WorkspaceId = "ws",
        Title = id,
        Status = status,
        Priority = TaskPriority.P3,
        ExecutionWindow = TaskExecutionWindow.Anytime,
        SortOrder = 0,
        Version = 1,
        CreatedAtUtc = DateTimeOffset.UtcNow,
        UpdatedAtUtc = DateTimeOffset.UtcNow,
    };

    private sealed class Catalog(IReadOnlyList<WorkspaceAgentDto> agents) : IWorkspaceAgentCatalog
    {
        public System.Threading.Tasks.Task<IReadOnlyList<WorkspaceAgentDto>> ListAgentsAsync(
            string workspaceId,
            CancellationToken ct = default) => System.Threading.Tasks.Task.FromResult(agents);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
