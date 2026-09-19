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
        bool enabled = true,
        int defaultMaxIterations = 256,
        bool continuationEnabled = false)
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
                ContinuationEnabled = continuationEnabled,
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
    public async Task Set_With_ContinuationEnabled_Commits_First_Durable_Intent_But_No_Turn()
    {
        var (db, service) = await CreateAsync(continuationEnabled: true);
        await using var _ = db;

        var result = await service.ExecuteAsync(SetRequest(), CancellationToken.None);

        Assert.IsTrue(result.Success);
        var outbox = await db.GoalOutbox.SingleAsync();
        Assert.AreEqual(GoalOutboxValues.Pending, outbox.Status);
        Assert.AreEqual(result.Snapshot!.GoalRunId, outbox.GoalRunId);
        Assert.AreEqual(1, outbox.ActivationEpoch);
        Assert.AreEqual(1, outbox.AggregateVersion);
        Assert.AreEqual(0, await db.ChatExecutionCommands.CountAsync());
        Assert.AreEqual(1, await db.ConversationEvents.CountAsync(
            item => item.Type == GoalEventTypes.ContinuationRequested));
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

    // ── ADR-092：/goal policy（resume_policy 写入路径）────────────────

    private static GoalCommandRequest PolicyRequest(
        string resumePolicy,
        string clientRequestId = "req-policy",
        string conversationId = "conv-1")
        => new("ws", conversationId, "agent-1", "admin", clientRequestId,
            new GoalCommand { Kind = GoalCommandKind.Policy, ResumePolicy = resumePolicy });

    [TestMethod]
    public async Task Policy_AutoResumeOnRestart_Persists_Column_And_Appends_Audit_Event()
    {
        var (db, service) = await CreateAsync();
        await using var _ = db;

        await service.ExecuteAsync(SetRequest(), CancellationToken.None);
        var result = await service.ExecuteAsync(
            PolicyRequest(
                GoalResumePolicies.AutoResumeOnRestart,
                clientRequestId: "req-policy-auto"),
            CancellationToken.None);

        Assert.IsTrue(result.Success);
        var goal = await db.GoalRuns.SingleAsync();
        Assert.AreEqual(GoalResumePolicies.AutoResumeOnRestart, goal.ResumePolicy);
        Assert.AreEqual(1, await db.ConversationEvents.CountAsync(
            e => e.Type == GoalEventTypes.PolicyChanged));

        // 审计 payload 记录 from/to。
        var evt = await db.ConversationEvents.SingleAsync(
            e => e.Type == GoalEventTypes.PolicyChanged);
        StringAssert.Contains(evt.Payload, "resume_policy");
        StringAssert.Contains(evt.Payload, GoalResumePolicies.AutoResumeOnRestart);
    }

    [TestMethod]
    public async Task Policy_Invalid_Value_Fails_Closed_Without_Mutation()
    {
        var (db, service) = await CreateAsync();
        await using var _ = db;

        await service.ExecuteAsync(SetRequest(), CancellationToken.None);
        var before = await db.GoalRuns.SingleAsync();

        var result = await service.ExecuteAsync(
            PolicyRequest("always_resume"), CancellationToken.None);

        Assert.IsFalse(result.Success);
        Assert.AreEqual(GoalErrorCodes.InvalidResumePolicy, result.ErrorCode);
        var after = await db.GoalRuns.SingleAsync();
        Assert.AreEqual(before.ResumePolicy, after.ResumePolicy);
        Assert.AreEqual(before.AggregateVersion, after.AggregateVersion);
        Assert.AreEqual(0, await db.ConversationEvents.CountAsync(
            e => e.Type == GoalEventTypes.PolicyChanged));
    }

    [TestMethod]
    public async Task Policy_Rejects_Terminal_Goal_With_Explicit_Reason()
    {
        var (db, service) = await CreateAsync();
        await using var _ = db;

        await service.ExecuteAsync(SetRequest(), CancellationToken.None);
        var cancel = await service.ExecuteAsync(
            SimpleRequest(GoalCommandKind.Cancel), CancellationToken.None);
        Assert.IsTrue(cancel.Success);

        var result = await service.ExecuteAsync(
            PolicyRequest(GoalResumePolicies.AutoResumeOnRestart), CancellationToken.None);

        Assert.IsFalse(result.Success);
        Assert.AreEqual(GoalErrorCodes.InvalidState, result.ErrorCode);
        var goal = await db.GoalRuns.SingleAsync();
        Assert.AreEqual(GoalPhase.Cancelled, goal.Status);
        Assert.AreEqual(GoalResumePolicies.Paused, goal.ResumePolicy);
    }

    [TestMethod]
    public async Task Policy_Default_Remains_Paused_When_Never_Set_And_Visible_In_Status()
    {
        var (db, service) = await CreateAsync();
        await using var _ = db;

        await service.ExecuteAsync(SetRequest(), CancellationToken.None);
        var status = await service.ExecuteAsync(
            SimpleRequest(GoalCommandKind.Status), CancellationToken.None);

        Assert.IsTrue(status.Success);
        var goal = await db.GoalRuns.SingleAsync();
        Assert.AreEqual(GoalResumePolicies.Paused, goal.ResumePolicy);
        StringAssert.Contains(status.Message, "Resume policy: paused");
    }

    [TestMethod]
    public async Task Policy_Same_Value_Is_Idempotent_No_Event_No_Version_Bump()
    {
        var (db, service) = await CreateAsync();
        await using var _ = db;

        await service.ExecuteAsync(SetRequest(), CancellationToken.None);
        var before = await db.GoalRuns.SingleAsync();

        var result = await service.ExecuteAsync(
            PolicyRequest(GoalResumePolicies.Paused), CancellationToken.None);

        Assert.IsTrue(result.Success);
        var after = await db.GoalRuns.SingleAsync();
        Assert.AreEqual(before.AggregateVersion, after.AggregateVersion);
        Assert.AreEqual(0, await db.ConversationEvents.CountAsync(
            e => e.Type == GoalEventTypes.PolicyChanged));
    }

    // ── W3：/goal extend 额度耗尽人工出口 ──────────────────────

    private static GoalCommandRequest ExtendRequest(
        int? rounds = 8,
        string clientRequestId = "req-extend",
        string conversationId = "conv-1")
        => new("ws", conversationId, "agent-1", "admin", clientRequestId,
            new GoalCommand { Kind = GoalCommandKind.Extend, Rounds = rounds });

    /// <summary>按 GoalSettlementStore 预算耗尽分支的字段口径，直接把 Goal 置为 budget_exhausted。</summary>
    private static async Task<GoalRunEntity> SettleToBudgetExhaustedAsync(
        PlatformDbContext db, int maxIterations)
    {
        var goal = await db.GoalRuns.SingleAsync();
        goal.Status = GoalPhase.BudgetExhausted;
        goal.StatusReason = "accepted_iteration_budget_exhausted";
        goal.TerminalAtUtc = DateTimeOffset.UtcNow;
        goal.MaxIterations = maxIterations;
        goal.IterationsStarted = maxIterations;
        goal.IterationsSettled = maxIterations;
        goal.ActivationEpoch++;
        await db.SaveChangesAsync();
        return goal;
    }

    [TestMethod]
    public async Task Extend_Reactivates_BudgetExhausted_Goal_Raises_Budget_And_Enqueues_Continuation()
    {
        var (db, service) = await CreateAsync(continuationEnabled: true);
        await using var _ = db;

        await service.ExecuteAsync(SetRequest(rounds: 3), CancellationToken.None);
        var exhausted = await SettleToBudgetExhaustedAsync(db, maxIterations: 3);
        // TryMutateAsync 在同一 DbContext 内重载同一 tracked 实体：extend 后 exhausted 与
        // goal 是同一对象，提前捕获基准值，避免别名污染断言。
        var exhaustedEpoch = exhausted.ActivationEpoch;

        var result = await service.ExecuteAsync(ExtendRequest(rounds: 8), CancellationToken.None);

        Assert.IsTrue(result.Success);
        Assert.AreEqual(GoalPhase.Active, result.Snapshot!.Phase);
        Assert.AreEqual(11, result.Snapshot.MaxIterations);
        Assert.AreEqual(3, result.Snapshot.IterationsStarted);

        var goal = await db.GoalRuns.SingleAsync();
        Assert.AreEqual(GoalPhase.Active, goal.Status);
        Assert.AreEqual(11, goal.MaxIterations);
        Assert.IsNull(goal.TerminalAtUtc);
        Assert.IsNull(goal.BlockedCode);
        Assert.AreEqual(exhaustedEpoch + 1, goal.ActivationEpoch);

        // 审计事件：payload 记录 field/from/to/rounds/by（触发者）。
        var evt = await db.ConversationEvents.SingleAsync(
            e => e.Type == GoalEventTypes.BudgetExtended);
        StringAssert.Contains(evt.Payload, "max_iterations");
        StringAssert.Contains(evt.Payload, "\"from\":3");
        StringAssert.Contains(evt.Payload, "\"to\":11");
        StringAssert.Contains(evt.Payload, "\"by\":\"admin\"");

        // 同事务续行意图：新 epoch、下一迭代号。
        var outbox = await db.GoalOutbox.SingleAsync(o => o.ActivationEpoch == goal.ActivationEpoch);
        Assert.AreEqual(GoalOutboxValues.Pending, outbox.Status);
        Assert.AreEqual(goal.GoalRunId, outbox.GoalRunId);
    }

    [TestMethod]
    public async Task Extend_Without_Rounds_Fails_Closed_Without_Mutation()
    {
        var (db, service) = await CreateAsync();
        await using var _ = db;

        await service.ExecuteAsync(SetRequest(rounds: 3), CancellationToken.None);
        await SettleToBudgetExhaustedAsync(db, 3);
        var before = await db.GoalRuns.SingleAsync();

        var result = await service.ExecuteAsync(
            ExtendRequest(rounds: null, clientRequestId: "req-extend-norounds"),
            CancellationToken.None);

        Assert.IsFalse(result.Success);
        Assert.AreEqual(GoalErrorCodes.InvalidRounds, result.ErrorCode);
        var after = await db.GoalRuns.SingleAsync();
        Assert.AreEqual(GoalPhase.BudgetExhausted, after.Status);
        Assert.AreEqual(before.MaxIterations, after.MaxIterations);
        Assert.AreEqual(before.AggregateVersion, after.AggregateVersion);
        Assert.AreEqual(0, await db.ConversationEvents.CountAsync(
            e => e.Type == GoalEventTypes.BudgetExtended));
    }

    [TestMethod]
    public async Task Extend_Rejects_Non_BudgetExhausted_Phases_With_Accurate_State()
    {
        var (db, service) = await CreateAsync();
        await using var _ = db;

        // active
        await service.ExecuteAsync(SetRequest(rounds: 8, clientRequestId: "req-a"), CancellationToken.None);
        var active = await service.ExecuteAsync(
            ExtendRequest(clientRequestId: "req-ea"), CancellationToken.None);
        Assert.IsFalse(active.Success);
        Assert.AreEqual(GoalErrorCodes.InvalidState, active.ErrorCode);
        StringAssert.Contains(active.Message, "Active");

        // paused
        await service.ExecuteAsync(SimpleRequest(GoalCommandKind.Pause), CancellationToken.None);
        var paused = await service.ExecuteAsync(
            ExtendRequest(clientRequestId: "req-ep"), CancellationToken.None);
        Assert.IsFalse(paused.Success);
        Assert.AreEqual(GoalErrorCodes.InvalidState, paused.ErrorCode);
        StringAssert.Contains(paused.Message, "Paused");

        // cancelled（终态但非额度耗尽）
        await service.ExecuteAsync(SimpleRequest(GoalCommandKind.Cancel), CancellationToken.None);
        var cancelled = await service.ExecuteAsync(
            ExtendRequest(clientRequestId: "req-ec"), CancellationToken.None);
        Assert.IsFalse(cancelled.Success);
        Assert.AreEqual(GoalErrorCodes.InvalidState, cancelled.ErrorCode);
        StringAssert.Contains(cancelled.Message, "Cancelled");

        Assert.AreEqual(0, await db.ConversationEvents.CountAsync(
            e => e.Type == GoalEventTypes.BudgetExtended));
    }

    [TestMethod]
    public async Task Extend_FailClosed_When_Task_Binding_Already_Terminal()
    {
        var (db, service) = await CreateAsync(continuationEnabled: true);
        await using var _ = db;

        await service.ExecuteAsync(SetRequest(rounds: 3), CancellationToken.None);
        var exhausted = await SettleToBudgetExhaustedAsync(db, 3);

        // 结算口径：binding 置 terminal 并写 ReleasedAtUtc（GoalSettlementStore 预算耗尽分支）。
        db.TaskGoalBindings.Add(new TaskGoalBindingEntity
        {
            BindingId = "binding-1",
            WorkspaceId = "ws",
            TaskId = "task-1",
            GoalRunId = exhausted.GoalRunId,
            AgentInstanceId = "agent-1",
            Status = "terminal",
            ReleasedAtUtc = DateTimeOffset.UtcNow,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var result = await service.ExecuteAsync(ExtendRequest(), CancellationToken.None);

        Assert.IsFalse(result.Success);
        Assert.AreEqual(GoalErrorCodes.GoalBindingReleased, result.ErrorCode);
        var goal = await db.GoalRuns.SingleAsync();
        Assert.AreEqual(GoalPhase.BudgetExhausted, goal.Status);
        Assert.AreEqual(3, goal.MaxIterations);
        Assert.AreEqual(0, await db.ConversationEvents.CountAsync(
            e => e.Type == GoalEventTypes.BudgetExtended));
        Assert.AreEqual(0, await db.GoalOutbox.CountAsync(
            o => o.ActivationEpoch == goal.ActivationEpoch));
    }

    [TestMethod]
    public async Task Extend_FailClosed_When_Bound_Task_Missing()
    {
        var (db, service) = await CreateAsync();
        await using var _ = db;

        await service.ExecuteAsync(SetRequest(rounds: 3), CancellationToken.None);
        var exhausted = await SettleToBudgetExhaustedAsync(db, 3);

        // binding 仍 active 但任务行已消失（回退/清理）—— 同样无法推进。
        db.TaskGoalBindings.Add(new TaskGoalBindingEntity
        {
            BindingId = "binding-2",
            WorkspaceId = "ws",
            TaskId = "task-gone",
            GoalRunId = exhausted.GoalRunId,
            AgentInstanceId = "agent-1",
            Status = "active",
            CreatedAtUtc = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var result = await service.ExecuteAsync(ExtendRequest(), CancellationToken.None);

        Assert.IsFalse(result.Success);
        Assert.AreEqual(GoalErrorCodes.GoalBindingReleased, result.ErrorCode);
        Assert.AreEqual(GoalPhase.BudgetExhausted, (await db.GoalRuns.SingleAsync()).Status);
    }

    [TestMethod]
    public async Task Extend_Without_Any_Goal_Returns_GoalNotFound()
    {
        var (db, service) = await CreateAsync();
        await using var _ = db;

        var result = await service.ExecuteAsync(ExtendRequest(), CancellationToken.None);

        Assert.IsFalse(result.Success);
        Assert.AreEqual(GoalErrorCodes.GoalNotFound, result.ErrorCode);
    }
}
