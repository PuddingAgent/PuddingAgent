using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PuddingCode.Abstractions;
using PuddingCode.Goals;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Tasks;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services.Goals;

namespace PuddingPlatformTests.Services.Goals;

/// <summary>
/// G92-1 S1-c 片6-3b（A1）接线回归：GoalSettlementWorker 消费候选携带的
/// goal_contract_proposal → refinement store（validator→幂等探针→CAS→同事务审计）→
/// 四项完整重载 → 覆盖门解锁（source=agent_refined ⇒ IsEngineeringGatesOnly=false）。
/// 负向：不带 proposal 的既有路径行为完全不变（仍 bounded_planning、仍被覆盖门拦）。
/// 幂等：同 proposal 重放 only already-applied，不二次推进版本。兼容：历史 payload 无键不抛异常。
/// </summary>
[TestClass]
public sealed class GoalContractRefinementWiringTests
{
    private const string WorkspaceId = "ws";
    private const string ConversationId = "conv-r1";
    private const string GoalId = "goal-r1";
    private const string IterationId = "gi-r1";
    private const string TurnId = "turn-r1";
    private const string ConversationId2 = "conv-r2";
    private const string GoalId2 = "goal-r2";
    private const string IterationId2 = "gi-r2";
    private const string TurnId2 = "turn-r2";
    private const string PlanId = "plan-r1";
    private const string TaskId = "task-r1";
    private const string ReservationId = "res-r1";
    private const string BindingId = "binding-r1";
    private const string AgentId = "agent-1";
    private const string PlanFingerprint = "fp-1";
    private const long TerminalSequence = 7;
    private const string CheckWorkingDirectory = "repo-root";
    private const string Project = "Source/PuddingPlatformTests/PuddingPlatformTests.csproj";

    /// <summary>合法 proposal（段1 parser + 3a validator 全门通过；requirementRefs 完整覆盖 objective）。</summary>
    private const string ValidProposalJson =
        """{"schemaVersion":1,"kind":"refine_acceptance_contract","expectedContractVersion":1,"criteria":[{"requirement":"只输出 READY","requirementRefs":["只输出 READY"],"verification":{"kind":"text-assertion","definitionRef":"checks/text-assertion.md#equals","inputRefs":[],"expectedText":"READY"}}]}""";

    private const string CompletedPayloadWithProposal =
        $$$"""{"kind":"completed","errorCode":null,"errorMessage":null,"reply":"READY","goal_contract_proposal":{{{ValidProposalJson}}}}""";

    private const string CompletedPayloadWithoutProposal =
        """{"kind":"completed","errorCode":null,"errorMessage":null,"reply":"work done"}""";

    private const string CompletedPayloadWithNullProposal =
        """{"kind":"completed","errorCode":null,"errorMessage":null,"reply":"done","goal_contract_proposal":null}""";

    // ── 测试替身：OS 进程边界（build/test 用）；text-assertion 不经进程 ─────────

