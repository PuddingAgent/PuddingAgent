using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PuddingCode.Goals;
using PuddingCode.Platform;
using PuddingPlatform.Data;
using PuddingPlatform.Services.Goals;

namespace PuddingPlatformTests.Services.Goals;

[TestClass]
public sealed class GoalCommandServiceTests
{
    private sealed class NoopSignal : ICommittedEventSignal
    {
        public ValueTask WaitForChangeAsync(string conversationId, long knownHead, CancellationToken ct)
            => ValueTask.FromCanceled(ct);

        public void Signal(string conversationId, long committedSequence)
        {
        }
    }

    private static async Task<(PlatformDbContext Db, GoalCommandService Service)> CreateAsync(
        bool enabled = true, int defaultMaxIterations = 256)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite(connection)
            .Options;
        var db = new PlatformDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var store = new GoalRunStore(db, new NoopSignal(), NullLogger<GoalRunStore>.Instance);
        var service = new GoalCommandService(
            store,
            Options.Create(new GoalRunOptions
            {
                Enabled = enabled,
                DefaultMaxIterations = defaultMaxIterations,
            }),
            TimeProvider.System,
            NullLogger<GoalCommandService>.Instance);
        return (db, service);
    }

    private static GoalCommandRequest SetRequest(
        string objective = "修复全部失败测试",
        int? rounds = null,
        string clientRequestId = "req-1",
        string conversationId = "conv-1")
        => new("ws", conversationId, "agent-1", "admin", clientRequestId,
            new GoalCommand { Kind = GoalCommandKind.Set, Objective = objective, Rounds = rounds });

    private static GoalCommandRequest SimpleRequest(GoalCommandKind kind, string clientRequestId = "req-1")
        => new("ws", "conv-1", "agent-1", "admin", clientRequestId, new GoalCommand { Kind = kind });

    [TestMethod]
    public async Task Set_Creates_Active_Goal_Without_Any_Agent_Turn()
    {
        var (db, service) = await CreateAsync();
        await using var _ = db;

        var result = await service.ExecuteAsync(SetRequest(), CancellationToken.None);

        Assert.IsTrue(result.Success);
        Assert.AreEqual(GoalPhase.Active, result.Snapshot!.Phase);
        Assert.AreEqual(256, result.Snapshot.MaxIterations);
        // G1 出口：命令不创建 Agent Turn / 执行命令。
        Assert.AreEqual(0, await db.ChatExecutionCommands.CountAsync());
        Assert.AreEqual(0, await db.ConversationTurns.CountAsync());
        Assert.AreEqual(1, await db.GoalRuns.CountAsync());
    }

    [TestMethod]
    public async Task Set_With_Rounds_Persists_Explicit_Budget()
    {
        var (db, service) = await CreateAsync();
        await using var _ = db;

        var result = await service.ExecuteAsync(SetRequest(rounds: 32), CancellationToken.None);

        Assert.IsTrue(result.Success);
        Assert.AreEqual(32, result.Snapshot!.MaxIterations);
    }

    [TestMethod]
    public async Task Set_Replay_With_Same_ClientRequestId_Returns_First_Goal()
    {
        var (db, service) = await CreateAsync();
        await using var _ = db;

        var first = await service.ExecuteAsync(SetRequest(clientRequestId: "req-dup"), CancellationToken.None);
        var replay = await service.ExecuteAsync(SetRequest(
            objective: "另一个目标", clientRequestId: "req-dup"), CancellationToken.None);

        Assert.IsTrue(replay.Success);
        Assert.AreEqual(first.Snapshot!.GoalRunId, replay.Snapshot!.GoalRunId);
        Assert.AreEqual("修复全部失败测试", replay.Snapshot.Objective);
        Assert.AreEqual(1, await db.GoalRuns.CountAsync());
    }

    [TestMethod]
    public async Task Set_Conflicts_With_Existing_NonTerminal_Goal()
    {
        var (db, service) = await CreateAsync();
        await using var _ = db;

        await service.ExecuteAsync(SetRequest(clientRequestId: "req-1"), CancellationToken.None);
        var second = await service.ExecuteAsync(SetRequest(clientRequestId: "req-2"), CancellationToken.None);

        Assert.IsFalse(second.Success);
        Assert.AreEqual(GoalErrorCodes.GoalConflict, second.ErrorCode);
        Assert.AreEqual("req-1", second.Snapshot!.SourceCommandId);
        Assert.AreEqual(1, await db.GoalRuns.CountAsync());
    }

    [TestMethod]
    public async Task Pause_Resume_Keep_Consumed_Iterations_And_Bump_Epoch()
    {
        var (db, service) = await CreateAsync();
        await using var _ = db;

        var created = await service.ExecuteAsync(SetRequest(), CancellationToken.None);
        var paused = await service.ExecuteAsync(
            SimpleRequest(GoalCommandKind.Pause, "req-2"), CancellationToken.None);
        Assert.IsTrue(paused.Success);
        Assert.AreEqual(GoalPhase.Paused, paused.Snapshot!.Phase);
        Assert.AreEqual(created.Snapshot!.ActivationEpoch + 1, paused.Snapshot.ActivationEpoch);

        var resumed = await service.ExecuteAsync(
            SimpleRequest(GoalCommandKind.Resume, "req-3"), CancellationToken.None);
        Assert.IsTrue(resumed.Success);
        Assert.AreEqual(GoalPhase.Active, resumed.Snapshot!.Phase);
    }

    [TestMethod]
    public async Task Resume_Does_Not_Reset_Iteration_Budget()
    {
        var (db, service) = await CreateAsync();
        await using var _ = db;

        await service.ExecuteAsync(SetRequest(rounds: 8), CancellationToken.None);
        // 直接落一个已消费 8/8 的预算耗尽状态（G2 前没有真实 iteration 消费路径）。
        var goal = await db.GoalRuns.SingleAsync();
        goal.IterationsStarted = 8;
        goal.Status = GoalPhase.BudgetExhausted;
        await db.SaveChangesAsync();

        var resumed = await service.ExecuteAsync(
            SimpleRequest(GoalCommandKind.Resume, "req-9"), CancellationToken.None);

        Assert.IsFalse(resumed.Success);
        Assert.AreEqual(GoalErrorCodes.InvalidState, resumed.ErrorCode);
        StringAssert.Contains(resumed.Message, "硬上限");
        Assert.AreEqual(8, resumed.Snapshot!.IterationsStarted);
    }

    [TestMethod]
    public async Task Cancel_Is_A_Terminal_State_That_Allows_New_Goal()
    {
        var (db, service) = await CreateAsync();
        await using var _ = db;

        await service.ExecuteAsync(SetRequest(clientRequestId: "req-1"), CancellationToken.None);
        var cancelled = await service.ExecuteAsync(
            new GoalCommandRequest("ws", "conv-1", "agent-1", "admin", "req-2",
                new GoalCommand { Kind = GoalCommandKind.Cancel, Reason = "用户要求" }),
            CancellationToken.None);

        Assert.IsTrue(cancelled.Success);
        Assert.AreEqual(GoalPhase.Cancelled, cancelled.Snapshot!.Phase);
        Assert.IsNotNull(cancelled.Snapshot.TerminalAtUtc);

        var next = await service.ExecuteAsync(SetRequest(
            objective: "新目标", clientRequestId: "req-3"), CancellationToken.None);
        Assert.IsTrue(next.Success);
        Assert.AreEqual(2, await db.GoalRuns.CountAsync());
    }

    [TestMethod]
    public async Task Replace_Cancels_Old_And_Creates_New_Goal()
    {
        var (db, service) = await CreateAsync();
        await using var _ = db;

        await service.ExecuteAsync(SetRequest(clientRequestId: "req-1"), CancellationToken.None);
        var replaced = await service.ExecuteAsync(
            new GoalCommandRequest("ws", "conv-1", "agent-1", "admin", "req-2",
                new GoalCommand { Kind = GoalCommandKind.Replace, Objective = "换一个目标", Rounds = 16 }),
            CancellationToken.None);

        Assert.IsTrue(replaced.Success);
        Assert.AreEqual("换一个目标", replaced.Snapshot!.Objective);
        Assert.AreEqual(16, replaced.Snapshot.MaxIterations);

        var goals = await db.GoalRuns.ToListAsync();
        Assert.AreEqual(2, goals.Count);
        Assert.AreEqual(1, goals.Count(g => g.Status == GoalPhase.Cancelled));
        Assert.AreEqual(1, goals.Count(g => g.Status == GoalPhase.Active));
    }

    [TestMethod]
    public async Task Edit_Keeps_Identity_But_Updates_Objective_Version()
    {
        var (db, service) = await CreateAsync();
        await using var _ = db;

        var created = await service.ExecuteAsync(SetRequest(), CancellationToken.None);
        var edited = await service.ExecuteAsync(
            new GoalCommandRequest("ws", "conv-1", "agent-1", "admin", "req-2",
                new GoalCommand { Kind = GoalCommandKind.Edit, Objective = "修订后的目标" }),
            CancellationToken.None);

        Assert.IsTrue(edited.Success);
        Assert.AreEqual(created.Snapshot!.GoalRunId, edited.Snapshot!.GoalRunId);
        Assert.AreEqual("修订后的目标", edited.Snapshot.Objective);
        Assert.AreEqual(created.Snapshot.ObjectiveVersion + 1, edited.Snapshot.ObjectiveVersion);
    }

    [TestMethod]
    public async Task ExpectedVersion_Mismatch_Returns_Version_Conflict()
    {
        var (db, service) = await CreateAsync();
        await using var _ = db;

        await service.ExecuteAsync(SetRequest(), CancellationToken.None);
        var stale = await service.ExecuteAsync(
            new GoalCommandRequest(
                "ws", "conv-1", "agent-1", "admin", "req-2",
                new GoalCommand { Kind = GoalCommandKind.Pause })
            {
                ExpectedVersion = 999,
            },
            CancellationToken.None);

        Assert.IsFalse(stale.Success);
        Assert.AreEqual(GoalErrorCodes.VersionConflict, stale.ErrorCode);
    }

    [TestMethod]
    public async Task Status_Reports_Active_Goal_And_Empty_State()
    {
        var (db, service) = await CreateAsync();
        await using var _ = db;

        var empty = await service.ExecuteAsync(
            SimpleRequest(GoalCommandKind.Status), CancellationToken.None);
        Assert.IsTrue(empty.Success);
        StringAssert.Contains(empty.Message, "没有 Goal");

        await service.ExecuteAsync(SetRequest(rounds: 64), CancellationToken.None);
        var status = await service.ExecuteAsync(
            SimpleRequest(GoalCommandKind.Status, "req-2"), CancellationToken.None);
        Assert.IsTrue(status.Success);
        StringAssert.Contains(status.Message, "iteration 0/64");
        StringAssert.Contains(status.Message, "修复全部失败测试");
    }

    [TestMethod]
    public async Task Clear_Rejects_Active_Goal_And_Clears_Terminal_Goal()
    {
        var (db, service) = await CreateAsync();
        await using var _ = db;

        await service.ExecuteAsync(SetRequest(clientRequestId: "req-1"), CancellationToken.None);
        var rejected = await service.ExecuteAsync(
            SimpleRequest(GoalCommandKind.Clear, "req-2"), CancellationToken.None);
        Assert.IsFalse(rejected.Success);
        Assert.AreEqual(GoalErrorCodes.InvalidState, rejected.ErrorCode);

        await service.ExecuteAsync(
            SimpleRequest(GoalCommandKind.Cancel, "req-3"), CancellationToken.None);
        var cleared = await service.ExecuteAsync(
            SimpleRequest(GoalCommandKind.Clear, "req-4"), CancellationToken.None);
        Assert.IsTrue(cleared.Success);
        Assert.IsNotNull(
            (await db.GoalRuns.SingleAsync()).ClearedAtUtc);
        // clear 不删除事件。
        Assert.AreEqual(1, await db.ConversationEvents.CountAsync(e => e.Type == GoalEventTypes.Created));
    }

    [TestMethod]
    public async Task Disabled_Flag_Blocks_Set_But_Allows_Status_Pause_Cancel()
    {
        var (db, service) = await CreateAsync(enabled: false);
        await using var _ = db;

        var set = await service.ExecuteAsync(SetRequest(), CancellationToken.None);
        Assert.IsFalse(set.Success);
        Assert.AreEqual(GoalErrorCodes.GoalDisabled, set.ErrorCode);

        var status = await service.ExecuteAsync(
            SimpleRequest(GoalCommandKind.Status), CancellationToken.None);
        Assert.IsTrue(status.Success);

        var pause = await service.ExecuteAsync(
            SimpleRequest(GoalCommandKind.Pause), CancellationToken.None);
        // 未被 goal_disabled 拦截 = 通过了 flag 门禁；无 Goal 时返回 goal_not_found。
        Assert.IsFalse(pause.Success);
        Assert.AreEqual(GoalErrorCodes.GoalNotFound, pause.ErrorCode);
    }

    [TestMethod]
    public async Task Invalid_DefaultMaxIterations_Config_Fails_Set_Deterministically()
    {
        var (db, service) = await CreateAsync(enabled: true, defaultMaxIterations: 999);
        await using var _ = db;

        // 配置越界（>256）时确定性 fail closed（invalid_rounds），不静默使用越界预算。
        var result = await service.ExecuteAsync(SetRequest(), CancellationToken.None);

        Assert.IsFalse(result.Success);
        Assert.AreEqual(GoalErrorCodes.InvalidRounds, result.ErrorCode);
        StringAssert.Contains(result.Message, "999");
        Assert.AreEqual(0, await db.GoalRuns.CountAsync());
    }
}
