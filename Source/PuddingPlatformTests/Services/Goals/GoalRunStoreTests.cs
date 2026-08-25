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
public sealed class GoalRunStoreTests
{
    private sealed class NoopSignal : ICommittedEventSignal
    {
        public ValueTask WaitForChangeAsync(string conversationId, long knownHead, CancellationToken ct)
            => ValueTask.FromCanceled(ct);

        public void Signal(string conversationId, long committedSequence)
        {
        }
    }

    private static async Task<(PlatformDbContext Db, GoalRunStore Store)> CreateAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite(connection)
            .Options;
        var db = new PlatformDbContext(options);
        await db.Database.EnsureCreatedAsync();
        return (db, new GoalRunStore(db, new NoopSignal(), NullLogger<GoalRunStore>.Instance));
    }

    private static GoalRunEntity NewGoal(
        string id = "goal-1",
        int maxIterations = 256,
        string? sourceCommandId = null) => new()
    {
        GoalRunId = id,
        WorkspaceId = "ws",
        CurrentConversationId = "conv-1",
        AgentInstanceId = "agent-1",
        Objective = "修复全部失败测试",
        Status = GoalPhase.Active,
        MaxIterations = maxIterations,
        SourceCommandId = sourceCommandId ?? $"cmd-{id}",
    };

    [TestMethod]
    public async Task Create_Commits_Goal_And_Two_Lifecycle_Events_Atomically()
    {
        var (db, store) = await CreateAsync();
        await using var _ = db;

        var goal = await store.CreateAsync(NewGoal(), "trace-1", CancellationToken.None);

        Assert.AreEqual(GoalPhase.Active, goal.Status);
        Assert.AreEqual(1, await db.GoalRuns.CountAsync());

        var events = await db.ConversationEvents
            .Where(e => e.ConversationId == "conv-1")
            .OrderBy(e => e.Sequence)
            .ToListAsync();
        Assert.AreEqual(2, events.Count);
        Assert.AreEqual(GoalEventTypes.Created, events[0].Type);
        Assert.AreEqual(GoalEventTypes.Activated, events[1].Type);
        Assert.AreEqual(1, events[0].Sequence);
        Assert.AreEqual(2, events[1].Sequence);
        Assert.AreEqual("goal", events[0].SourceKind!.ToString().ToLowerInvariant());
        Assert.AreEqual(goal.GoalRunId, events[0].CorrelationId);
        Assert.AreEqual(GoalProducerComponents.Command, events[0].ProducerComponent);

        var head = await db.ConversationHeads.SingleAsync(h => h.ConversationId == "conv-1");
        Assert.AreEqual(2, head.HeadSequence);
    }

    [TestMethod]
    public async Task Create_Second_Goal_For_Same_Conversation_Violates_Partial_Unique_Index()
    {
        var (db, store) = await CreateAsync();
        await using var _ = db;

        await store.CreateAsync(NewGoal("goal-1"), "t", CancellationToken.None);

        // 第二个非终态 Goal 即违反"单会话一个非终态 Goal"的 partial unique 索引。
        await Assert.ThrowsAsync<DbUpdateException>(
            () => store.CreateAsync(NewGoal("goal-2"), "t", CancellationToken.None));
    }

    [TestMethod]
    public async Task Create_Terminal_Goal_Does_Not_Block_New_Active_Goal()
    {
        var (db, store) = await CreateAsync();
        await using var _ = db;

        var goal = await store.CreateAsync(NewGoal("goal-1"), "t", CancellationToken.None);
        await store.TryMutateAsync(
            goal.GoalRunId, 0,
            g =>
            {
                g.Status = GoalPhase.Cancelled;
                return true;
            },
            new GoalRunStore.GoalEventAppend(GoalEventTypes.Cancelled, new { }),
            "t", CancellationToken.None);

        // 终态 Goal 不占用"单会话最多一个非终态"的唯一槽位。
        var second = await store.CreateAsync(
            NewGoal("goal-2"), "t", CancellationToken.None);
        Assert.AreEqual(GoalPhase.Active, second.Status);
    }

    [TestMethod]
    public async Task TryMutate_Bumps_Version_Writes_Event_And_Returns_Goal()
    {
        var (db, store) = await CreateAsync();
        await using var _ = db;

        var goal = await store.CreateAsync(NewGoal(), "t", CancellationToken.None);
        var versionBefore = goal.AggregateVersion;

        var (mutated, conflict) = await store.TryMutateAsync(
            goal.GoalRunId, 0,
            g =>
            {
                g.Status = GoalPhase.Paused;
                g.StatusReason = "user";
                return true;
            },
            new GoalRunStore.GoalEventAppend(GoalEventTypes.Paused, new { reason = "user" }),
            "t", CancellationToken.None);

        Assert.IsFalse(conflict);
        Assert.IsNotNull(mutated);
        Assert.AreEqual(GoalPhase.Paused, mutated.Status);
        Assert.AreEqual(versionBefore + 1, mutated.AggregateVersion);
        Assert.AreEqual(1, await db.ConversationEvents.CountAsync(e => e.Type == GoalEventTypes.Paused));
    }

    [TestMethod]
    public async Task TryMutate_Rejects_Stale_ExpectedVersion()
    {
        var (db, store) = await CreateAsync();
        await using var _ = db;

        var goal = await store.CreateAsync(NewGoal(), "t", CancellationToken.None);
        await store.TryMutateAsync(
            goal.GoalRunId, 0,
            g => { g.Status = GoalPhase.Paused; return true; },
            new GoalRunStore.GoalEventAppend(GoalEventTypes.Paused, new { }),
            "t", CancellationToken.None);

        // goal-1 的快照 version 已过期（现在是 2）。
        var (mutated, conflict) = await store.TryMutateAsync(
            goal.GoalRunId, expectedVersion: 1,
            g => { g.Status = GoalPhase.Cancelled; return true; },
            new GoalRunStore.GoalEventAppend(GoalEventTypes.Cancelled, new { }),
            "t", CancellationToken.None);

        Assert.IsNull(mutated);
        Assert.IsTrue(conflict);
        Assert.AreEqual(GoalPhase.Paused,
            (await db.GoalRuns.SingleAsync(g => g.GoalRunId == goal.GoalRunId)).Status);
    }

    [TestMethod]
    public async Task TryMutate_Guard_Returning_False_Commits_Nothing()
    {
        var (db, store) = await CreateAsync();
        await using var _ = db;

        var goal = await store.CreateAsync(NewGoal(), "t", CancellationToken.None);

        var (mutated, conflict) = await store.TryMutateAsync(
            goal.GoalRunId, 0,
            g => false, // 状态卫兵失败
            new GoalRunStore.GoalEventAppend(GoalEventTypes.Paused, new { }),
            "t", CancellationToken.None);

        Assert.IsNull(mutated);
        Assert.IsFalse(conflict);
        Assert.AreEqual(1,
            (await db.GoalRuns.SingleAsync(g => g.GoalRunId == goal.GoalRunId)).AggregateVersion);
        Assert.AreEqual(2, await db.ConversationEvents.CountAsync());
    }

    [TestMethod]
    public async Task EventSequences_Allocate_Monotonically_Across_Mutations()
    {
        var (db, store) = await CreateAsync();
        await using var _ = db;

        var goal = await store.CreateAsync(NewGoal(), "t", CancellationToken.None);
        var expectedTypes = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var pausing = i % 2 == 0;
            expectedTypes.Add(pausing ? GoalEventTypes.Paused : GoalEventTypes.Resumed);
            await store.TryMutateAsync(
                goal.GoalRunId, 0,
                g =>
                {
                    g.Status = pausing ? GoalPhase.Paused : GoalPhase.Active;
                    return true;
                },
                new GoalRunStore.GoalEventAppend(
                    pausing ? GoalEventTypes.Paused : GoalEventTypes.Resumed, new { }),
                "t", CancellationToken.None);
        }

        var events = await db.ConversationEvents
            .Where(e => e.ConversationId == "conv-1")
            .OrderBy(e => e.Sequence)
            .Select(e => new { e.Sequence, e.Type })
            .ToListAsync();
        Assert.AreEqual(5, events.Count); // created + activated + 3 次迁移
        CollectionAssert.AreEqual(
            new List<long> { 1, 2, 3, 4, 5 },
            events.Select(e => e.Sequence).ToList());
        CollectionAssert.AreEqual(
            new List<string> { GoalEventTypes.Created, GoalEventTypes.Activated }
                .Concat(expectedTypes)
                .ToList(),
            events.Select(e => e.Type).ToList());
    }

    [TestMethod]
    public async Task FindActive_And_FindBySourceCommand_Query_Correct_Scopes()
    {
        var (db, store) = await CreateAsync();
        await using var _ = db;

        var goal = await store.CreateAsync(NewGoal(), "t", CancellationToken.None);

        Assert.AreEqual(goal.GoalRunId,
            (await store.FindActiveAsync("conv-1", "agent-1"))!.GoalRunId);
        Assert.IsNull(await store.FindActiveAsync("conv-other", "agent-1"));
        Assert.IsNull(await store.FindActiveAsync("conv-1", "agent-other"));

        Assert.AreEqual(goal.GoalRunId,
            (await store.FindBySourceCommandAsync("cmd-goal-1"))!.GoalRunId);
        Assert.IsNull(await store.FindBySourceCommandAsync("cmd-other"));
    }
}
