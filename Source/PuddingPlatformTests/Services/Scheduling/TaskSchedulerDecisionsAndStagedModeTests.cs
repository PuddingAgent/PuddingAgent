using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PuddingCode.Goals;
using PuddingCode.Scheduling;
using PuddingCode.Tasks;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services.Scheduling;

namespace PuddingPlatformTests.Services.Scheduling;

/// <summary>
/// 调度内核三缺口（评分 breakdown / 决策持久化 / staged mode）验收测试：
/// 评分确定性+序列化、决策幂等、SQLite 重开库恢复、staged 五值 Validate 矩阵、
/// needs_refinement 落库、next_eligible_at_utc 写回、shadow/authoritative 行分流。
/// </summary>
[TestClass]
public sealed class TaskSchedulerDecisionsAndStagedModeTests
{
    private string _root = null!;
    private PlatformDbContextFactory _factory = null!;
    private readonly DateTimeOffset _now = DateTimeOffset.Parse("2026-08-31T08:00:00Z");

    [TestInitialize]
    public async Task InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), "PuddingAgent", "scheduler-decisions-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _factory = CreateFactory();
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
        await TaskSchedulerDecisionSchemaBootstrapper.EnsureCreatedAsync(db);
    }

    [TestCleanup]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    // ── 验收①：评分确定性 + breakdown 序列化 ─────────────────────
    [TestMethod]
    public void Score_IsDeterministic_ForIdenticalInputs()
    {
        var task = ScoreTask();
        var availability = IdleAvailability();

        var first = TaskSchedulerScorer.Score(task, availability, routeCompatible: true, routeSelectionCode: "preferred_agent", now: _now);
        var second = TaskSchedulerScorer.Score(task, availability, routeCompatible: true, routeSelectionCode: "preferred_agent", now: _now);

        Assert.AreEqual(first, second, "Same inputs must produce the identical breakdown.");
        Assert.IsTrue(first.Total is > 0 and <= 100);
        Assert.AreEqual(1.0, first.AgentCapability);
        Assert.AreEqual(1.0, first.AgentRoute);
        Assert.AreEqual(1.0, first.AgentHealth);
        Assert.AreEqual(1.0, first.AgentCapacity);
        Assert.AreEqual(1.0, first.Priority);
    }

    [TestMethod]
    public void ScoreBreakdown_SerializesToJson_RoundTrips()
    {
        var breakdown = TaskSchedulerScorer.Score(
            ScoreTask(), IdleAvailability(), routeCompatible: true, routeSelectionCode: "compatible_agent", now: _now);

        var json = JsonSerializer.Serialize(breakdown);
        var restored = JsonSerializer.Deserialize<TaskSchedulerScoreBreakdown>(json);

        Assert.IsNotNull(restored);
        Assert.AreEqual(breakdown, restored);
        Assert.AreEqual(breakdown.Total, restored.Total);
        Assert.IsTrue(restored.AgentRoute < breakdown.AgentCapability,
            "compatible_agent route factor must be lower than capability factor.");
    }

    [TestMethod]
    public void Score_RanksIdlePreferredAgentAboveBusyCompatibleFallback()
    {
        var task = ScoreTask();
        var idlePreferred = TaskSchedulerScorer.Score(task, IdleAvailability(), true, "preferred_agent", _now);
        var busyFallback = TaskSchedulerScorer.Score(
            task,
            IdleAvailability() with { State = AgentAvailabilityState.Busy, ReasonCode = "runtime_execution", IdleSinceUtc = null },
            true,
            "compatible_agent",
            _now);

        Assert.IsTrue(idlePreferred.Total > busyFallback.Total,
            "total desc ordering must prefer the idle preferred agent over a busy fallback.");
    }

    [TestMethod]
    public void EffectiveMaxStartsPerScan_ForcesOneInAuthoritativeSingle()
    {
        var configured = new TaskAutoDispatchOptions { MaxStartsPerScan = 7 };

        Assert.AreEqual(1, TaskAutoDispatchOptions.EffectiveMaxStartsPerScan(
            new TaskAutoDispatchOptions { Mode = "authoritative-single", MaxStartsPerScan = 7 }));
        Assert.AreEqual(7, TaskAutoDispatchOptions.EffectiveMaxStartsPerScan(
            new TaskAutoDispatchOptions { Mode = "authoritative-bounded", MaxStartsPerScan = 7 }));
        Assert.AreEqual(7, TaskAutoDispatchOptions.EffectiveMaxStartsPerScan(
            new TaskAutoDispatchOptions { Mode = "authoritative", MaxStartsPerScan = 7 }));
        Assert.AreEqual(7, TaskAutoDispatchOptions.EffectiveMaxStartsPerScan(configured),
            "options without a mode keep the configured value.");
    }

    // ── 验收④：staged 五值 Validate 矩阵 ────────────────────────
    [TestMethod]
    public void Validate_AcceptsAllFiveStagedModes_CaseInsensitive()
    {
        var safeGoals = new TaskBoundGoalOptions { Enabled = true };
        var safeRuns = new GoalRunOptions { Enabled = true, ContinuationEnabled = true };
        string[] modes =
        [
            "disabled",
            "shadow",
            "authoritative-single",
            "authoritative-bounded",
            "authoritative",
            "SHADOW",
            "Authoritative-Single",
            "  authoritative-bounded  ",
        ];

        foreach (var mode in modes)
        {
            var errors = TaskAutoDispatchOptions.Validate(
                new TaskAutoDispatchOptions { Enabled = true, Mode = mode },
                safeGoals,
                safeRuns);
            Assert.IsFalse(errors.Any(item => item.Contains("Mode", StringComparison.Ordinal)),
                $"mode '{mode}' must pass the staged matrix; errors: {string.Join(";", errors)}");
        }

        foreach (var badMode in new[] { "bogus", "", "authoritative-full", "Authoritative " + "X" })
        {
            var errors = TaskAutoDispatchOptions.Validate(
                new TaskAutoDispatchOptions { Enabled = true, Mode = badMode },
                safeGoals,
                safeRuns);
            Assert.IsTrue(errors.Any(item => item.Contains("Mode", StringComparison.Ordinal)),
                $"invalid mode '{badMode}' must be rejected.");
        }
    }

    // ── 验收②：决策幂等（同 scan_id 重放不重复行）──────────────
    [TestMethod]
    public async Task RecordCandidateDecisions_IsIdempotentPerScanId()
    {
        var store = new TaskSchedulerDecisionStore(_factory);
        var decisions = new[]
        {
            DeferredDecision("task-1", "task_not_yet_eligible", _now.AddMinutes(30)),
            DeferredDecision("task-2", "agent_not_idle", null),
        };

        var inserted = await store.RecordCandidateDecisionsAsync("ws", "shadow", "scan-a", decisions);
        var replayed = await store.RecordCandidateDecisionsAsync("ws", "shadow", "scan-a", decisions);

        Assert.AreEqual(2, inserted);
        Assert.AreEqual(0, replayed, "replaying the same scan_id must not duplicate rows.");
        Assert.AreEqual(2, await CountRowsAsync());
    }

    // ── 验收⑦：shadow/authoritative 行分流 ──────────────────────
    [TestMethod]
    public async Task RecordCandidateDecisions_ShadowAndAuthoritativeRowsAreSeparableByMode()
    {
        var store = new TaskSchedulerDecisionStore(_factory);
        await store.RecordCandidateDecisionsAsync(
            "ws", "shadow", "scan-shadow",
            [DeferredDecision("task-1", "task_not_yet_eligible", _now.AddMinutes(30))]);
        await store.RecordCandidateDecisionsAsync(
            "ws", "authoritative-single", "scan-auth",
            [DeferredDecision("task-1", "task_not_yet_eligible", _now.AddMinutes(30))]);

        Assert.AreEqual(1, await CountRowsAsync("mode = 'shadow'"));
        Assert.AreEqual(1, await CountRowsAsync("mode = 'authoritative-single'"));
    }

    // ── 验收⑤：needs_refinement 落库（5 verdict 稳定 snake_case）──
    [TestMethod]
    public async Task RecordRefinementDecisions_PersistsAllFiveVerdicts()
    {
        var store = new TaskSchedulerDecisionStore(_factory);
        var decisions = new[]
        {
            Refinement("task-ready", TaskBacklogRefinementVerdict.ReadyCandidate, "ready_for_auto_dispatch", "agent-1"),
            Refinement("task-desc", TaskBacklogRefinementVerdict.NeedsRefinement, "description_required"),
            Refinement("task-ac", TaskBacklogRefinementVerdict.NeedsRefinement, "acceptance_criteria_required"),
            Refinement("task-type", TaskBacklogRefinementVerdict.NeedsRefinement, "task_type_unclassified"),
            Refinement("task-agent", TaskBacklogRefinementVerdict.NeedsRefinement, "no_compatible_agent"),
        };

        var inserted = await store.RecordRefinementDecisionsAsync("ws", "shadow", "scan-refine", decisions);

        Assert.AreEqual(5, inserted);
        Assert.AreEqual(1, await CountRowsAsync("phase = 'refinement' AND decision = 'ready'"));
        Assert.AreEqual(4, await CountRowsAsync("phase = 'refinement' AND decision = 'needs_refinement'"));
        Assert.AreEqual(1, await CountRowsAsync("phase = 'refinement' AND decision_code = 'description_required'"));
        // refinement 重放同样幂等
        var replayed = await store.RecordRefinementDecisionsAsync("ws", "shadow", "scan-refine", decisions);
        Assert.AreEqual(0, replayed);
    }

    // ── 验收⑥：next_eligible_at_utc 写回（只前推不回拨）─────────
    [TestMethod]
    public async Task WriteBack_SetsNextEligibleAtUtc_AndNeverRollsBack()
    {
        await AddTaskAsync("task-1");
        var store = new TaskSchedulerDecisionStore(_factory);
        var gate = _now.AddMinutes(30);

        var updated = await store.ApplyNextEligibleWriteBackAsync(
            "ws", [DeferredDecision("task-1", "task_not_yet_eligible", gate)]);

        Assert.AreEqual(1, updated);
        Assert.AreEqual(gate, await ReadNextEligibleAsync("task-1"));

        // 更早的门不得回拨已前推的门
        var rolled = await store.ApplyNextEligibleWriteBackAsync(
            "ws", [DeferredDecision("task-1", "task_not_yet_eligible", _now.AddMinutes(10))]);
        Assert.AreEqual(0, rolled);
        Assert.AreEqual(gate, await ReadNextEligibleAsync("task-1"));
    }

    [TestMethod]
    public async Task WriteBack_SkipsEligibleDecisionsAndTasksWithoutGate()
    {
        await AddTaskAsync("task-1");
        var store = new TaskSchedulerDecisionStore(_factory);
        var eligible = DeferredDecision("task-1", "eligible", _now.AddMinutes(5))
            with { Verdict = TaskAutoDispatchCandidateVerdict.Eligible };

        var updated = await store.ApplyNextEligibleWriteBackAsync("ws", [eligible, DeferredDecision("task-1", "agent_not_idle", null)]);

        Assert.AreEqual(0, updated);
        Assert.IsNull(await ReadNextEligibleAsync("task-1"));
    }

    // ── 验收③：SQLite 重开库恢复（SchemaBootstrapper 重入安全）──
    [TestMethod]
    public async Task DecisionSchema_ReopensSqliteDatabase_AndIsReentrantSafe()
    {
        var dbPath = Path.Combine(_root, "recovery.db");
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite($"Data Source={dbPath};Default Timeout=10")
            .Options;

        var first = new PlatformDbContextFactory(options);
        await using (var db = await first.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
            await TaskSchedulerDecisionSchemaBootstrapper.EnsureCreatedAsync(db);
        }

        SqliteConnection.ClearAllPools();

        var second = new PlatformDbContextFactory(options);
        await using (var db2 = await second.CreateDbContextAsync())
        {
            // 重开库后重入 bootstrap 必须无异常（IF NOT EXISTS 幂等）
            await TaskSchedulerDecisionSchemaBootstrapper.EnsureCreatedAsync(db2);
            await TaskSchedulerDecisionSchemaBootstrapper.EnsureCreatedAsync(db2);
        }

        var store = new TaskSchedulerDecisionStore(second);
        var inserted = await store.RecordCandidateDecisionsAsync(
            "ws", "shadow", "scan-recovery",
            [DeferredDecision("task-1", "task_not_yet_eligible", _now.AddMinutes(30))]);
        Assert.AreEqual(1, inserted, "decisions must persist after a database reopen.");
    }

    [TestMethod]
    public void DecisionCodes_NormalizesKnownAliases_AndSnakeCasesUnknown()
    {
        Assert.AreEqual("task_not_yet_eligible", TaskSchedulerDecisionCodes.Normalize("TaskNotYetEligible"));
        Assert.AreEqual("ready_for_auto_dispatch", TaskSchedulerDecisionCodes.Normalize("ReadyForAutoDispatch"));
        Assert.AreEqual("agent_not_idle", TaskSchedulerDecisionCodes.Normalize("agent_not_idle"));
        Assert.AreEqual("custom_plan_code", TaskSchedulerDecisionCodes.Normalize("Custom Plan Code"));
        Assert.AreEqual("unknown", TaskSchedulerDecisionCodes.Normalize(null));
    }

    // ── 测试工厂 ────────────────────────────────────────────────
    private PlatformDbContextFactory CreateFactory()
    {
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite($"Data Source={Path.Combine(_root, "platform.db")};Default Timeout=10")
            .Options;
        return new PlatformDbContextFactory(options);
    }

    private WorkspaceTaskEntity ScoreTask() => new()
    {
        TaskId = "task-score",
        WorkspaceId = "ws",
        Title = "score fixture",
        Status = WorkspaceTaskStatus.Ready,
        Priority = TaskPriority.P0,
        ExecutionWindow = TaskExecutionWindow.Anytime,
        TaskType = "implementation",
        AutoDispatchEnabled = true,
        SortOrder = 0,
        Version = 1,
        CreatedAtUtc = _now.AddDays(-1),
        UpdatedAtUtc = _now.AddDays(-1),
    };

    private static AgentAvailabilitySnapshot IdleAvailability() => new()
    {
        WorkspaceId = "ws",
        AgentId = "agent-1",
        State = AgentAvailabilityState.Idle,
        ActivityReason = AgentActivityReason.None,
        Version = 7,
        ObservedAtUtc = DateTimeOffset.Parse("2026-08-31T08:00:00Z"),
        ValidUntilUtc = DateTimeOffset.Parse("2026-08-31T08:01:00Z"),
        IdleSinceUtc = DateTimeOffset.Parse("2026-08-31T07:00:00Z"),
        ReasonCode = "idle_confirmed",
    };

    private static TaskAutoDispatchCandidateDecision DeferredDecision(
        string taskId,
        string code,
        DateTimeOffset? nextEligible) => new()
    {
        WorkspaceId = "ws",
        TaskId = taskId,
        Verdict = TaskAutoDispatchCandidateVerdict.Deferred,
        Code = code,
        EvaluatedAtUtc = DateTimeOffset.Parse("2026-08-31T08:00:00Z"),
        NextEligibleAtUtc = nextEligible,
        ScoreBreakdown = new TaskSchedulerScoreBreakdown { Total = 42.5 },
    };

    private static TaskBacklogRefinementDecision Refinement(
        string taskId,
        TaskBacklogRefinementVerdict verdict,
        string code,
        string? agentId = null) => new()
    {
        WorkspaceId = "ws",
        TaskId = taskId,
        TaskVersion = 1,
        TaskType = "implementation",
        Verdict = verdict,
        Code = code,
        CompatibleAgentId = agentId,
        AgentRoutingFingerprint = agentId is null ? null : new string('a', 64),
    };

    private async Task AddTaskAsync(string taskId)
    {
        await using var db = await _factory.CreateDbContextAsync();
        db.WorkspaceTasks.Add(new WorkspaceTaskEntity
        {
            TaskId = taskId,
            WorkspaceId = "ws",
            Title = taskId,
            Status = WorkspaceTaskStatus.Ready,
            Priority = TaskPriority.P1,
            ExecutionWindow = TaskExecutionWindow.Anytime,
            TaskType = "implementation",
            AutoDispatchEnabled = true,
            SortOrder = 0,
            Version = 1,
            CreatedAtUtc = _now,
            UpdatedAtUtc = _now,
        });
        await db.SaveChangesAsync();
    }

    private async Task<int> CountRowsAsync(string? whereClause = null)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var sql = "SELECT COUNT(*) AS Value FROM task_scheduler_decisions";
        if (whereClause is not null)
            sql += $" WHERE {whereClause}";
        var rows = await db.Database
            .SqlQueryRaw<int>($"{sql}")
            .ToListAsync();
        return rows.First();
    }

    private async Task<DateTimeOffset?> ReadNextEligibleAsync(string taskId)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var text = await db.Database
            .SqlQuery<string>($"SELECT next_eligible_at_utc AS Value FROM workspace_tasks WHERE workspace_id = {"ws"} AND task_id = {taskId}")
            .FirstOrDefaultAsync();
        return DateTimeOffset.TryParse(text, out var value) ? value : null;
    }
}