    private sealed class ScriptedTerminalProcessManager(
        Func<string, (int ExitCode, IReadOnlyList<string> Lines)> script) : ITerminalProcessManager
    {
        private readonly Dictionary<string, TerminalOutputSnapshot> _snapshots = new(StringComparer.Ordinal);

        internal List<string> Commands { get; } = [];

        public Task<TerminalProcessInfo> StartAsync(
            string sessionId,
            string command,
            string workingDir,
            CancellationToken ct = default)
        {
            var (exitCode, lines) = script(command);
            Commands.Add(command);
            var processId = $"term-{Commands.Count}";
            var info = new TerminalProcessInfo
            {
                ProcessId = processId,
                OsProcessId = 1000 + Commands.Count,
                SessionId = sessionId,
                Command = command,
                WorkingDir = workingDir,
                StartedAt = DateTimeOffset.UtcNow,
                ExitCode = exitCode,
                Status = exitCode == 0 ? TerminalProcessStatus.Exited : TerminalProcessStatus.Failed,
            };
            _snapshots[processId] = new TerminalOutputSnapshot
            {
                Process = info,
                Offset = 0,
                NextOffset = lines.Count,
                TotalLines = lines.Count,
                Truncated = false,
                Lines = lines,
            };
            return Task.FromResult(info);
        }

        public IAsyncEnumerable<string> SubscribeAsync(string processId, CancellationToken ct = default)
            => throw new NotSupportedException("GoalCheckRunner 不订阅实时输出。");

        public Task<TerminalOutputSnapshot?> ReadOutputAsync(
            string processId,
            int offset,
            int? maxLines,
            int? maxChars,
            CancellationToken ct = default)
            => Task.FromResult<TerminalOutputSnapshot?>(
                _snapshots.TryGetValue(processId, out var snapshot) ? snapshot : null);

        public Task<bool> WriteInputAsync(string processId, string input, CancellationToken ct = default)
            => Task.FromResult(false);

        public Task<bool> KillAsync(string processId) => Task.FromResult(true);

        public IReadOnlyList<TerminalProcessInfo> ListProcesses(string? sessionId = null)
            => _snapshots.Values.Select(item => item.Process).ToList();

        public Task<int> ReapAsync() => Task.FromResult(0);
    }

    private sealed class AllowAllAdmission : ITerminalCommandAdmission
    {
        public void EnsureAllowed(string command, bool yoloMode)
        {
        }
    }

    // ── 记录型 verifier：委托生产实现，捕获 worker 真正喂入的 capsule/decision ──

    private sealed class RecordingVerifier : IGoalIterationVerifier
    {
        private readonly IGoalIterationVerifier _inner = new ConservativeGoalIterationVerifier();

        internal GoalEvidenceCapsule? LastCapsule { get; private set; }

        internal GoalVerificationDecision? LastDecision { get; private set; }

        public async Task<GoalVerificationDecision> VerifyAsync(
            GoalEvidenceCapsule capsule,
            CancellationToken ct = default)
        {
            LastCapsule = capsule;
            var decision = await _inner.VerifyAsync(capsule, ct);
            LastDecision = decision;
            return decision;
        }
    }

    // ── 播种 ──────────────────────────────────────────────────────────────────

