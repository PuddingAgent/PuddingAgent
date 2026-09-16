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
/// ADR-092 §5/§6（G92-1）worker 级集成：证明「空合同 → 有界派生合同 → 受控检查真实执行 →
/// 报告持久化 → 用持久报告刷新 capsule → 依据真实报告裁决 → 结算落库」这条链路整体成立，
/// 而不是逐个单元自证。
/// <para>
/// 关键集成语义（只在这里能被测到）：
/// 1) <see cref="GoalSettlementWorker.ProcessOnceAsync"/> 自己调用 planner 派生合同并写回 capsule；
/// 2) worker 忽略 check runner 的返回值，裁决依据只能来自
///    <see cref="GoalCheckRecordStore.ReadForEpochAsync"/> + <see cref="GoalVerificationPersistence.ReadReports"/>
///    读回的 finished 报告（因此"自我声明通过"不得推进）；
/// 3) 裁决由真实报告推出：全 passed 才允许推进当前单元，任一 failed 只能修复，报告缺失只能等待；
/// 4) 派生的受控命令只能来自 <see cref="GoalCheckDefinitionRegistry"/> 的版本化定义。
/// 测试替身只替换"OS 进程"与"入参记录"两处边界，其余全部使用生产实现。
/// </para>
/// </summary>
[TestClass]
public sealed class GoalSettlementWorkerCheckIntegrationTests
{
    private const string WorkspaceId = "ws";
    private const string ConversationId = "conv-1";
    private const string GoalId = "goal-1";
    private const string IterationId = "gi-1";
    private const string TurnId = "turn-1";
    private const string PlanId = "plan-1";
    private const string TaskId = "task-1";
    private const string ReservationId = "res-1";
    private const string BindingId = "binding-1";
    private const string AgentId = "agent-1";
    private const string PlanFingerprint = "fp-1";
    private const long TerminalSequence = 7;

    private const string RootNodeId = "node-root";
    private const string CurrentNodeId = "node-current";
    private const string NextNodeId = "node-next";

    /// <summary>受控检查的作用目录只被传给进程管理器；这里只需要非空（真实执行器 fail-closed 要求）。</summary>
    private const string CheckWorkingDirectory = "repo-root";

    private const string Project = "Source/PuddingPlatformTests/PuddingPlatformTests.csproj";

    private static readonly string ExpectedBuildCommand = $"dotnet build {Project} --no-restore --nologo";

    private static readonly string ExpectedTestCommand = $"dotnet test {Project} --no-restore --nologo";

    // ── 测试替身 1：提交信号（不参与断言，仅为 Store 构造） ─────────────────────

    private sealed class NoopCommittedSignal : ICommittedEventSignal
    {
        public ValueTask WaitForChangeAsync(string conversationId, long knownHead, CancellationToken ct)
            => ValueTask.FromCanceled(ct);

        public void Signal(string conversationId, long committedSequence)
        {
        }
    }

    // ── 测试替身 2：受控执行器的 OS 进程边界 ───────────────────────────────────
    // 只替换"启动进程 / 读回输出"，其余（去重、租约、报告构造、计数解析、持久化）
    // 全部走生产 GoalCheckRunner 与生产 GoalCheckRecordStore。

