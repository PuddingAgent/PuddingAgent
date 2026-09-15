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

    private static async Task<(PlatformDbContext Db, GoalSettlementStore Store, SqliteConnection Connection)> CreateAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite(connection)
            .Options;
        var db = new PlatformDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var store = new GoalSettlementStore(
            new SharedConnectionFactory(connection),
            new NoopSignal(),
            new GoalOutboxSignal());
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

        var current = await db.TaskNodes.SingleAsync(node => node.TaskNodeId == CurrentNodeId);
        var next = await db.TaskNodes.SingleAsync(node => node.TaskNodeId == NextNodeId);
        var plan = await db.TaskPlanRuns.SingleAsync(item => item.PlanId == PlanId);

        // 单元已验收：当前单元完成，但整体目标未完成，计划不得标 Completed。
        Assert.AreEqual(TaskNodeStatuses.Completed.ToString(), current.Status);
        Assert.AreNotEqual(TaskNodeStatuses.Completed.ToString(), next.Status);
        Assert.AreNotEqual(TaskPlanStatuses.Completed.ToString(), plan.Status);
    }
}
