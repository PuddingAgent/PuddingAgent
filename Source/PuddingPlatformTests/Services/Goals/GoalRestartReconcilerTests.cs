using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Goals;
using PuddingCode.Platform;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services.Goals;

namespace PuddingPlatformTests.Services.Goals;

[TestClass]
public sealed class GoalRestartReconcilerTests
{
    private sealed class NoopSignal : ICommittedEventSignal
    {
        public ValueTask WaitForChangeAsync(string conversationId, long knownHead, CancellationToken ct)
            => ValueTask.FromCanceled(ct);

        public void Signal(string conversationId, long committedSequence)
        {
        }
    }

    private static async Task<(PlatformDbContext Db, GoalRestartReconciler Reconciler, GoalRunStore Store)> CreateAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite(connection)
            .Options;
        var db = new PlatformDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var store = new GoalRunStore(db, new NoopSignal(), NullLogger<GoalRunStore>.Instance);
        return (db, new GoalRestartReconciler(db, store, NullLogger<GoalRestartReconciler>.Instance), store);
    }

    private static GoalRunEntity NewGoal(string id, GoalPhase status) => new()
    {
        GoalRunId = id,
        WorkspaceId = "ws",
        CurrentConversationId = $"conv-{id}",
        AgentInstanceId = "agent-1",
        Objective = "目标",
        Status = status,
        MaxIterations = 256,
        SourceCommandId = $"cmd-{id}",
    };

    [TestMethod]
    public async Task Disarm_Converts_Active_To_Paused_With_Reason_And_BootId()
    {
        var (db, reconciler, store) = await CreateAsync();
        await using var _ = db;

        await store.CreateAsync(NewGoal("g-active", GoalPhase.Active), "t", CancellationToken.None);
        await store.CreateAsync(NewGoal("g-paused", GoalPhase.Paused), "t", CancellationToken.None);
        await store.CreateAsync(NewGoal("g-terminal", GoalPhase.Cancelled), "t", CancellationToken.None);

        var disarmed = await reconciler.DisarmActiveGoalsAsync("boot-42", CancellationToken.None);

        Assert.AreEqual(1, disarmed);
        var active = await db.GoalRuns.SingleAsync(g => g.GoalRunId == "g-active");
        Assert.AreEqual(GoalPhase.Paused, active.Status);
        Assert.AreEqual("core_restart_disarm", active.StatusReason);
        Assert.AreEqual("boot-42", active.ActivationBootId);

        // 非 active 状态不受影响。
        Assert.AreEqual(GoalPhase.Paused,
            (await db.GoalRuns.SingleAsync(g => g.GoalRunId == "g-paused")).Status);
        Assert.AreEqual(GoalPhase.Cancelled,
            (await db.GoalRuns.SingleAsync(g => g.GoalRunId == "g-terminal")).Status);

        // disarm 事件进入 canonical 事件流（SourceKind=goal）。
        Assert.AreEqual(1, await db.ConversationEvents.CountAsync(e =>
            e.Type == GoalEventTypes.Paused &&
            e.CorrelationId == "g-active"));
    }

    [TestMethod]
    public async Task Disarm_Is_Idempotent_Across_Repeated_Runs()
    {
        var (db, reconciler, store) = await CreateAsync();
        await using var _ = db;

        await store.CreateAsync(NewGoal("g-1", GoalPhase.Active), "t", CancellationToken.None);

        var first = await reconciler.DisarmActiveGoalsAsync("boot-1", CancellationToken.None);
        var second = await reconciler.DisarmActiveGoalsAsync("boot-2", CancellationToken.None);

        Assert.AreEqual(1, first);
        Assert.AreEqual(0, second); // 已 paused，不再触碰

        var goal = await db.GoalRuns.SingleAsync();
        Assert.AreEqual("boot-1", goal.ActivationBootId); // 保留首次 disarm 的 boot 锚点
        Assert.AreEqual(1, await db.ConversationEvents.CountAsync(e => e.Type == GoalEventTypes.Paused));
    }

    [TestMethod]
    public async Task Disarm_With_No_Goals_Is_A_NoOp()
    {
        var (db, reconciler, _) = await CreateAsync();
        await using var _ = db;

        Assert.AreEqual(0, await reconciler.DisarmActiveGoalsAsync("boot-1", CancellationToken.None));
    }
}