    private sealed class ScriptedTerminalProcessManager(
        Func<string, (int ExitCode, IReadOnlyList<string> Lines)> script,
        Func<string, bool>? neverExit = null) : ITerminalProcessManager
    {
        private readonly Dictionary<string, TerminalOutputSnapshot> _snapshots = new(StringComparer.Ordinal);

        internal List<string> Commands { get; } = [];

        public Task<TerminalProcessInfo> StartAsync(
            string sessionId,
            string command,
            string workingDir,
            CancellationToken ct = default)
        {
            // neverExit：模拟「真超时」——进程一直 Running、无退出码；其余按退出码派生终态（与生产一致）。
            var neverExits = neverExit?.Invoke(command) == true;
            (int ExitCode, IReadOnlyList<string> Lines) scripted;
            if (neverExits)
                scripted = (0, Array.Empty<string>()); // 占位值：Running 状态下退出码以 null 表达。
            else
                scripted = script(command);

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
                ExitCode = neverExits ? null : scripted.ExitCode,
                // 按退出码派生终态：0 ⇒ Exited，非 0 ⇒ Failed（生产 TerminalProcessManager 的状态机）。
                // 此前写死 Exited 会让集成测试对「执行完毕且失败被误分类为等待」失明。
                Status = neverExits
                    ? TerminalProcessStatus.Running
                    : scripted.ExitCode == 0 ? TerminalProcessStatus.Exited : TerminalProcessStatus.Failed,
            };
            _snapshots[processId] = new TerminalOutputSnapshot
            {
                Process = info,
                Offset = 0,
                NextOffset = scripted.Lines.Count,
                TotalLines = scripted.Lines.Count,
                Truncated = false,
                Lines = scripted.Lines,
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

        public Task<bool> KillAsync(string processId)
            => Task.FromResult(true);

        public IReadOnlyList<TerminalProcessInfo> ListProcesses(string? sessionId = null)
            => _snapshots.Values.Select(item => item.Process).ToList();

        public Task<int> ReapAsync()
            => Task.FromResult(0);
    }

    // ── 测试替身 3：只"自我声明"、什么都不持久化的检查执行器（负对照） ──────────
    // 它返回的报告在证据策略下完全合法（status/runner/报告引用/计数齐备），
    // 但 worker 只能从持久记录里取报告 —— 因此这些声明不得改变裁决。

    private sealed class SelfClaimingCheckRunner : IGoalCheckRunner
    {
        internal int CallCount { get; private set; }

        internal IReadOnlyList<GoalCheckSpec> LastChecks { get; private set; } = [];

        public Task<IReadOnlyList<GoalCheckReport>> RunAsync(
            IReadOnlyList<GoalCheckSpec> checks,
            GoalCheckContext context,
            CancellationToken ct = default)
        {
            CallCount++;
            LastChecks = checks;
            var reports = checks
                .Select(check => new GoalCheckReport
                {
                    CheckId = check.CheckId,
                    CriterionId = check.CriterionId,
                    CriterionRevision = check.CriterionRevision,
                    Status = GoalCriterionResultStatuses.Passed,
                    EvidenceRefs = [$"self-claim:{check.CheckId}"],
                    InputFingerprint = check.InputFingerprint,
                    DefinitionHash = check.DefinitionHash,
                    ReportRef = $"self-claim:{check.CheckId}",
                    ExitCode = 0,
                    ExecutedTestCount = 42,
                    PassedTestCount = 42,
                    FailedTestCount = 0,
                    RunnerId = "self-claimed-runner",
                    InvocationId = $"self-claim:{check.CheckId}",
                    ReportedAtUtc = DateTimeOffset.UtcNow,
                })
                .ToList();
            return Task.FromResult<IReadOnlyList<GoalCheckReport>>(reports);
        }
    }

    // ── 测试替身 4：记录 worker 真正喂给 verifier 的 capsule / decision ─────────
    // 只做委托 + 记录，裁决仍由生产 ConservativeGoalIterationVerifier 产生。

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

    /// <summary>
    /// 播种一个已终结的 canonical Turn + 绑定的 Task/Plan（当前单元 Running、可选后续单元），
    /// 并写入 turn.completed 证据事件，使
    /// <see cref="GoalSettlementStore.GetCandidatesAsync"/> 能构造出可结算候选
    /// （EvidenceComplete == true）。注意：候选只从持久事实构造，`EvidenceComplete == false`
    /// 会被结算的确定性 gate 覆盖成 evidence_incomplete。
    /// </summary>
    private static async Task SeedTerminalIterationAsync(
        IDbContextFactory<PlatformDbContext> factory,
        bool withNextUnit)
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

        // 结算候选要求 canonical 终态事件真的落库（证据完整性由事件而非 Turn 状态决定）。
        db.ConversationEvents.Add(new ConversationEventEntity
        {
            ConversationId = ConversationId,
            Sequence = TerminalSequence,
            EventId = $"terminal-{TurnId}",
            WorkspaceId = WorkspaceId,
            TurnId = TurnId,
            Type = ConversationEventTypes.TurnCompleted,
            SchemaVersion = 1,
            Payload = "{}",
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

        // 绑定要在 reservation 的身份列生成后写入：fencing token 必须真实一致，
        // 否则结算会被 reservation_fence_lost 覆盖，报告驱动的裁决就测不到了。
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

    private static GoalSettlementWorker NewWorker(
        IDbContextFactory<PlatformDbContext> factory,
        GoalRunOptions options,
        IGoalCheckRunner checkRunner,
        IGoalIterationVerifier verifier)
    {
        var contractStore = new GoalAcceptanceContractStore(factory);
        return new GoalSettlementWorker(
            new GoalSettlementStore(factory, new NoopCommittedSignal(), new GoalOutboxSignal()),
            verifier,
            new GoalAcceptanceContractPlanner(
                contractStore,
                Options.Create(options),
                NullLogger<GoalAcceptanceContractPlanner>.Instance),
            contractStore,
            checkRunner,
            new GoalCheckRecordStore(factory),
            Options.Create(options),
            NullLogger<GoalSettlementWorker>.Instance);
    }

    private static (int ExitCode, IReadOnlyList<string> Lines) GreenScript(string command)
        => command.StartsWith("dotnet build", StringComparison.Ordinal)
            ? (0, new[] { "Build succeeded.", "    0 Warning(s)", "    0 Error(s)" })
            : (0, new[]
            {
                "Passed! - Failed: 0, Passed: 12, Skipped: 0, Total: 12, Duration: 1 s",
            });

    // ── 测试 ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// 主链路：空合同 → worker 有界派生合同 → 受控执行器真实执行（命令来自版本化注册表）→
    /// 报告持久化 → 用持久报告刷新 capsule → 全 passed 才推进当前单元（但不得完成 Plan/Goal）。
    /// </summary>
    [TestMethod]
    public async Task Worker_DerivesContract_RunsRealChecks_AndAdvancesOnlyFromPersistedReports()
    {
        var (connection, factory) = await GoalWritePathHarness.CreateAsync();
        await using var _ = connection;
        await SeedTerminalIterationAsync(factory, withNextUnit: true);

        var processManager = new ScriptedTerminalProcessManager(GreenScript);
        var checkRunner = new GoalCheckRunner(
            new GoalCheckRecordStore(factory),
            processManager,
            new AllowAllAdmission(), NullLogger<GoalCheckRunner>.Instance);
        var verifier = new RecordingVerifier();
        var worker = NewWorker(factory, CheckOptions(Project), checkRunner, verifier);

        Assert.AreEqual(1, await worker.ProcessOnceAsync(CancellationToken.None));

        await using var db = await factory.CreateDbContextAsync();

        // 1) 合同派生：worker 自己从"空合同"生成必需条件 + 版本化检查定义。
        var contract = await db.GoalAcceptanceContracts.AsNoTracking().SingleAsync();
        Assert.AreEqual(1, contract.ContractVersion);
        Assert.AreEqual(GoalAcceptanceContractPlanner.Source, contract.Source);
        // 检查的输入指纹必须来自绑定的 Plan 指纹（合同与工作树版本的唯一桥）。
        Assert.AreEqual(PlanFingerprint, contract.PlanFingerprint);

        var criteria = GoalVerificationPersistence.ReadCriteria(contract.CriteriaJson);
        var checks = GoalVerificationPersistence.ReadChecks(contract.ChecksJson);
        Assert.AreEqual(2, criteria.Count, "有界规划必须产出 build + test 两个必需条件。");
        Assert.AreEqual(2, checks.Count);
        Assert.IsTrue(criteria.All(item => item.Required), "派生条件必须全部是必需条件。");
        Assert.IsTrue(checks.All(item => item.InputFingerprint == PlanFingerprint));

        // 2) 受控检查真实执行：命令只能来自注册表的版本化定义，且目标是配置的受检项目。
        CollectionAssert.AreEquivalent(
            new[] { ExpectedBuildCommand, ExpectedTestCommand },
            processManager.Commands,
            "受控检查命令必须由检查定义注册表按受检目标生成。");

        // 3) 报告持久化：只有 finished + 真报告才是可信证据。
        var records = await db.GoalCheckRecords.AsNoTracking().ToListAsync();
        Assert.AreEqual(2, records.Count);
        Assert.IsTrue(
            records.All(item => item.Status == GoalCheckRecordStatuses.Finished),
            "真实执行出来的检查必须落成 finished 记录。");
        Assert.IsTrue(records.All(item => !string.IsNullOrWhiteSpace(item.ReportJson)));

        var reports = GoalVerificationPersistence.ReadReports(records);
        Assert.AreEqual(2, reports.Count);
        Assert.IsTrue(reports.All(item => item.Status == GoalCriterionResultStatuses.Passed));
        Assert.IsTrue(reports.All(item => item.RunnerId == GoalCheckRunner.RunnerId));

        // 4) capsule 刷新 + 依据真实报告裁决（记录的是 worker 真正喂给 verifier 的快照）。
        Assert.IsNotNull(verifier.LastCapsule, "worker 必须真的调用 verifier。");
        Assert.AreEqual(2, verifier.LastCapsule!.Criteria.Count, "派生合同必须写回 capsule。");
        Assert.AreEqual(2, verifier.LastCapsule.Checks.Count);
        Assert.AreEqual(
            2,
            verifier.LastCapsule.CheckReports.Count,
            "capsule 的 CheckReports 必须由持久记录刷新，而不是执行器的返回值。");

        Assert.IsNotNull(verifier.LastDecision);
        Assert.IsTrue(
            GoalSettlementDecisionCalculator.AllRequiredCriteriaPassed(verifier.LastDecision!),
            "全部必需条件必须依据真实报告判为通过。");

        // 5) 结算落库：单元推进（当前单元 Completed），但 Plan/Goal 不得被宣布完成。
        var current = await db.TaskNodes.AsNoTracking().SingleAsync(item => item.TaskNodeId == CurrentNodeId);
        Assert.AreEqual(
            TaskNodeStatuses.Completed.ToString(),
            current.Status,
            "真实报告全通过后才允许推进当前单元。");

        var next = await db.TaskNodes.AsNoTracking().SingleAsync(item => item.TaskNodeId == NextNodeId);
        Assert.AreNotEqual(TaskNodeStatuses.Completed.ToString(), next.Status);

        var plan = await db.TaskPlanRuns.AsNoTracking().SingleAsync(item => item.PlanId == PlanId);
        Assert.AreNotEqual(TaskPlanStatuses.Completed.ToString(), plan.Status);

        var goal = await db.GoalRuns.AsNoTracking().SingleAsync(item => item.GoalRunId == GoalId);
        Assert.AreEqual(GoalPhase.Active, goal.Status);

        var verification = await db.GoalVerifications.AsNoTracking().SingleAsync();
        Assert.AreEqual("continue", verification.Verdict, "工作单元通过只能推进，不能判定整体完成。");
    }

    /// <summary>
    /// 失败优先：真实执行出 failed 报告时，裁决必须落在 criterion_failed（可修复），
    /// 单元不得推进、Goal 不得终态化 —— 失败报告与失败码必须可回溯。
    /// </summary>
    [TestMethod]
    public async Task Worker_WhenPersistedCheckFails_BlocksTheWorkUnitWithFailureEvidence()
    {
        var (connection, factory) = await GoalWritePathHarness.CreateAsync();
        await using var _ = connection;
        await SeedTerminalIterationAsync(factory, withNextUnit: true);

        var processManager = new ScriptedTerminalProcessManager(command =>
            command.StartsWith("dotnet build", StringComparison.Ordinal)
                ? (0, new[] { "Build succeeded." })
                : (1, new[] { "Failed: 1, Passed: 11, Skipped: 0, Total: 12, Duration: 1 s" }));

        var worker = NewWorker(
            factory,
            CheckOptions(Project),
            new GoalCheckRunner(
                new GoalCheckRecordStore(factory),
                processManager,
                new AllowAllAdmission(), NullLogger<GoalCheckRunner>.Instance),
            new RecordingVerifier());

        Assert.AreEqual(1, await worker.ProcessOnceAsync(CancellationToken.None));

        await using var db = await factory.CreateDbContextAsync();

        var reports = GoalVerificationPersistence.ReadReports(
            await db.GoalCheckRecords.AsNoTracking().ToListAsync());
        Assert.AreEqual(2, reports.Count);
        Assert.IsTrue(
            reports.Any(item => item.FailureCode == GoalCheckFailureCodes.NonZeroExitCode),
            "真实退出码非 0 必须作为失败码持久化。");

        var verification = await db.GoalVerifications.AsNoTracking().SingleAsync();
        Assert.AreEqual("criterion_failed", verification.BlockerCode);
        Assert.IsFalse(string.IsNullOrWhiteSpace(verification.BlockerMessage));

        var current = await db.TaskNodes.AsNoTracking().SingleAsync(item => item.TaskNodeId == CurrentNodeId);
        Assert.AreNotEqual(TaskNodeStatuses.Completed.ToString(), current.Status);
        Assert.IsNull(current.CompletedAt);

        var goal = await db.GoalRuns.AsNoTracking().SingleAsync(item => item.GoalRunId == GoalId);
        Assert.AreEqual(GoalPhase.Active, goal.Status, "可修复的失败不得把 Task-bound Goal 终态化。");
    }

    /// <summary>
    /// R6 区分性：真超时（进程一直未结束）仍必须落在 check_results_pending（等待），
    /// 与「执行完毕且失败 ⇒ criterion_failed」形成对照 —— 两条通路不得混同。
    /// </summary>
    [TestMethod]
    public async Task Worker_WhenCheckTrulyTimesOut_StillWaitsForPendingResults()
    {
        var (connection, factory) = await GoalWritePathHarness.CreateAsync();
        await using var _ = connection;
        await SeedTerminalIterationAsync(factory, withNextUnit: true);

        // 测试直接构造 options（不走 30s 下限校验），把超时压到 1s 以免拖慢套件。
        var options = CheckOptions(Project);
        options.CheckTimeoutSeconds = 1;

        var processManager = new ScriptedTerminalProcessManager(
            command => command.StartsWith("dotnet build", StringComparison.Ordinal)
                ? (0, new[] { "Build succeeded." })
                : (0, new[] { "Passed! - Failed: 0, Passed: 12, Skipped: 0, Total: 12, Duration: 1 s" }),
            command => command.StartsWith("dotnet test", StringComparison.Ordinal));

        var worker = NewWorker(
            factory,
            options,
            new GoalCheckRunner(
                new GoalCheckRecordStore(factory),
                processManager,
                new AllowAllAdmission(), NullLogger<GoalCheckRunner>.Instance),
            new RecordingVerifier());

        Assert.AreEqual(1, await worker.ProcessOnceAsync(CancellationToken.None));

        await using var db = await factory.CreateDbContextAsync();

        // build 正常 finished；test 真超时 ⇒ 退回 pending、无报告、保留 check_timeout 失败码。
        var records = await db.GoalCheckRecords.AsNoTracking().ToListAsync();
        Assert.AreEqual(2, records.Count);
        Assert.IsTrue(
            records.Any(item => item.Status == GoalCheckRecordStatuses.Pending
                && item.ReportJson is null
                && item.FailureCode == GoalCheckFailureCodes.CheckTimeout),
            "真超时的检查必须退回 pending（等待），不得落成终态。");
        Assert.AreEqual(
            1,
            records.Count(item => item.Status == GoalCheckRecordStatuses.Finished),
            "未超时的检查仍应正常落 finished。");

        var verification = await db.GoalVerifications.AsNoTracking().SingleAsync();
        Assert.AreEqual("check_results_pending", verification.BlockerCode);

        var current = await db.TaskNodes.AsNoTracking().SingleAsync(item => item.TaskNodeId == CurrentNodeId);
        Assert.AreNotEqual(TaskNodeStatuses.Completed.ToString(), current.Status);
    }

    /// <summary>
    /// fail-closed：没有配置受检目标时不得凭 Turn 终态通过 —— 合同保持为空、
    /// 不执行任何检查、裁决落在 acceptance_contract_missing（有界修复而非 vacuous pass）。
    /// </summary>
    [TestMethod]
    public async Task Worker_WithoutBoundedTargets_KeepsContractEmptyAndRunsNoCheck()
    {
        var (connection, factory) = await GoalWritePathHarness.CreateAsync();
        await using var _ = connection;
        await SeedTerminalIterationAsync(factory, withNextUnit: true);

        var processManager = new ScriptedTerminalProcessManager(GreenScript);
        var verifier = new RecordingVerifier();
        var worker = NewWorker(
            factory,
            CheckOptions(),
            new GoalCheckRunner(
                new GoalCheckRecordStore(factory),
                processManager,
                new AllowAllAdmission(), NullLogger<GoalCheckRunner>.Instance),
            verifier);

        Assert.AreEqual(1, await worker.ProcessOnceAsync(CancellationToken.None));

        await using var db = await factory.CreateDbContextAsync();

        Assert.AreEqual(0, await db.GoalAcceptanceContracts.AsNoTracking().CountAsync());
        Assert.AreEqual(0, await db.GoalCheckRecords.AsNoTracking().CountAsync());
        Assert.AreEqual(0, processManager.Commands.Count, "空合同不得启动任何受控检查。");

        Assert.AreEqual("acceptance_contract_missing", verifier.LastDecision!.BlockerCode);
        Assert.IsFalse(
            GoalSettlementDecisionCalculator.AllRequiredCriteriaPassed(verifier.LastDecision!),
            "空合同绝不能 vacuous pass。");

        var verification = await db.GoalVerifications.AsNoTracking().SingleAsync();
        Assert.AreEqual("acceptance_contract_missing", verification.BlockerCode);

        var current = await db.TaskNodes.AsNoTracking().SingleAsync(item => item.TaskNodeId == CurrentNodeId);
        Assert.AreNotEqual(TaskNodeStatuses.Completed.ToString(), current.Status);

        var goal = await db.GoalRuns.AsNoTracking().SingleAsync(item => item.GoalRunId == GoalId);
        Assert.AreEqual(GoalPhase.Active, goal.Status);
    }

    /// <summary>
    /// 负对照：执行器返回的报告即使完全"合法"（runner/报告引用/退出码/计数齐备），
    /// 只要没有持久化，worker 就不能据此裁决 —— 裁决只认持久报告，落在 check_results_pending。
    /// </summary>
    [TestMethod]
    public async Task Worker_IgnoresRunnerReturnedReports_AndWaitsForPersistedEvidence()
    {
        var (connection, factory) = await GoalWritePathHarness.CreateAsync();
        await using var _ = connection;
        await SeedTerminalIterationAsync(factory, withNextUnit: true);

        var checkRunner = new SelfClaimingCheckRunner();
        var verifier = new RecordingVerifier();
        var worker = NewWorker(factory, CheckOptions(Project), checkRunner, verifier);

        Assert.AreEqual(1, await worker.ProcessOnceAsync(CancellationToken.None));

        await using var db = await factory.CreateDbContextAsync();

        Assert.AreEqual(1, checkRunner.CallCount, "合同有检查定义时 worker 必须真的调用执行器。");
        Assert.AreEqual(2, checkRunner.LastChecks.Count);
        Assert.AreEqual(
            2,
            GoalVerificationPersistence.ReadChecks(
                (await db.GoalAcceptanceContracts.AsNoTracking().SingleAsync()).ChecksJson).Count,
            "worker 必须先把派生合同持久化，再把其中的检查交给执行器。");

        Assert.AreEqual(
            0,
            await db.GoalCheckRecords.AsNoTracking().CountAsync(),
            "自我声明的执行器不写持久记录。");

        Assert.IsNotNull(verifier.LastCapsule);
        Assert.AreEqual(
            0,
            verifier.LastCapsule!.CheckReports.Count,
            "capsule 只能由持久报告刷新，执行器返回值不构成证据。");
        Assert.IsTrue(
            verifier.LastDecision!.CriterionResults.All(
                item => item.Status == GoalCriterionResultStatuses.Pending),
            "没有持久报告时必需条件只能按 pending 处理。");

        var verification = await db.GoalVerifications.AsNoTracking().SingleAsync();
        Assert.AreEqual("check_results_pending", verification.BlockerCode);

        var current = await db.TaskNodes.AsNoTracking().SingleAsync(item => item.TaskNodeId == CurrentNodeId);
        Assert.AreNotEqual(TaskNodeStatuses.Completed.ToString(), current.Status);
    }

    /// <summary>准入桩：本文件验证结算链路，不验证准入策略。</summary>
    private sealed class AllowAllAdmission : ITerminalCommandAdmission
    {
        public void EnsureAllowed(string command, bool isYoloMode)
        {
        }
    }
}
