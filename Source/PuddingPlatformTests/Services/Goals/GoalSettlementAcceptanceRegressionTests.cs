using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PuddingCode.Goals;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Tasks;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services.Goals;

namespace PuddingPlatformTests.Services.Goals;

/// <summary>
/// G92-0 真实 Store 回归：GoalSettlementStore 在真实 SQLite 上结算 canonical Turn 终态。
/// 断言「回合结束不等于步骤或目标完成」：只有当前单元的必需条件通过验证，才允许推进或完成；
/// 重复结算、旧 epoch 结算不得改变计划或续行。
/// </summary>
[TestClass]
public sealed class GoalSettlementAcceptanceRegressionTests
{
    private const string ConversationId = "conv-1";
    private const string GoalId = "goal-1";
    private const string IterationId = "gi-1";
    private const string TurnId = "turn-1";
    private const string PlanId = "plan-1";
    private const string TaskId = "task-1";
    private const string ReservationId = "res-1";
    private const string AgentId = "agent-1";
    private const string WorkspaceId = "ws";
    private const long TerminalSequence = 7;

    private const string RootNodeId = "node-root";
    private const string CurrentNodeId = "node-current";
    private const string NextNodeId = "node-next";

    private sealed class NoopSignal : ICommittedEventSignal
    {
        public ValueTask WaitForChangeAsync(string conversationId, long knownHead, CancellationToken ct)
            => ValueTask.FromCanceled(ct);

        public void Signal(string conversationId, long committedSequence)
        {
        }
    }

