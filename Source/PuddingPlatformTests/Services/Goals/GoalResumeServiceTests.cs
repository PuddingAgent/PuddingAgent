using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PuddingCode.Goals;
using PuddingCode.Platform;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services.Goals;

namespace PuddingPlatformTests.Services.Goals;

/// <summary>
/// 「Agent 自主恢复 Goal」端口（GoalResumeService）聚焦测试：
/// 覆盖 成功恢复 / already_active 幂等 / 终态拒绝（cancelled + budget_exhausted）/
/// 他人 Goal 拒绝 / 熔断无证据拒绝 / 熔断带证据成功 / 同一 activation epoch 自动恢复上限 /
/// GoalRunId 定位的 not_found 折叠与归属拦截 / CAS 版本冲突。
/// 复用 GoalCommandServiceTests 的 SQLite in-memory 基础设施与 canonical 命令路径造数。
/// </summary>
[TestClass]
public sealed class GoalResumeServiceTests
{
    private readonly List<IAsyncDisposable> _resources = [];

    [TestCleanup]
    public async Task CleanupAsync()
    {
        foreach (var resource in _resources.AsEnumerable().Reverse())
            await resource.DisposeAsync();
    }

    private sealed class NoopSignal : ICommittedEventSignal
    {
        public ValueTask WaitForChangeAsync(string conversationId, long knownHead, CancellationToken ct)
            => ValueTask.FromCanceled(ct);

        public void Signal(string conversationId, long committedSequence)
        {
        }
    }