    /// <summary>无 Task 绑定的 active Goal + 已终结 canonical Turn（payload 由用例给定）。</summary>
    private static async Task SeedUnboundGoalAsync(
        IDbContextFactory<PlatformDbContext> factory,
        string goalRunId,
        string conversationId,
        string iterationId,
        string turnId,
        string objective,
        string terminalPayload)
    {
        var now = DateTimeOffset.UtcNow;
        await using var db = await factory.CreateDbContextAsync();
        db.GoalRuns.Add(new GoalRunEntity
        {
            GoalRunId = goalRunId,
            WorkspaceId = WorkspaceId,
            CurrentConversationId = conversationId,
            AgentInstanceId = AgentId,
            Objective = objective,
            ObjectiveVersion = 1,
            Status = GoalPhase.Active,
            ActivationEpoch = 1,
            MaxIterations = 256,
            IterationsStarted = 1,
            SourceCommandId = $"cmd-{goalRunId}",
        });
        db.ConversationTurns.Add(new ConversationTurnEntity
        {
            ConversationId = conversationId,
            TurnId = turnId,
            WorkspaceId = WorkspaceId,
            Status = "completed",
            AcceptedSequence = TerminalSequence - 1,
            TerminalSequence = TerminalSequence,
            TerminalKind = "completed",
            CreatedAt = 0,
            CompletedAt = 1,
        });
        db.ConversationEvents.Add(new ConversationEventEntity
        {
            ConversationId = conversationId,
            Sequence = TerminalSequence,
            EventId = $"terminal-{turnId}",
            WorkspaceId = WorkspaceId,
            TurnId = turnId,
            Type = ConversationEventTypes.TurnCompleted,
            SchemaVersion = 1,
            Payload = terminalPayload,
            OccurredAt = now.ToString("O"),
            CommittedAt = now.ToString("O"),
            CorrelationId = conversationId,
            SourceKind = "agent",
        });
        db.GoalIterations.Add(new GoalIterationEntity
        {
            GoalIterationId = iterationId,
            GoalRunId = goalRunId,
            ActivationEpoch = 1,
            IterationNo = 1,
            Status = "accepted",
            TurnId = turnId,
            RunId = "run-1",
            AcceptedSequence = TerminalSequence - 1,
            StartedAtUtc = now.AddMinutes(-1),
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Task 绑定型 Goal + 当前 WorkUnit Running（负向回归用，模板同构；无后续单元）。</summary>
    private static async Task SeedBoundGoalAsync(IDbContextFactory<PlatformDbContext> factory)
    {
        var now = DateTimeOffset.UtcNow;
        await using var db = await factory.CreateDbContextAsync();
        db.GoalRuns.Add(new GoalRunEntity
        {
            GoalRunId = GoalId,
            WorkspaceId = WorkspaceId,
            CurrentConversationId = ConversationId,
            AgentInstanceId = AgentId,
            Objective = "修复全部失败测试并证明普通开发任务可继续执行",
            ObjectiveVersion = 1,
            Status = GoalPhase.Active,
            ActivationEpoch = 1,
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
        db.ConversationEvents.Add(new ConversationEventEntity
        {
            ConversationId = ConversationId,
            Sequence = TerminalSequence,
            EventId = $"terminal-{TurnId}",
            WorkspaceId = WorkspaceId,
            TurnId = TurnId,
            Type = ConversationEventTypes.TurnCompleted,
            SchemaVersion = 1,
            Payload = CompletedPayloadWithoutProposal,
            OccurredAt = now.ToString("O"),
            CommittedAt = now.ToString("O"),
            CorrelationId = ConversationId,
            SourceKind = "agent",
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
            Status = WorkspaceTaskStatus.InProgress,
            Title = "Goal 结算集成任务",
            AcceptanceCriteria = "全部失败测试通过",
        });
        db.TaskPlanRuns.Add(new TaskPlanRunEntity
        {
            PlanId = PlanId,
            WorkspaceId = WorkspaceId,
            WorkspaceTaskId = TaskId,
            WorkspaceTaskVersion = 3,
            PlanVersion = 1,
            PlanKind = "delegation",
            PlanFingerprint = PlanFingerprint,
            RootSessionId = "session-1",
            LeaderAgentId = AgentId,
            Objective = "修复全部失败测试并证明普通开发任务可继续执行",
            Status = TaskPlanStatuses.Active.ToString(),
        });
        db.TaskNodes.Add(new TaskNodeEntity
        {
            TaskNodeId = "node-root",
            PlanId = PlanId,
            Depth = 0,
            SequenceNo = 0,
            Status = TaskNodeStatuses.Running.ToString(),
            Objective = "修复全部失败测试并证明普通开发任务可继续执行",
        });
        db.TaskNodes.Add(new TaskNodeEntity
        {
            TaskNodeId = "node-current",
            PlanId = PlanId,
            ParentTaskNodeId = "node-root",
            Depth = 1,
            SequenceNo = 1,
            Status = TaskNodeStatuses.Running.ToString(),
            Objective = "修复自动审计并证明普通开发任务能继续执行",
        });
        var reservation = new AgentExecutionReservationEntity
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
        };
        db.AgentExecutionReservations.Add(reservation);
        await db.SaveChangesAsync();
        db.TaskGoalBindings.Add(new TaskGoalBindingEntity
        {
            BindingId = BindingId,
            WorkspaceId = WorkspaceId,
            TaskId = TaskId,
            GoalRunId = GoalId,
            AgentInstanceId = AgentId,
            ReservationId = ReservationId,
            ReservationFencingToken = reservation.FencingToken,
            TaskPlanId = PlanId,
            PlanFingerprint = PlanFingerprint,
            Status = "active",
            CreatedAtUtc = now,
        });
        await db.SaveChangesAsync();
    }

    // ── 装配 ──────────────────────────────────────────────────────────────────

    private static GoalRunOptions CheckOptions(params string[] projects) => new()
    {
        Enabled = true,
        ContinuationEnabled = true,
        CheckProjects = projects,
        CheckWorkingDirectory = CheckWorkingDirectory,
    };

    private static (GoalSettlementWorker Worker, RecordingVerifier Verifier, ScriptedTerminalProcessManager ProcessManager)
        NewWorker(IDbContextFactory<PlatformDbContext> factory, GoalRunOptions options)
    {
        var contractStore = new GoalAcceptanceContractStore(factory);
        var processManager = new ScriptedTerminalProcessManager(command =>
            command.StartsWith("dotnet build", StringComparison.Ordinal)
                ? (0, new[] { "Build succeeded.", "    0 Warning(s)", "    0 Error(s)" })
                : (0, new[] { "Passed! - Failed: 0, Passed: 12, Skipped: 0, Total: 12, Duration: 1 s" }));
        var verifier = new RecordingVerifier();
        var worker = new GoalSettlementWorker(
            new GoalSettlementStore(factory, new NoopCommittedSignal(), new GoalOutboxSignal()),
            verifier,
            new GoalAcceptanceContractPlanner(
                contractStore,
                Options.Create(options),
                NullLogger<GoalAcceptanceContractPlanner>.Instance),
            contractStore,
            new GoalContractRefinementStore(factory),
            new GoalCheckRunner(
                new GoalCheckRecordStore(factory),
                processManager,
                new AllowAllAdmission(),
                NullLogger<GoalCheckRunner>.Instance),
            new GoalCheckRecordStore(factory),
            Options.Create(options),
            NullLogger<GoalSettlementWorker>.Instance);
        return (worker, verifier, processManager);
    }

    private sealed class NoopCommittedSignal : ICommittedEventSignal
    {
        public ValueTask WaitForChangeAsync(string conversationId, long knownHead, CancellationToken ct)
            => ValueTask.FromCanceled(ct);

        public void Signal(string conversationId, long committedSequence)
        {
        }
    }

    // ── ① 端到端正向：合法 proposal ⇒ 合同精炼 + 覆盖门解锁 ──────────────────

    [TestMethod]
    public async Task Worker_WithValidProposal_RefinesContract_AndUnlocksCoverageGate()
    {
        var (connection, factory) = await GoalWritePathHarness.CreateAsync();
        await using var _ = connection;
        await SeedUnboundGoalAsync(
            factory, GoalId, ConversationId, IterationId, TurnId,
            "只输出 READY", CompletedPayloadWithProposal);

        // 首轮纯门禁合同（bounded_planning v1）：一条必需 build 条件 + 一条版本化 build 检查。
        var contractStore = new GoalAcceptanceContractStore(factory);
        await contractStore.SaveAsync(GoalId, 1, 1, null, [BuildCriterion()], [BuildCheck()]);

        var (worker, verifier, _) = NewWorker(factory, CheckOptions());
        Assert.AreEqual(1, await worker.ProcessOnceAsync(CancellationToken.None));

        await using var db = await factory.CreateDbContextAsync();

        // 合同行原地 CAS 更新：仍 1 行、版本 1→2、source=agent_refined，条件换成目标级 text-assertion。
        var contract = await db.GoalAcceptanceContracts.AsNoTracking().SingleAsync();
        Assert.AreEqual(2, contract.ContractVersion);
        Assert.AreEqual(GoalAcceptanceContractSources.AgentRefined, contract.Source);
        var criteria = GoalVerificationPersistence.ReadCriteria(contract.CriteriaJson);
        var checks = GoalVerificationPersistence.ReadChecks(contract.ChecksJson);
        Assert.AreEqual(1, criteria.Count);
        Assert.AreEqual("objective-text-assertion:0", criteria[0].Id);
        Assert.IsTrue(criteria[0].Required);
        Assert.AreEqual(1, checks.Count);
        Assert.AreEqual("objective:text-assertion:0", checks[0].CheckId);
        Assert.AreEqual("READY", checks[0].ExpectedText);
        Assert.AreEqual(
            1,
            await db.ConversationEvents.AsNoTracking()
                .CountAsync(item => item.Type == GoalEventTypes.ContractRefined),
            "goal.contract_refined 审计事件必须恰 1 条。");

        // capsule 四项完整重载：source=agent_refined ⇒ IsEngineeringGatesOnly=false ⇒ 覆盖门不再命中。
        Assert.IsNotNull(verifier.LastCapsule);
        Assert.AreEqual(GoalAcceptanceContractSources.AgentRefined, verifier.LastCapsule!.AcceptanceContractSource);
        Assert.AreEqual(2, verifier.LastCapsule.AcceptanceContractVersion);
        Assert.IsFalse(verifier.LastCapsule.IsEngineeringGatesOnly);
        Assert.AreEqual(1, verifier.LastCapsule.Criteria.Count);
        Assert.AreEqual(1, verifier.LastCapsule.CheckReports.Count, "text-assertion 报告必须已持久化并回灌。");
        Assert.IsNotNull(verifier.LastDecision);
        Assert.AreEqual(GoalVerificationVerdict.Complete, verifier.LastDecision!.Verdict);
        Assert.AreNotEqual(
            "contract_coverage_insufficient",
            verifier.LastDecision.BlockerCode,
            "覆盖门不得再命中（本片要解锁的核心结果）。");

        // 结算落库：无绑定 Goal 依唯一完成判据（全部必需条件同版本 passed）终态完成。
        var verification = await db.GoalVerifications.AsNoTracking().SingleAsync();
        Assert.AreEqual("complete", verification.Verdict);
    }

    // ── 首轮纯门禁合同的最小材料（A1 成功后会被 CAS 原地替换，从不执行） ──────

    private static GoalCriterion BuildCriterion() => new()
    {
        Id = "criterion-build",
        Revision = 1,
        Requirement = "criterion build",
        Required = true,
        Kind = GoalVerificationSpecKinds.Test,
        DefinitionRef = "checks/regression.md#L1",
        DefinitionHash = "hash-a",
        InputRefs = ["Source"],
        ExecutorRole = "core",
    };

    private static GoalCheckSpec BuildCheck() => new()
    {
        CheckId = "check-criterion-build",
        CriterionId = "criterion-build",
        CriterionRevision = 1,
        Kind = GoalVerificationSpecKinds.Test,
        DefinitionRef = "checks/regression.md#L1",
        DefinitionHash = "hash-a",
        InputRefs = ["Source"],
        InputFingerprint = "fp-1",
        ExecutorRole = "core",
        ExpectedTestCount = 5,
    };

    // ── ② 负向回归：不带 proposal 的既有路径行为完全不变 ─────────────────────

    [TestMethod]
    public async Task Worker_WithoutProposal_KeepsBoundedPlanning_AndCoverageGateBlocks()
    {
        var (connection, factory) = await GoalWritePathHarness.CreateAsync();
        await using var _ = connection;
        await SeedBoundGoalAsync(factory);

        // 既有路径预置首轮合同（planner 有界派生：source=bounded_planning v1，build/test 门禁）。
        var options = CheckOptions(Project);
        var planner = new GoalAcceptanceContractPlanner(
            new GoalAcceptanceContractStore(factory),
            Options.Create(options),
            NullLogger<GoalAcceptanceContractPlanner>.Instance);
        Assert.IsTrue(await planner.EnsureContractAsync(
            GoalId, 1, 1, PlanFingerprint, "修复全部失败测试并证明普通开发任务可继续执行"));

        var (worker, verifier, processManager) = NewWorker(factory, options);
        Assert.AreEqual(1, await worker.ProcessOnceAsync(CancellationToken.None));
        Assert.AreEqual(2, processManager.Commands.Count, "build/test 门禁照常真实执行。");

        await using var db = await factory.CreateDbContextAsync();
        var contract = await db.GoalAcceptanceContracts.AsNoTracking().SingleAsync();
        Assert.AreEqual(1, contract.ContractVersion, "无 proposal 时合同不得被触碰。");
        Assert.AreEqual(GoalAcceptanceContractSources.BoundedPlanning, contract.Source);

        Assert.IsNotNull(verifier.LastCapsule);
        Assert.IsTrue(verifier.LastCapsule!.IsEngineeringGatesOnly);
        Assert.IsNotNull(verifier.LastDecision);
        Assert.AreEqual("contract_coverage_insufficient", verifier.LastDecision!.BlockerCode);
        Assert.AreEqual(
            0,
            await db.ConversationEvents.AsNoTracking()
                .CountAsync(item => item.Type == GoalEventTypes.ContractRefined));

        var verification = await db.GoalVerifications.AsNoTracking().SingleAsync();
        Assert.AreEqual("contract_coverage_insufficient", verification.BlockerCode);
    }

    // ── ③ 幂等/重复：同 proposal 重放 only already-applied，不二次推进 ────────

    [TestMethod]
    public async Task TryApply_SameProposalReplay_IsAlreadyApplied_AndNeverDoubleAdvances()
    {
        var (connection, factory) = await GoalWritePathHarness.CreateAsync();
        await using var _ = connection;
        await SeedUnboundGoalAsync(
            factory, GoalId, ConversationId, IterationId, TurnId,
            "只输出 READY", CompletedPayloadWithProposal);
        var contractStore = new GoalAcceptanceContractStore(factory);
        await contractStore.SaveAsync(GoalId, 1, 1, null, [BuildCriterion()], [BuildCheck()]);

        var refinementStore = new GoalContractRefinementStore(factory);
        GoalContractProposalFacts Facts() => new()
        {
            GoalRunId = GoalId,
            ActivationEpoch = 1,
            ObjectiveVersion = 1,
            TurnId = TurnId,
            AggregateVersion = 1,
            Objective = "只输出 READY",
            TerminalKind = "completed",
            EvidenceComplete = true,
            ProposalJson = ValidProposalJson,
        };

        var first = await refinementStore.TryApplyAsync(Facts());
        Assert.AreEqual(GoalContractRefinementStatus.Applied, first.Status);
        Assert.AreEqual(2, first.ContractVersion);

        var replay = await refinementStore.TryApplyAsync(Facts());
        Assert.AreEqual(GoalContractRefinementStatus.AlreadyApplied, replay.Status);
        Assert.AreEqual(2, replay.ContractVersion, "重放不得二次推进合同版本。");

        await using var db = await factory.CreateDbContextAsync();
        var contract = await db.GoalAcceptanceContracts.AsNoTracking().SingleAsync();
        Assert.AreEqual(2, contract.ContractVersion);
        Assert.AreEqual(
            1,
            GoalVerificationPersistence.ReadCriteria(contract.CriteriaJson).Count,
            "同一 (goal, epoch, objectiveVersion) 二次提议不得产生第二份条件。");
        Assert.AreEqual(
            1,
            await db.ConversationEvents.AsNoTracking()
                .CountAsync(item => item.Type == GoalEventTypes.ContractRefined));
    }

    // ── ④ 历史 payload 兼容：无键 / 显式 null 都不抛异常，照常出候选 ─────────

    [TestMethod]
    public async Task GetCandidates_HistoricalPayloadWithoutProposalKey_NeverThrows()
    {
        var (connection, factory) = await GoalWritePathHarness.CreateAsync();
        await using var _ = connection;
        await SeedUnboundGoalAsync(
            factory, GoalId, ConversationId, IterationId, TurnId,
            "目标 A", CompletedPayloadWithoutProposal);
        await SeedUnboundGoalAsync(
            factory, GoalId2, ConversationId2, IterationId2, TurnId2,
            "目标 B", CompletedPayloadWithNullProposal);

        var store = new GoalSettlementStore(
            factory, new NoopCommittedSignal(), new GoalOutboxSignal());

        var candidates = await store.GetCandidatesAsync(10);

        Assert.AreEqual(2, candidates.Count, "历史行必须照常产生候选（TryGetProperty，不抛异常）。");
        Assert.IsTrue(candidates.All(item => item.GoalContractProposalJson is null));
        Assert.IsTrue(candidates.All(item => item.FinalAssistantReply is not null));
    }
}