    /// <summary>共享单个内存 SQLite 连接的 Context 工厂；Store 每次调用创建新 Context，与生产一致。</summary>
    private sealed class SharedConnectionFactory(SqliteConnection connection) : IDbContextFactory<PlatformDbContext>
    {
        public PlatformDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<PlatformDbContext>()
                .UseSqlite(connection)
                .Options;
            return new PlatformDbContext(options);
        }
    }

    private static async Task<(PlatformDbContext Db, GoalSettlementStore Store, SqliteConnection Connection)> CreateAsync(
        GoalRunOptions? goalOptions = null)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite(connection)
            .Options;
        var db = new PlatformDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var store = goalOptions is null
            ? new GoalSettlementStore(
                new SharedConnectionFactory(connection),
                new NoopSignal(),
                new GoalOutboxSignal())
            : new GoalSettlementStore(
                new SharedConnectionFactory(connection),
                new NoopSignal(),
                new GoalOutboxSignal(),
                taskBoundOptions: null,
                goalOptions: Microsoft.Extensions.Options.Options.Create(goalOptions));
        return (db, store, connection);
    }

    /// <summary>
    /// 播种一个 Task-bound Goal：1 个计划、根节点、1 个 running 当前单元，可选 1 个后续单元。
    /// goalActivationEpoch 用于构造旧 epoch 结算。
    /// </summary>
    private static async Task SeedBoundPlanAsync(
        PlatformDbContext db,
        bool withNextUnit,
        WorkspaceTaskStatus taskStatus = WorkspaceTaskStatus.InProgress,
        int goalActivationEpoch = 1)
    {
        var now = DateTimeOffset.UtcNow;

        db.GoalRuns.Add(new GoalRunEntity
        {
            GoalRunId = GoalId,
            WorkspaceId = WorkspaceId,
            CurrentConversationId = ConversationId,
            AgentInstanceId = AgentId,
            Objective = "修复全部失败测试并证明普通开发任务可继续执行",
            Status = GoalPhase.Active,
            ActivationEpoch = goalActivationEpoch,
            MaxIterations = 256,
            IterationsStarted = 1,
            SourceCommandId = "cmd-1",
        });

        db.ConversationTurns.Add(new ConversationTurnEntity
        {
            ConversationId = ConversationId,
            TurnId = TurnId,
            WorkspaceId = WorkspaceId,
            Status = "completed",
            AcceptedSequence = TerminalSequence - 1,
            TerminalSequence = TerminalSequence,
            TerminalKind = "completed",
            CreatedAt = 0,
            CompletedAt = 1,
        });

        db.GoalIterations.Add(new GoalIterationEntity
        {
            GoalIterationId = IterationId,
            GoalRunId = GoalId,
            ActivationEpoch = 1,
            IterationNo = 1,
            Status = "accepted",
            TurnId = TurnId,
            RunId = "run-1",
            AcceptedSequence = TerminalSequence - 1,
            StartedAtUtc = now.AddMinutes(-1),
        });

        db.WorkspaceTasks.Add(new WorkspaceTaskEntity
        {
            WorkspaceId = WorkspaceId,
            TaskId = TaskId,
            Version = 3,
            Status = taskStatus,
            Title = "Goal 结算回归任务",
        });

        db.TaskPlanRuns.Add(new TaskPlanRunEntity
        {
            PlanId = PlanId,
            WorkspaceId = WorkspaceId,
            WorkspaceTaskId = TaskId,
            WorkspaceTaskVersion = 3,
            PlanVersion = 1,
            PlanKind = "delegation",
            PlanFingerprint = "fp-1",
            RootSessionId = "session-1",
            LeaderAgentId = AgentId,
            Objective = "修复全部失败测试并证明普通开发任务可继续执行",
            Status = TaskPlanStatuses.Active.ToString(),
        });

        db.TaskNodes.Add(new TaskNodeEntity
        {
            TaskNodeId = RootNodeId,
            PlanId = PlanId,
            Depth = 0,
            SequenceNo = 0,
            Status = TaskNodeStatuses.Running.ToString(),
            Objective = "修复全部失败测试并证明普通开发任务可继续执行",
        });

        db.TaskNodes.Add(new TaskNodeEntity
        {
            TaskNodeId = CurrentNodeId,
            PlanId = PlanId,
            ParentTaskNodeId = RootNodeId,
            Depth = 1,
            SequenceNo = 1,
            Status = TaskNodeStatuses.Running.ToString(),
            Objective = "修复自动审计并证明普通开发任务能继续执行",
        });

        if (withNextUnit)
        {
            db.TaskNodes.Add(new TaskNodeEntity
            {
                TaskNodeId = NextNodeId,
                PlanId = PlanId,
                ParentTaskNodeId = RootNodeId,
                Depth = 1,
                SequenceNo = 2,
                Status = TaskNodeStatuses.Planned.ToString(),
                Objective = "证明长程目标可以持续推进",
            });
        }

        db.AgentExecutionReservations.Add(new AgentExecutionReservationEntity
        {
            ReservationId = ReservationId,
            WorkspaceId = WorkspaceId,
            AgentId = AgentId,
            TaskId = TaskId,
            GoalRunId = GoalId,
            OwnerId = "owner-1",
            Status = "active",
            LeaseUntilUtc = now.AddHours(2),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        });

        db.TaskGoalBindings.Add(new TaskGoalBindingEntity
        {
            BindingId = "binding-1",
            WorkspaceId = WorkspaceId,
            TaskId = TaskId,
            GoalRunId = GoalId,
            AgentInstanceId = AgentId,
            ReservationId = ReservationId,
            ReservationFencingToken = 1,
            TaskPlanId = PlanId,
            PlanFingerprint = "fp-1",
            Status = "active",
            CreatedAtUtc = now,
        });

        await db.SaveChangesAsync();
    }

    private static GoalSettlementCandidate Candidate(
        string iterationId = IterationId,
        string turnId = TurnId,
        long terminalSequence = TerminalSequence,
        int iterationNo = 1) => new()
    {
        GoalIterationId = iterationId,
        GoalRunId = GoalId,
        WorkspaceId = WorkspaceId,
        ConversationId = ConversationId,
        AgentInstanceId = AgentId,
        ActivationEpoch = 1,
        AggregateVersion = 0,
        IterationNo = iterationNo,
        MaxIterations = 256,
        IterationsStarted = 1,
        Objective = "修复全部失败测试并证明普通开发任务可继续执行",
        ObjectiveVersion = 1,
        TurnId = turnId,
        TerminalKind = "completed",
        TerminalSequence = terminalSequence,
        EvidenceRefs = ["turn:turn-1:terminal:7"],
        ErrorCode = null,
        ErrorMessage = null,
        TaskId = TaskId,
        TaskVersion = 3,
        TaskStatus = WorkspaceTaskStatus.InProgress.ToString(),
        TaskAcceptanceCriteria = "全部失败测试通过",
        HasPendingExecutionFacts = false,
        EvidenceComplete = true,
        RunId = "run-1",
        StartedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
        ActiveElapsedMs = 1000,
        LlmRounds = 2,
        ToolCalls = 3,
        InputTokens = 100,
        OutputTokens = 20,
    };

    private static GoalCriterion Criterion(string id) => new()
    {
        Id = id,
        Revision = 1,
        Requirement = $"criterion {id}",
        Required = true,
        Kind = GoalVerificationSpecKinds.Test,
        DefinitionRef = "checks/regression.md",
        DefinitionHash = "hash-1",
    };

    private static GoalCriterionResult Passed(string id) => new()
    {
        CriterionId = id,
        CriterionRevision = 1,
        Status = GoalCriterionResultStatuses.Passed,
        EvidenceRefs = ["artifact:test-report"],
    };

    /// <summary>一轮 completed 但只交付计划/部分代码：没有任何条件结果。</summary>
    private static GoalVerificationDecision PlannedOnlyDecision(GoalVerificationVerdict verdict)
        => new()
        {
            Verdict = verdict,
            Reason = "Turn completed with a plan only.",
            EvidenceRefs = ["turn:turn-1:terminal:7"],
            Criteria = [Criterion("build"), Criterion("test")],
            CriterionResults = [],
        };

    private static GoalVerificationDecision VerifiedDecision(GoalVerificationVerdict verdict)
        => new()
        {
            Verdict = verdict,
            Reason = "Required criteria passed.",
            EvidenceRefs = ["turn:turn-1:terminal:7"],
            Criteria = [Criterion("build"), Criterion("test")],
            CriterionResults = [Passed("build"), Passed("test")],
        };

    [TestMethod]
    public async Task CompletedTurn_WithoutVerifiedCriteria_DoesNotAdvanceWorkUnit()
    {
        var (db, store, connection) = await CreateAsync();
        await using var _ = db;
        await using var __ = connection;
        await SeedBoundPlanAsync(db, withNextUnit: true);

        var applied = await store.ApplyAsync(Candidate(), PlannedOnlyDecision(GoalVerificationVerdict.Complete), CancellationToken.None);

        Assert.IsTrue(applied);

        var current = await db.TaskNodes.SingleAsync(node => node.TaskNodeId == CurrentNodeId);
        var plan = await db.TaskPlanRuns.SingleAsync(item => item.PlanId == PlanId);
        var root = await db.TaskNodes.SingleAsync(node => node.TaskNodeId == RootNodeId);

        // 验收标准 1：回合结束不得把当前 WorkUnit 标完成，也不得标 Plan/Goal 完成。
        Assert.AreNotEqual(TaskNodeStatuses.Completed.ToString(), current.Status);
        Assert.AreNotEqual(TaskPlanStatuses.Completed.ToString(), plan.Status);
        Assert.AreNotEqual(TaskNodeStatuses.Completed.ToString(), root.Status);
        Assert.IsNull(current.CompletedAt);
        Assert.IsNull(plan.CompletedAt);
    }

    [TestMethod]
    public async Task RepeatedSettlement_DoesNotRecontinue()
    {
        var (db, store, connection) = await CreateAsync();
        await using var _ = db;
        await using var __ = connection;
        await SeedBoundPlanAsync(db, withNextUnit: true);

        var first = await store.ApplyAsync(Candidate(), PlannedOnlyDecision(GoalVerificationVerdict.Continue), CancellationToken.None);
        var second = await store.ApplyAsync(Candidate(), PlannedOnlyDecision(GoalVerificationVerdict.Continue), CancellationToken.None);

        // 验收标准 3：同一 iteration 只能结算一次，重复结算必须被拒绝。
        Assert.IsTrue(first);
        Assert.IsFalse(second);

        var current = await db.TaskNodes.SingleAsync(node => node.TaskNodeId == CurrentNodeId);
        Assert.AreNotEqual(TaskNodeStatuses.Completed.ToString(), current.Status);
    }

    [TestMethod]
    public async Task StaleEpochSettlement_DoesNotAdvanceOrCompletePlan()
    {
        var (db, store, connection) = await CreateAsync();
        await using var _ = db;
        await using var __ = connection;
        // Goal 已推进到 epoch 2，本 iteration 属于 epoch 1。
        await SeedBoundPlanAsync(db, withNextUnit: false, goalActivationEpoch: 2);

        var applied = await store.ApplyAsync(Candidate(), VerifiedDecision(GoalVerificationVerdict.Complete), CancellationToken.None);

        Assert.IsTrue(applied);

        var current = await db.TaskNodes.SingleAsync(node => node.TaskNodeId == CurrentNodeId);
        var plan = await db.TaskPlanRuns.SingleAsync(item => item.PlanId == PlanId);

        // 验收标准 3：旧 epoch 的结果可以入账，但不得写当前状态。
        Assert.AreNotEqual(TaskNodeStatuses.Completed.ToString(), current.Status);
        Assert.AreNotEqual(TaskPlanStatuses.Completed.ToString(), plan.Status);
    }

    [TestMethod]
    public async Task LastUnitWithoutRequiredEvidence_DoesNotCompletePlan()
    {
        var (db, store, connection) = await CreateAsync();
        await using var _ = db;
        await using var __ = connection;
        // 最后一单元，且 Task 已是 Completed —— 旧逻辑会直接完成 Plan/Root。
        await SeedBoundPlanAsync(db, withNextUnit: false, taskStatus: WorkspaceTaskStatus.Completed);

        var applied = await store.ApplyAsync(Candidate(), PlannedOnlyDecision(GoalVerificationVerdict.Complete), CancellationToken.None);

        Assert.IsTrue(applied);

        var current = await db.TaskNodes.SingleAsync(node => node.TaskNodeId == CurrentNodeId);
        var plan = await db.TaskPlanRuns.SingleAsync(item => item.PlanId == PlanId);
        var root = await db.TaskNodes.SingleAsync(node => node.TaskNodeId == RootNodeId);
        var goal = await db.GoalRuns.SingleAsync(item => item.GoalRunId == GoalId);

        // 验收标准 2：最后单元缺必要证据时不得标 Plan/Goal Completed。
        Assert.AreNotEqual(TaskNodeStatuses.Completed.ToString(), current.Status);
        Assert.AreNotEqual(TaskPlanStatuses.Completed.ToString(), plan.Status);
        Assert.AreNotEqual(TaskNodeStatuses.Completed.ToString(), root.Status);
        Assert.AreEqual(GoalPhase.Active, goal.Status);
    }

    [TestMethod]
    public async Task VerifiedLastUnit_CompletesPlanAndRoot()
    {
        var (db, store, connection) = await CreateAsync();
        await using var _ = db;
        await using var __ = connection;
        await SeedBoundPlanAsync(db, withNextUnit: false, taskStatus: WorkspaceTaskStatus.Completed);

        var applied = await store.ApplyAsync(Candidate(), VerifiedDecision(GoalVerificationVerdict.Complete), CancellationToken.None);

        Assert.IsTrue(applied);

        // 播种用的 DbContext 仍跟踪播种时的实体，直接查询会读到旧值（stale change tracker）；
        // 断言必须读服务端已提交的真实状态。（真实运行复现：写入已发生但断言读到播种值 Running）
        db.ChangeTracker.Clear();
        var current = await db.TaskNodes.SingleAsync(node => node.TaskNodeId == CurrentNodeId);
        var plan = await db.TaskPlanRuns.SingleAsync(item => item.PlanId == PlanId);
        var root = await db.TaskNodes.SingleAsync(node => node.TaskNodeId == RootNodeId);

        // 只有必需条件全部通过，最后一单元才允许完成整个计划。
        Assert.AreEqual(TaskNodeStatuses.Completed.ToString(), current.Status);
        Assert.AreEqual(TaskPlanStatuses.Completed.ToString(), plan.Status);
        Assert.AreEqual(TaskNodeStatuses.Completed.ToString(), root.Status);
    }

    [TestMethod]
    public async Task VerifiedUnit_WithNextUnit_AdvancesWithoutCompletingPlan()
    {
        var (db, store, connection) = await CreateAsync();
        await using var _ = db;
        await using var __ = connection;
        await SeedBoundPlanAsync(db, withNextUnit: true);

        var applied = await store.ApplyAsync(Candidate(), VerifiedDecision(GoalVerificationVerdict.Continue), CancellationToken.None);

        Assert.IsTrue(applied);

        // 同上：清空变更跟踪后从存储重读，避免断言读到播种时的旧值。
        db.ChangeTracker.Clear();
        var current = await db.TaskNodes.SingleAsync(node => node.TaskNodeId == CurrentNodeId);
        var next = await db.TaskNodes.SingleAsync(node => node.TaskNodeId == NextNodeId);
        var plan = await db.TaskPlanRuns.SingleAsync(item => item.PlanId == PlanId);

        // 单元已验收：当前单元完成，但整体目标未完成，计划不得标 Completed。
        Assert.AreEqual(TaskNodeStatuses.Completed.ToString(), current.Status);
        Assert.AreNotEqual(TaskNodeStatuses.Completed.ToString(), next.Status);
        Assert.AreNotEqual(TaskPlanStatuses.Completed.ToString(), plan.Status);
    }

    [TestMethod]
    public async Task TwoCompletedRounds_StayInTheSameWorkUnit()
    {
        var (db, store, connection) = await CreateAsync();
        await using var _ = db;
        await using var __ = connection;
        await SeedBoundPlanAsync(db, withNextUnit: true);

        Assert.IsTrue(await store.ApplyAsync(
            Candidate(),
            PlannedOnlyDecision(GoalVerificationVerdict.Continue),
            CancellationToken.None));

        // 第二轮：新的 iteration 与 canonical Turn，仍是同一个 WorkUnit。
        db.ConversationTurns.Add(new ConversationTurnEntity
        {
            ConversationId = ConversationId,
            TurnId = "turn-2",
            WorkspaceId = WorkspaceId,
            Status = "completed",
            AcceptedSequence = 8,
            TerminalSequence = 9,
            TerminalKind = "completed",
            CreatedAt = 2,
            CompletedAt = 3,
        });
        db.GoalIterations.Add(new GoalIterationEntity
        {
            GoalIterationId = "gi-2",
            GoalRunId = GoalId,
            ActivationEpoch = 1,
            IterationNo = 2,
            Status = "accepted",
            TurnId = "turn-2",
            RunId = "run-2",
            AcceptedSequence = 8,
            StartedAtUtc = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        Assert.IsTrue(await store.ApplyAsync(
            Candidate("gi-2", "turn-2", 9, 2),
            PlannedOnlyDecision(GoalVerificationVerdict.Continue),
            CancellationToken.None));

        var current = await db.TaskNodes.SingleAsync(node => node.TaskNodeId == CurrentNodeId);
        var next = await db.TaskNodes.SingleAsync(node => node.TaskNodeId == NextNodeId);
        var runningUnits = await db.TaskNodes
            .Where(node => node.PlanId == PlanId
                           && node.Depth == 1
                           && node.Status == TaskNodeStatuses.Running.ToString())
            .ToListAsync();

        // 验收标准 1：两轮 completed 都留在同一单元内工作，没有推进到下一单元。
        Assert.AreNotEqual(TaskNodeStatuses.Completed.ToString(), current.Status);
        Assert.AreEqual(1, runningUnits.Count);
        Assert.AreEqual(CurrentNodeId, runningUnits[0].TaskNodeId);
        Assert.AreNotEqual(TaskNodeStatuses.Running.ToString(), next.Status);
    }

    // ── P0-2（ADR-092 §7）：无进展/同阻塞熔断接线 ─────────────────────────────────

    private static GoalCriterionResult Failed(string id) => new()
    {
        CriterionId = id,
        CriterionRevision = 1,
        Status = GoalCriterionResultStatuses.Failed,
        EvidenceRefs = ["artifact:test-report"],
    };

    /// <summary>criterion_failed 轮：completed Turn 但必需条件失败（repair），携带进度指纹。</summary>
    private static GoalVerificationDecision FailedDecision(string? progressFingerprint) => new()
    {
        Verdict = GoalVerificationVerdict.Continue,
        Reason = "Turn completed but required criteria failed.",
        EvidenceRefs = ["turn:turn-1:terminal:7"],
        Criteria = [Criterion("build"), Criterion("test")],
        CriterionResults = [Failed("build")],
        ProgressFingerprint = progressFingerprint,
    };

    /// <summary>合法等待轮：dependency_wait（等待族白名单），不得参与熔断计数。</summary>
    private static GoalVerificationDecision WaitDecision(string? progressFingerprint) => new()
    {
        Verdict = GoalVerificationVerdict.Blocked,
        Reason = "Waiting for dependency.",
        EvidenceRefs = ["turn:turn-1:terminal:7"],
        BlockerCode = "dependency_wait",
        BlockerMessage = "Dependency not ready yet.",
        ProgressFingerprint = progressFingerprint,
    };

    private static async Task SeedRoundAsync(PlatformDbContext db, int round)
    {
        var terminalSequence = TerminalSequence + round - 1;
        db.ConversationTurns.Add(new ConversationTurnEntity
        {
            ConversationId = ConversationId,
            TurnId = $"turn-{round}",
            WorkspaceId = WorkspaceId,
            Status = "completed",
            AcceptedSequence = terminalSequence - 1,
            TerminalSequence = terminalSequence,
            TerminalKind = "completed",
            CreatedAt = round * 2,
            CompletedAt = round * 2 + 1,
        });
        db.GoalIterations.Add(new GoalIterationEntity
        {
            GoalIterationId = $"gi-{round}",
            GoalRunId = GoalId,
            ActivationEpoch = 1,
            IterationNo = round,
            Status = "accepted",
            TurnId = $"turn-{round}",
            RunId = $"run-{round}",
            AcceptedSequence = terminalSequence - 1,
            StartedAtUtc = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>连续结算 1..rounds 轮（每轮播种新的 iteration 与 canonical Turn）。</summary>
    private static async Task SettleRoundsAsync(
        GoalSettlementStore store,
        PlatformDbContext db,
        int rounds,
        Func<int, GoalVerificationDecision> decisionForRound)
    {
        for (var round = 1; round <= rounds; round++)
        {
            if (round > 1)
                await SeedRoundAsync(db, round);
            var terminalSequence = TerminalSequence + round - 1;
            Assert.IsTrue(await store.ApplyAsync(
                Candidate($"gi-{round}", $"turn-{round}", terminalSequence, round),
                decisionForRound(round),
                CancellationToken.None));
        }
    }

    [TestMethod]
    public async Task SameFingerprint_FourRounds_FourthSettlementIsNotRepair()
    {
        var (db, store, connection) = await CreateAsync();
        await using var _ = db;
        await using var __ = connection;
        await SeedBoundPlanAsync(db, withNextUnit: false);

        await SettleRoundsAsync(store, db, 4, _ => FailedDecision("fp-stuck"));

        db.ChangeTracker.Clear();
        var goal = await db.GoalRuns.SingleAsync(item => item.GoalRunId == GoalId);
        var task = await db.WorkspaceTasks.SingleAsync(item => item.TaskId == TaskId);

        // 第 4 轮：指纹未变化连续 3 次 → 熔断，不再返回 repair；无可改选 ready 单元 ⇒
        // 转 needs_user：task-bound 尝试终结为 Failed（可审计），Task 落 Blocked 可人工恢复。
        Assert.AreEqual(3, goal.ConsecutiveNoProgress);
        Assert.AreEqual(3, goal.ConsecutiveSameBlocker);
        Assert.AreEqual("no_progress_circuit_open", goal.BlockedCode);
        Assert.AreEqual(GoalPhase.Failed, goal.Status);
        Assert.AreEqual(WorkspaceTaskStatus.Blocked, task.Status);
        Assert.AreEqual("no_progress_circuit_open", task.BlockerKind);
        Assert.IsTrue(await db.ConversationEvents.AnyAsync(item =>
            item.ConversationId == ConversationId
            && item.Type == GoalEventTypes.CircuitOpened));
        Assert.IsTrue(await db.ConversationEvents.AnyAsync(item =>
            item.ConversationId == ConversationId
            && item.Type == GoalEventTypes.ProgressRecorded));
    }

    [TestMethod]
    public async Task SameFingerprint_WithReadyAlternativeUnit_ReplansOnceAndStaysActive()
    {
        var (db, store, connection) = await CreateAsync();
        await using var _ = db;
        await using var __ = connection;
        await SeedBoundPlanAsync(db, withNextUnit: true);

        await SettleRoundsAsync(store, db, 4, _ => FailedDecision("fp-stuck"));

        db.ChangeTracker.Clear();
        var goal = await db.GoalRuns.SingleAsync(item => item.GoalRunId == GoalId);
        var plan = await db.TaskPlanRuns.SingleAsync(item => item.PlanId == PlanId);
        var current = await db.TaskNodes.SingleAsync(node => node.TaskNodeId == CurrentNodeId);
        var binding = await db.TaskGoalBindings.SingleAsync(item => item.GoalRunId == GoalId);

        // 一次性 Replan：提升 PlanVersion、退回卡死单元（Running→Planned），
        // 本结算按 typed wait 收口（不是 repair，也不是静默终态），Goal 保持 Active 可续行。
        Assert.AreEqual(3, goal.ConsecutiveNoProgress);
        Assert.AreEqual(GoalPhase.Active, goal.Status);
        Assert.AreEqual(2, plan.PlanVersion);
        Assert.AreEqual(TaskNodeStatuses.Planned.ToString(), current.Status);
        Assert.AreEqual("no_progress_circuit_open", goal.BlockedCode);
        Assert.IsTrue(goal.BlockedMessage!.Contains("replan"));
        Assert.AreEqual("active", binding.Status);
        Assert.IsTrue(await db.ConversationEvents.AnyAsync(item =>
            item.Type == GoalEventTypes.CircuitOpened));
    }

    [TestMethod]
    public async Task WaitBlocker_TenRounds_NeverTripsBreaker()
    {
        var (db, store, connection) = await CreateAsync();
        await using var _ = db;
        await using var __ = connection;
        await SeedBoundPlanAsync(db, withNextUnit: false);

        await SettleRoundsAsync(store, db, 10, _ => WaitDecision("fp-wait"));

        db.ChangeTracker.Clear();
        var goal = await db.GoalRuns.SingleAsync(item => item.GoalRunId == GoalId);
        var current = await db.TaskNodes.SingleAsync(node => node.TaskNodeId == CurrentNodeId);

        // 合法等待（等待族白名单）整轮跳过计数：三个连续计数器保持 0，不触发熔断。
        Assert.AreEqual(0, goal.ConsecutiveNoProgress);
        Assert.AreEqual(0, goal.ConsecutiveSameBlocker);
        Assert.AreEqual(0, goal.ConsecutiveInfraFailures);
        Assert.AreEqual(GoalPhase.Active, goal.Status);
        Assert.AreEqual("dependency_wait", goal.BlockedCode);
        Assert.AreEqual(TaskNodeStatuses.Running.ToString(), current.Status);
        Assert.IsNull(goal.LastProgressFingerprint);
        Assert.IsFalse(await db.ConversationEvents.AnyAsync(item =>
            item.Type == GoalEventTypes.CircuitOpened));
    }

    [TestMethod]
    public async Task FingerprintChange_ResetsNoProgressCounter()
    {
        var (db, store, connection) = await CreateAsync();
        await using var _ = db;
        await using var __ = connection;
        await SeedBoundPlanAsync(db, withNextUnit: false);

        await SettleRoundsAsync(store, db, 3, round => FailedDecision(
            round <= 2 ? "fp-a" : "fp-b"));

        db.ChangeTracker.Clear();
        var goal = await db.GoalRuns.SingleAsync(item => item.GoalRunId == GoalId);

        // 第 3 轮指纹变化 ⇒ ConsecutiveNoProgress 归零并记录新指纹。
        Assert.AreEqual(0, goal.ConsecutiveNoProgress);
        Assert.AreEqual("fp-b", goal.LastProgressFingerprint);
        Assert.AreEqual(GoalPhase.Active, goal.Status);
    }

    [TestMethod]
    public async Task InfraFailures_ConsecutiveThree_TripsBreakerToNeedsUser()
    {
        var (db, store, connection) = await CreateAsync();
        await using var _ = db;
        await using var __ = connection;
        await SeedBoundPlanAsync(db, withNextUnit: false);

        // 预先释放 reservation ⇒ 每轮结算被 reservation_fence_lost 门禁拦下（基础设施类失败）。
        var reservation = await db.AgentExecutionReservations.SingleAsync(
            item => item.ReservationId == ReservationId);
        reservation.Status = "released";
        await db.SaveChangesAsync();

        await SettleRoundsAsync(store, db, 3, _ => FailedDecision("fp-infra"));

        db.ChangeTracker.Clear();
        var goal = await db.GoalRuns.SingleAsync(item => item.GoalRunId == GoalId);

        // 基础设施失败连续 3 次 → 熔断转 needs_user；BlockedDecision 无指纹 ⇒ 指纹轴不受影响。
        Assert.AreEqual(3, goal.ConsecutiveInfraFailures);
        Assert.AreEqual(0, goal.ConsecutiveNoProgress);
        Assert.AreEqual("no_progress_circuit_open", goal.BlockedCode);
        Assert.AreEqual(GoalPhase.Failed, goal.Status);
    }

    [TestMethod]
    public async Task BreakerThreshold_IsConfigurableAndValidated()
    {
        // ① 阈值=2：第 3 轮即熔断（默认阈值 3 时第 3 轮仍是 repair）。
        var (dbFast, storeFast, connectionFast) = await CreateAsync(
            new GoalRunOptions { NoProgressBreakerThreshold = 2 });
        await using var _1 = dbFast;
        await using var _2 = connectionFast;
        await SeedBoundPlanAsync(dbFast, withNextUnit: false);
        await SettleRoundsAsync(storeFast, dbFast, 3, _ => FailedDecision("fp-fast"));

        dbFast.ChangeTracker.Clear();
        var fastGoal = await dbFast.GoalRuns.SingleAsync(item => item.GoalRunId == GoalId);
        Assert.AreEqual(2, fastGoal.ConsecutiveNoProgress);
        Assert.AreEqual("no_progress_circuit_open", fastGoal.BlockedCode);
        Assert.AreEqual(GoalPhase.Failed, fastGoal.Status);

        // ② 对照组：默认阈值 3，同样 3 轮不熔断。
        var (dbSlow, storeSlow, connectionSlow) = await CreateAsync();
        await using var _3 = dbSlow;
        await using var _4 = connectionSlow;
        await SeedBoundPlanAsync(dbSlow, withNextUnit: false);
        await SettleRoundsAsync(storeSlow, dbSlow, 3, _ => FailedDecision("fp-slow"));

        dbSlow.ChangeTracker.Clear();
        var slowGoal = await dbSlow.GoalRuns.SingleAsync(item => item.GoalRunId == GoalId);
        Assert.AreEqual(2, slowGoal.ConsecutiveNoProgress);
        Assert.AreEqual(GoalPhase.Active, slowGoal.Status);
        Assert.AreNotEqual("no_progress_circuit_open", slowGoal.BlockedCode);

        // ③ Validate 边界（1..16）。
        var zero = GoalRunOptions.Validate(new GoalRunOptions { NoProgressBreakerThreshold = 0 });
        Assert.IsTrue(zero.Any(message => message.Contains("NoProgressBreakerThreshold")));
        var over = GoalRunOptions.Validate(new GoalRunOptions { NoProgressBreakerThreshold = 17 });
        Assert.IsTrue(over.Any(message => message.Contains("NoProgressBreakerThreshold")));
        var valid = GoalRunOptions.Validate(new GoalRunOptions { NoProgressBreakerThreshold = 3 });
        Assert.IsFalse(valid.Any(message => message.Contains("NoProgressBreakerThreshold")));
    }

    // ── P0-3（ADR-092 §7.6）：不可恢复处置的连续同因降级门槛 ─────────────────────

    /// <summary>不可恢复阻塞轮：Blocked + 白名单不可恢复码（task_plan_state_invalid，参与降级门槛）。</summary>
    private static GoalVerificationDecision UnrecoverableDecision(string code) => new()
    {
        Verdict = GoalVerificationVerdict.Blocked,
        Reason = $"Unrecoverable blocker: {code}.",
        EvidenceRefs = ["turn:turn-1:terminal:7"],
        BlockerCode = code,
        BlockerMessage = "Plan state is unrecoverable.",
    };

    /// <summary>Unsafe 裁决轮：安全例外，必须立即终止，不得降级。</summary>
    private static GoalVerificationDecision UnsafeDecision() => new()
    {
        Verdict = GoalVerificationVerdict.Unsafe,
        Reason = "Verifier flagged unsafe execution conditions.",
        EvidenceRefs = ["turn:turn-1:terminal:7"],
        BlockerCode = "unsafe",
        BlockerMessage = "Unsafe execution blocked.",
    };

    [TestMethod]
    public async Task Unrecoverable_SingleOccurrence_DoesNotTerminate()
    {
        var (db, store, connection) = await CreateAsync();
        await using var _ = db;
        await using var __ = connection;
        await SeedBoundPlanAsync(db, withNextUnit: false);

        await SettleRoundsAsync(store, db, 1, _ => UnrecoverableDecision("task_plan_state_invalid"));

        db.ChangeTracker.Clear();
        var goal = await db.GoalRuns.SingleAsync(item => item.GoalRunId == GoalId);
        var task = await db.WorkspaceTasks.SingleAsync(item => item.TaskId == TaskId);
        var binding = await db.TaskGoalBindings.SingleAsync(item => item.GoalRunId == GoalId);
        var plan = await db.TaskPlanRuns.SingleAsync(item => item.PlanId == PlanId);

        // 第 1 次不可恢复：不得终态化 —— Goal 保持 Active、Task 不 Blocked、binding 保持、计划不 Failed；
        // 降级原因与计数（1/3）写入 StatusReason，ProgressRecorded 事件携带降级审计字段。
        Assert.AreEqual(GoalPhase.Active, goal.Status);
        Assert.AreNotEqual(WorkspaceTaskStatus.Blocked, task.Status);
        Assert.AreEqual("active", binding.Status);
        Assert.AreNotEqual(TaskPlanStatuses.Failed.ToString(), plan.Status);
        Assert.AreEqual("task_plan_state_invalid", goal.BlockedCode);
        Assert.IsTrue(goal.StatusReason!.Contains("1/3"));
        Assert.IsTrue(await db.ConversationEvents.AnyAsync(item =>
            item.ConversationId == ConversationId
            && item.Type == GoalEventTypes.ProgressRecorded));
    }

    [TestMethod]
    public async Task Unrecoverable_ThreeConsecutiveSameCode_Terminates()
    {
        var (db, store, connection) = await CreateAsync();
        await using var _ = db;
        await using var __ = connection;
        await SeedBoundPlanAsync(db, withNextUnit: false);

        await SettleRoundsAsync(store, db, 3, _ => UnrecoverableDecision("task_plan_state_invalid"));

        db.ChangeTracker.Clear();
        var goal = await db.GoalRuns.SingleAsync(item => item.GoalRunId == GoalId);
        var task = await db.WorkspaceTasks.SingleAsync(item => item.TaskId == TaskId);
        var binding = await db.TaskGoalBindings.SingleAsync(item => item.GoalRunId == GoalId);
        var plan = await db.TaskPlanRuns.SingleAsync(item => item.PlanId == PlanId);

        // 连续 3 次同一不可恢复原因 ⇒ 达阈值，走原终态化路径（task-bound ⇒ Goal Failed + Task Blocked）。
        Assert.AreEqual(GoalPhase.Failed, goal.Status);
        Assert.AreEqual(WorkspaceTaskStatus.Blocked, task.Status);
        Assert.AreEqual("terminal", binding.Status);
        Assert.AreEqual(TaskPlanStatuses.Failed.ToString(), plan.Status);
        Assert.AreEqual("task_plan_state_invalid", goal.BlockedCode);
        Assert.IsTrue(await db.ConversationEvents.AnyAsync(item =>
            item.ConversationId == ConversationId
            && item.Type == GoalEventTypes.Failed));
    }

    [TestMethod]
    public async Task Unrecoverable_CodeChangeResetsStreak_NoFalseTermination()
    {
        var (db, store, connection) = await CreateAsync();
        await using var _ = db;
        await using var __ = connection;
        await SeedBoundPlanAsync(db, withNextUnit: false);

        // A、普通 repair、A、A：中途换因打断连续计数 —— 4 轮后不得终态化
        //（若无归零语义，第 4 轮会被误判为“连续 3 次同 A”）。
        await SettleRoundsAsync(store, db, 4, round => round switch
        {
            1 => UnrecoverableDecision("task_plan_state_invalid"),
            2 => FailedDecision("fp-reset"),
            3 => UnrecoverableDecision("task_plan_state_invalid"),
            _ => UnrecoverableDecision("task_plan_state_invalid"),
        });

        db.ChangeTracker.Clear();
        var goal = await db.GoalRuns.SingleAsync(item => item.GoalRunId == GoalId);
        var task = await db.WorkspaceTasks.SingleAsync(item => item.TaskId == TaskId);

        // 换因归零生效：第 4 轮只是“换因后连续第 2 次”，Goal 仍 Active。
        Assert.AreEqual(GoalPhase.Active, goal.Status);
        Assert.AreNotEqual(WorkspaceTaskStatus.Blocked, task.Status);
        Assert.AreEqual(1, goal.ConsecutiveSameBlocker);
    }

    [TestMethod]
    public async Task UnsafeVerdict_TerminatesImmediately_SafetyException()
    {
        var (db, store, connection) = await CreateAsync();
        await using var _ = db;
        await using var __ = connection;
        await SeedBoundPlanAsync(db, withNextUnit: false);

        await SettleRoundsAsync(store, db, 1, _ => UnsafeDecision());

        db.ChangeTracker.Clear();
        var goal = await db.GoalRuns.SingleAsync(item => item.GoalRunId == GoalId);
        var task = await db.WorkspaceTasks.SingleAsync(item => item.TaskId == TaskId);
        var binding = await db.TaskGoalBindings.SingleAsync(item => item.GoalRunId == GoalId);

        // 安全例外：Unsafe 一次即终止（安全红线不可等 3 次）。
        Assert.AreEqual(GoalPhase.Failed, goal.Status);
        Assert.AreEqual(WorkspaceTaskStatus.Blocked, task.Status);
        Assert.AreEqual("terminal", binding.Status);
        Assert.AreEqual("unsafe", goal.BlockedCode);
    }

    [TestMethod]
    public async Task CancelledBlocker_TerminatesImmediately_UserIntentException()
    {
        var (db, store, connection) = await CreateAsync();
        await using var _ = db;
        await using var __ = connection;
        await SeedBoundPlanAsync(db, withNextUnit: false);

        // 显式取消（iteration_cancelled）：ADR-092 终态契约，不得降级 —— 一次即终态化。
        await SettleRoundsAsync(store, db, 1, _ => UnrecoverableDecision("iteration_cancelled"));

        db.ChangeTracker.Clear();
        var goal = await db.GoalRuns.SingleAsync(item => item.GoalRunId == GoalId);
        var task = await db.WorkspaceTasks.SingleAsync(item => item.TaskId == TaskId);

        Assert.AreEqual(GoalPhase.Failed, goal.Status);
        Assert.AreEqual(WorkspaceTaskStatus.Blocked, task.Status);
        Assert.AreEqual("iteration_cancelled", goal.BlockedCode);
    }
}