    private async Task<(PlatformDbContext Db, GoalResumeService Service, GoalCommandService Commands)>
        CreateAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        _resources.Add(connection);
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite(connection)
            .Options;
        var db = new PlatformDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var store = new GoalRunStore(db, new NoopSignal(), NullLogger<GoalRunStore>.Instance);
        var commands = new GoalCommandService(
            store,
            Options.Create(new GoalRunOptions { Enabled = true, ContinuationEnabled = false }),
            TimeProvider.System,
            NullLogger<GoalCommandService>.Instance);
        // Exercise the product lifetimes, including strict scope validation.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ICommittedEventSignal, NoopSignal>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(Options.Create(new GoalRunOptions { Enabled = true, ContinuationEnabled = false }));
        services.AddScoped(_ => new PlatformDbContext(options));
        services.AddScoped<GoalRunStore>();
        services.AddScoped<IGoalCommandService, GoalCommandService>();
        services.AddSingleton<GoalResumeService>();
        var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
        _resources.Add(provider);
        var service = provider.GetRequiredService<GoalResumeService>();
        return (db, service, commands);
    }

    private static async Task<GoalCommandResult> SetAsync(
        GoalCommandService commands,
        string conversationId = "conv-1",
        string agentInstanceId = "agent-1",
        string clientRequestId = "req-set-1")
        => await commands.ExecuteAsync(
            new GoalCommandRequest(
                "ws", conversationId, agentInstanceId, "admin", clientRequestId,
                new GoalCommand { Kind = GoalCommandKind.Set, Objective = "修复全部失败测试" }),
            CancellationToken.None);

    private static async Task<GoalCommandResult> PauseAsync(
        GoalCommandService commands,
        string conversationId = "conv-1",
        string agentInstanceId = "agent-1",
        string clientRequestId = "req-pause-1")
        => await commands.ExecuteAsync(
            new GoalCommandRequest(
                "ws", conversationId, agentInstanceId, "admin", clientRequestId,
                new GoalCommand { Kind = GoalCommandKind.Pause }),
            CancellationToken.None);

    private static GoalResumeRequest ResumeRequest(
        string conversationId = "conv-1",
        string agentInstanceId = "agent-1",
        string? goalRunId = null,
        int? expectedVersion = null,
        IReadOnlyList<string>? evidenceRefs = null,
        string? reason = null)
        => new()
        {
            WorkspaceId = "ws",
            ConversationId = conversationId,
            AgentInstanceId = agentInstanceId,
            GoalRunId = goalRunId,
            ExpectedVersion = expectedVersion,
            Reason = reason,
            EvidenceRefs = evidenceRefs,
        };

    /// <summary>测试专用：直改 goal 状态（模拟结算置 Blocked / 终态），保持其余字段。</summary>
    private static async Task MutateGoalAsync(
        PlatformDbContext db,
        string goalRunId,
        Action<GoalRunEntity> mutate)
    {
        var goal = await db.GoalRuns.SingleAsync(g => g.GoalRunId == goalRunId);
        mutate(goal);
        await db.SaveChangesAsync();
    }

    // ── ① 成功 resume ──────────────────────────────────────────────────────

    [TestMethod]
    public async Task Resume_PausedGoal_Succeeds_WithEpochAdvance()
    {
        var (db, service, commands) = await CreateAsync();
        await using var _ = db;

        var set = await SetAsync(commands);
        Assert.IsTrue(set.Success);
        await PauseAsync(commands);

        var result = await service.ResumeAsync(ResumeRequest(reason: "阻塞已解除"), CancellationToken.None);

        Assert.IsTrue(result.Success, result.Message);
        Assert.IsTrue(result.Resumed);
        Assert.AreEqual(GoalResumeCodes.Resumed, result.Code);
        Assert.AreEqual(GoalPhase.Active, result.Phase);
        Assert.IsNull(result.BlockedCode);
        // set(e1) → pause(e2) → resume(e3)，resume 不重置已消费额度。
        Assert.AreEqual(3, result.ActivationEpoch);
        Assert.AreEqual(set.Snapshot!.IterationsStarted, result.IterationsStarted);
        Assert.AreEqual(1, await db.ConversationEvents.CountAsync(item => item.Type == GoalEventTypes.Resumed));
    }

    // ── ② already_active 幂等 ─────────────────────────────────────────────

    [TestMethod]
    public async Task Resume_AlreadyActive_ReturnsIdempotentSuccess_WithoutAnyWrite()
    {
        var (db, service, commands) = await CreateAsync();
        await using var _ = db;

        var set = await SetAsync(commands);
        Assert.IsTrue(set.Success);

        var result = await service.ResumeAsync(ResumeRequest(), CancellationToken.None);

        Assert.IsTrue(result.Success);
        Assert.IsFalse(result.Resumed);
        Assert.AreEqual(GoalResumeCodes.AlreadyActive, result.Code);
        Assert.AreEqual(GoalPhase.Active, result.Phase);
        Assert.AreEqual(set.Snapshot!.ActivationEpoch, result.ActivationEpoch);
        Assert.AreEqual(0, await db.ConversationEvents.CountAsync(item => item.Type == GoalEventTypes.Resumed));
    }

    // ── ③ 终态拒绝（cancelled / budget_exhausted）─────────────────────────

    [TestMethod]
    public async Task Resume_CancelledTerminalGoal_Rejected()
    {
        var (db, service, commands) = await CreateAsync();
        await using var _ = db;

        var set = await SetAsync(commands);
        var cancel = await commands.ExecuteAsync(
            new GoalCommandRequest("ws", "conv-1", "agent-1", "admin", "req-cancel-1",
                new GoalCommand { Kind = GoalCommandKind.Cancel }),
            CancellationToken.None);
        Assert.IsTrue(cancel.Success);

        var result = await service.ResumeAsync(ResumeRequest(), CancellationToken.None);

        Assert.IsFalse(result.Success);
        Assert.AreEqual(GoalResumeCodes.NotResumable, result.Code);
        Assert.AreEqual(GoalPhase.Cancelled, result.Phase);
        Assert.AreEqual(0, await db.ConversationEvents.CountAsync(item => item.Type == GoalEventTypes.Resumed));
    }

    [TestMethod]
    public async Task Resume_BudgetExhaustedTerminalGoal_Rejected()
    {
        var (db, service, commands) = await CreateAsync();
        await using var _ = db;

        var set = await SetAsync(commands);
        await MutateGoalAsync(db, set.Snapshot!.GoalRunId, goal =>
        {
            goal.Status = GoalPhase.BudgetExhausted;
            goal.TerminalAtUtc = DateTimeOffset.UtcNow;
        });

        var result = await service.ResumeAsync(ResumeRequest(), CancellationToken.None);

        Assert.IsFalse(result.Success);
        Assert.AreEqual(GoalResumeCodes.NotResumable, result.Code);
        Assert.AreEqual(GoalPhase.BudgetExhausted, result.Phase);
        // 终态零写入：状态保持 BudgetExhausted。
        Assert.AreEqual(
            GoalPhase.BudgetExhausted,
            (await db.GoalRuns.SingleAsync(g => g.GoalRunId == set.Snapshot.GoalRunId)).Status);
    }

    // ── ④ 非本人 Goal 拒绝 ─────────────────────────────────────────────────

    [TestMethod]
    public async Task Resume_OtherAgentsGoal_HeldByOtherAgent()
    {
        var (db, service, commands) = await CreateAsync();
        await using var _ = db;

        var set = await SetAsync(commands); // agent-1 的 goal
        await PauseAsync(commands);

        // agent-2 在同一会话发起：FindActive(agent-2) 为空 → FindLatest 命中 agent-1 的 goal → 归属拦截。
        var result = await service.ResumeAsync(
            ResumeRequest(agentInstanceId: "agent-2"), CancellationToken.None);

        Assert.IsFalse(result.Success);
        Assert.AreEqual(GoalResumeCodes.HeldByOtherAgent, result.Code);
        // 快照回填当前事实（归属方的 goal），但不产生任何状态变更。
        Assert.AreEqual(set.Snapshot!.GoalRunId, result.GoalRunId);
        Assert.AreEqual(
            GoalPhase.Paused,
            (await db.GoalRuns.SingleAsync(g => g.GoalRunId == set.Snapshot.GoalRunId)).Status);
        Assert.AreEqual(0, await db.ConversationEvents.CountAsync(item => item.Type == GoalEventTypes.Resumed));
    }

    // ── ⑤/⑥ 熔断证据闸门 ──────────────────────────────────────────────────

    [TestMethod]
    public async Task Resume_CircuitBlocked_WithoutEvidence_Rejected()
    {
        var (db, service, commands) = await CreateAsync();
        await using var _ = db;

        var set = await SetAsync(commands);
        await MutateGoalAsync(db, set.Snapshot!.GoalRunId, goal =>
        {
            goal.Status = GoalPhase.Blocked;
            goal.BlockedCode = GoalResumeCodes.CircuitBlockerCode;
            goal.BlockedMessage = "Circuit breaker opened";
        });

        var result = await service.ResumeAsync(ResumeRequest(), CancellationToken.None);

        Assert.IsFalse(result.Success);
        Assert.AreEqual(GoalResumeCodes.CircuitEvidenceRequired, result.Code);
        Assert.AreEqual(GoalResumeCodes.CircuitBlockerCode, result.BlockedCode);
        Assert.AreEqual(
            GoalPhase.Blocked,
            (await db.GoalRuns.SingleAsync(g => g.GoalRunId == set.Snapshot.GoalRunId)).Status);
        Assert.AreEqual(0, await db.ConversationEvents.CountAsync(item => item.Type == GoalEventTypes.Resumed));
    }

    [TestMethod]
    public async Task Resume_CircuitBlocked_WithEvidence_Succeeds()
    {
        var (db, service, commands) = await CreateAsync();
        await using var _ = db;

        var set = await SetAsync(commands);
        await MutateGoalAsync(db, set.Snapshot!.GoalRunId, goal =>
        {
            goal.Status = GoalPhase.Blocked;
            goal.BlockedCode = GoalResumeCodes.CircuitBlockerCode;
            goal.BlockedMessage = "Circuit breaker opened";
        });

        var result = await service.ResumeAsync(
            ResumeRequest(evidenceRefs: ["artifact:manual-intervention-1"]), CancellationToken.None);

        Assert.IsTrue(result.Success, result.Message);
        Assert.IsTrue(result.Resumed);
        Assert.AreEqual(GoalResumeCodes.Resumed, result.Code);
        Assert.AreEqual(GoalPhase.Active, result.Phase);
        Assert.IsNull(result.BlockedCode);
    }

    // ── 同一 activation epoch 内自动恢复上限 1 次 ─────────────────────────

    [TestMethod]
    public async Task Resume_SecondAutoResumeInSameEpoch_Rejected_UntilEpochAdvances()
    {
        var (db, service, commands) = await CreateAsync();
        await using var _ = db;

        var set = await SetAsync(commands);
        var goalRunId = set.Snapshot!.GoalRunId;
        await PauseAsync(commands);

        // 第一次自动恢复：paused(e2) → active(e3)，台账记录 e3。
        var first = await service.ResumeAsync(ResumeRequest(), CancellationToken.None);
        Assert.IsTrue(first.Success, first.Message);
        Assert.AreEqual(3, first.ActivationEpoch);

        // 模拟回退到可恢复状态但 epoch 未推进（activation_epoch 仍为 3）：
        // 使用非熔断阻塞码，绕开熔断证据闸门，单测 epoch 配额闸门。
        await MutateGoalAsync(db, goalRunId, goal =>
        {
            goal.Status = GoalPhase.Blocked;
            goal.BlockedCode = "needs_user";
            goal.BlockedMessage = "blocked again without epoch advance";
        });

        var second = await service.ResumeAsync(ResumeRequest(), CancellationToken.None);
        Assert.IsFalse(second.Success);
        Assert.AreEqual(GoalResumeCodes.ResumeEpochLimit, second.Code);
        Assert.AreEqual(3, second.ActivationEpoch);

        // epoch 推进（人工介入/重启换发 fence 的等价效果）后配额重置。
        await MutateGoalAsync(db, goalRunId, goal => goal.ActivationEpoch = 4);

        var third = await service.ResumeAsync(ResumeRequest(), CancellationToken.None);
        Assert.IsTrue(third.Success, third.Message);
        Assert.IsTrue(third.Resumed);
        Assert.AreEqual(GoalResumeCodes.Resumed, third.Code);
        Assert.AreEqual(5, third.ActivationEpoch);
    }

    // ── GoalRunId 定位：not_found 折叠与归属拦截 ──────────────────────────

    [TestMethod]
    public async Task Resume_UnknownGoalRunId_ReturnsNotFound()
    {
        var (db, service, commands) = await CreateAsync();
        await using var _ = db;

        var result = await service.ResumeAsync(
            ResumeRequest(goalRunId: "nonexistent-goal"), CancellationToken.None);

        Assert.IsFalse(result.Success);
        Assert.AreEqual(GoalResumeCodes.NotFound, result.Code);
    }

    [TestMethod]
    public async Task Resume_GoalRunIdInOtherConversation_FoldedToNotFound()
    {
        var (db, service, commands) = await CreateAsync();
        await using var _ = db;

        var set = await SetAsync(commands);
        var goalRunId = set.Snapshot!.GoalRunId;

        // goal 存在，但声明会话与 goal.CurrentConversationId 不一致 → 不泄露存在性。
        var result = await service.ResumeAsync(
            ResumeRequest(conversationId: "conv-other", goalRunId: goalRunId), CancellationToken.None);

        Assert.IsFalse(result.Success);
        Assert.AreEqual(GoalResumeCodes.NotFound, result.Code);
    }

    [TestMethod]
    public async Task Resume_GoalRunIdOwnedByOtherAgent_HeldByOtherAgent()
    {
        var (db, service, commands) = await CreateAsync();
        await using var _ = db;

        var set = await SetAsync(commands); // agent-1 的 goal
        var goalRunId = set.Snapshot!.GoalRunId;
        await PauseAsync(commands);

        var result = await service.ResumeAsync(
            ResumeRequest(agentInstanceId: "agent-2", goalRunId: goalRunId), CancellationToken.None);

        Assert.IsFalse(result.Success);
        Assert.AreEqual(GoalResumeCodes.HeldByOtherAgent, result.Code);
        Assert.AreEqual(
            GoalPhase.Paused,
            (await db.GoalRuns.SingleAsync(g => g.GoalRunId == goalRunId)).Status);
    }

    // ── CAS 版本冲突 ───────────────────────────────────────────────────────

    [TestMethod]
    public async Task Resume_WithStaleExpectedVersion_ReturnsVersionConflict()
    {
        var (db, service, commands) = await CreateAsync();
        await using var _ = db;

        var set = await SetAsync(commands);
        var goalRunId = set.Snapshot!.GoalRunId;
        await PauseAsync(commands);

        var result = await service.ResumeAsync(
            ResumeRequest(expectedVersion: 999), CancellationToken.None);

        Assert.IsFalse(result.Success);
        Assert.AreEqual(GoalResumeCodes.VersionConflict, result.Code);
        // CAS 失败零写入：状态保持 paused。
        var goal = await db.GoalRuns.SingleAsync(g => g.GoalRunId == goalRunId);
        Assert.AreEqual(GoalPhase.Paused, goal.Status);
        Assert.AreEqual(0, await db.ConversationEvents.CountAsync(item => item.Type == GoalEventTypes.Resumed));
    }
}
