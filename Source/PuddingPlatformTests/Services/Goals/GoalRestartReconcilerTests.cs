using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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

    private static async Task<(PlatformDbContext Db, GoalRestartReconciler Reconciler, GoalRunStore Store)> CreateAsync(
        GoalRunOptions? runOptions = null)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var dbOptions = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite(connection)
            .Options;
        var db = new PlatformDbContext(dbOptions);
        await db.Database.EnsureCreatedAsync();
        var store = new GoalRunStore(db, new NoopSignal(), NullLogger<GoalRunStore>.Instance);
        return (db,
            new GoalRestartReconciler(
                db,
                store,
                Options.Create(runOptions ?? new GoalRunOptions()),
                NullLogger<GoalRestartReconciler>.Instance),
            store);
    }

    private static GoalRunEntity NewGoal(
        string id,
        GoalPhase status,
        string resumePolicy = GoalResumePolicies.Paused) => new()
    {
        GoalRunId = id,
        WorkspaceId = "ws",
        CurrentConversationId = $"conv-{id}",
        AgentInstanceId = "agent-1",
        Objective = "目标",
        Status = status,
        MaxIterations = 256,
        SourceCommandId = $"cmd-{id}",
        ResumePolicy = resumePolicy,
    };

    [TestMethod]
    public async Task Disarm_Converts_Active_To_Paused_With_Reason_And_BootId()
    {
        var (db, reconciler, store) = await CreateAsync();
        await using var _ = db;

        await store.CreateAsync(NewGoal("g-active", GoalPhase.Active), "t", CancellationToken.None);
        await store.CreateAsync(NewGoal("g-paused", GoalPhase.Paused), "t", CancellationToken.None);
        await store.CreateAsync(NewGoal("g-terminal", GoalPhase.Cancelled), "t", CancellationToken.None);

        var result = await reconciler.DisarmActiveGoalsAsync("boot-42", CancellationToken.None);

        Assert.AreEqual(1, result.DisarmedCount);
        Assert.AreEqual(0, result.AutoResumedCount);
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

        Assert.AreEqual(1, first.DisarmedCount);
        Assert.AreEqual(0, second.DisarmedCount); // 已 paused，不再触碰

        var goal = await db.GoalRuns.SingleAsync();
        Assert.AreEqual("boot-1", goal.ActivationBootId); // 保留首次 disarm 的 boot 锚点
        Assert.AreEqual(1, await db.ConversationEvents.CountAsync(e => e.Type == GoalEventTypes.Paused));
    }

    [TestMethod]
    public async Task Disarm_With_No_Goals_Is_A_NoOp()
    {
        var (db, reconciler, _) = await CreateAsync();
        await using var _ = db;

        var result = await reconciler.DisarmActiveGoalsAsync("boot-1", CancellationToken.None);
        Assert.AreEqual(0, result.DisarmedCount);
        Assert.AreEqual(0, result.AutoResumedCount);
    }

    // ── ADR-092：持久 ResumePolicy ─────────────────────────────

    [TestMethod]
    public async Task PolicyPaused_Converts_Active_To_Paused()
    {
        var (db, reconciler, store) = await CreateAsync();
        await using var _ = db;

        await store.CreateAsync(
            NewGoal("g-policy-paused", GoalPhase.Active, GoalResumePolicies.Paused),
            "t", CancellationToken.None);

        var result = await reconciler.DisarmActiveGoalsAsync("boot-p", CancellationToken.None);

        Assert.AreEqual(1, result.DisarmedCount);
        Assert.AreEqual(0, result.AutoResumedCount);
        var goal = await db.GoalRuns.SingleAsync(g => g.GoalRunId == "g-policy-paused");
        Assert.AreEqual(GoalPhase.Paused, goal.Status);
        Assert.AreEqual("core_restart_disarm", goal.StatusReason);
        Assert.AreEqual("boot-p", goal.ActivationBootId);
        Assert.AreEqual(2, goal.ActivationEpoch);
        Assert.AreEqual(1, await db.ConversationEvents.CountAsync(e =>
            e.Type == GoalEventTypes.Paused && e.CorrelationId == "g-policy-paused"));
    }

    [TestMethod]
    public async Task AutoResume_Keeps_Active_And_Renews_Fence()
    {
        var (db, reconciler, store) = await CreateAsync();
        await using var _ = db;

        await store.CreateAsync(
            NewGoal("g-auto", GoalPhase.Active, GoalResumePolicies.AutoResumeOnRestart),
            "t", CancellationToken.None);

        var result = await reconciler.DisarmActiveGoalsAsync("boot-a", CancellationToken.None);

        Assert.AreEqual(0, result.DisarmedCount);
        Assert.AreEqual(1, result.AutoResumedCount);
        var goal = await db.GoalRuns.SingleAsync(g => g.GoalRunId == "g-auto");
        Assert.AreEqual(GoalPhase.Active, goal.Status);    // 保持 Active
        Assert.AreEqual("boot-a", goal.ActivationBootId);  // fence 换发为本次 boot
        Assert.AreEqual(2, goal.ActivationEpoch);          // epoch++
        Assert.AreEqual(1, await db.ConversationEvents.CountAsync(e =>
            e.Type == GoalEventTypes.Resumed && e.CorrelationId == "g-auto"));
    }

    [TestMethod]
    public async Task AutoResume_Is_Idempotent_Per_Boot_And_Skips_NonActive()
    {
        var (db, reconciler, store) = await CreateAsync();
        await using var _ = db;

        await store.CreateAsync(
            NewGoal("g-auto", GoalPhase.Active, GoalResumePolicies.AutoResumeOnRestart),
            "t", CancellationToken.None);
        await store.CreateAsync(NewGoal("g-paused", GoalPhase.Paused), "t", CancellationToken.None);
        await store.CreateAsync(NewGoal("g-cancelled", GoalPhase.Cancelled), "t", CancellationToken.None);

        var first = await reconciler.DisarmActiveGoalsAsync("boot-x", CancellationToken.None);
        var replay = await reconciler.DisarmActiveGoalsAsync("boot-x", CancellationToken.None);

        Assert.AreEqual(1, first.AutoResumedCount);
        Assert.AreEqual(0, replay.AutoResumedCount); // 同 bootId 重放不重复递增
        Assert.AreEqual(0, replay.DisarmedCount);

        var goal = await db.GoalRuns.SingleAsync(g => g.GoalRunId == "g-auto");
        Assert.AreEqual(GoalPhase.Active, goal.Status);
        Assert.AreEqual(2, goal.ActivationEpoch); // 只递增一次
        Assert.AreEqual(1, await db.ConversationEvents.CountAsync(e =>
            e.Type == GoalEventTypes.Resumed && e.CorrelationId == "g-auto"));

        // 非 Active 一律不动（不新增任何 paused/resumed 事件）。
        Assert.AreEqual(GoalPhase.Paused,
            (await db.GoalRuns.SingleAsync(g => g.GoalRunId == "g-paused")).Status);
        Assert.AreEqual(GoalPhase.Cancelled,
            (await db.GoalRuns.SingleAsync(g => g.GoalRunId == "g-cancelled")).Status);
        Assert.AreEqual(0, await db.ConversationEvents.CountAsync(e =>
            (e.Type == GoalEventTypes.Paused || e.Type == GoalEventTypes.Resumed)
            && (e.CorrelationId == "g-paused" || e.CorrelationId == "g-cancelled")));
    }

    [TestMethod]
    public async Task AutoResume_Quota_Excess_Falls_Back_To_Paused()
    {
        var runOptions = new GoalRunOptions { MaxAutoResumesPerBoot = 1 };
        var (db, reconciler, store) = await CreateAsync(runOptions);
        await using var _ = db;

        await store.CreateAsync(
            NewGoal("g-a-1", GoalPhase.Active, GoalResumePolicies.AutoResumeOnRestart),
            "t", CancellationToken.None);
        await store.CreateAsync(
            NewGoal("g-a-2", GoalPhase.Active, GoalResumePolicies.AutoResumeOnRestart),
            "t", CancellationToken.None);

        var result = await reconciler.DisarmActiveGoalsAsync("boot-q", CancellationToken.None);

        // 超出配额的部分按 paused 处理（GoalRunId 升序确定性截断）。
        Assert.AreEqual(1, result.AutoResumedCount);
        Assert.AreEqual(1, result.DisarmedCount);
        var resumed = await db.GoalRuns.SingleAsync(g => g.GoalRunId == "g-a-1");
        var fallback = await db.GoalRuns.SingleAsync(g => g.GoalRunId == "g-a-2");
        Assert.AreEqual(GoalPhase.Active, resumed.Status);
        Assert.AreEqual(2, resumed.ActivationEpoch);
        Assert.AreEqual(GoalPhase.Paused, fallback.Status);
        Assert.AreEqual("core_restart_disarm", fallback.StatusReason);
    }

    [TestMethod]
    public void GoalRunOptions_Validate_Rejects_Unknown_Policy_And_Quota_Out_Of_Range()
    {
        var badPolicy = GoalRunOptions.Validate(new GoalRunOptions { DefaultResumePolicy = "always_on" });
        Assert.AreEqual(1, badPolicy.Count);
        Assert.IsTrue(badPolicy[0].Contains("DefaultResumePolicy", StringComparison.Ordinal));

        var badQuota = GoalRunOptions.Validate(new GoalRunOptions { MaxAutoResumesPerBoot = 65 });
        Assert.AreEqual(1, badQuota.Count);
        Assert.IsTrue(badQuota[0].Contains("MaxAutoResumesPerBoot", StringComparison.Ordinal));

        Assert.AreEqual(0, GoalRunOptions.Validate(new GoalRunOptions
        {
            DefaultResumePolicy = GoalResumePolicies.AutoResumeOnRestart,
            MaxAutoResumesPerBoot = 64,
        }).Count);
    }
}
