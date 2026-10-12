using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Models;
using PuddingCode.Runtime;
using PuddingMemoryEngine.Data;
using PuddingMemoryEngine.Data;
using PuddingMemoryEngine.Entities;
using PuddingRuntime.Services;
using PuddingRuntime.Services.Messaging;

namespace PuddingMemoryEngineTests;

/// <summary>
/// R2 后半门禁（复核 §3 修改方案 2/3）：普通压缩必须拆成
/// 「短锁内冻结 → **锁外生成** → 短锁内重验/提交」，且提交走同一提交原语。
/// <list type="number">
/// <item>生成阶段不得持 <see cref="CompactionCoordinator"/> 锁（否则下一条消息的水合/硬保护会排队）；</item>
/// <item>生成期间代次/revision/覆盖状态被别的提交改变 ⇒ 提交必须拒绝且**零写入**；</item>
/// <item>后台压缩在提交前出现前台需求 ⇒ 让位给下一条消息，零写入。</item>
/// </list>
/// </summary>
[TestClass]
public sealed class ContextCompactionCommitRevalidationTests
{
    private const string SessionId = "session-r2b";
    private const string WorkspaceId = "workspace-r2b";

    /// <summary>生成阶段被屏障卡住时，压缩锁必须**已经释放**（下一条消息不被排队）。</summary>
    [TestMethod]
    public async Task GenerationPhase_DoesNotHoldTheCompactionLock()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = CreateOptions(connection);
        await using (var db = new MemoryDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            await SeedAsync(db, messageCount: 10);
        }

        var coordinator = new CompactionCoordinator();
        var generator = new BlockingSummaryGenerator();
        var service = CreateService(options, generator, coordinator);

        var compactTask = service.CompactAsync(CreateRequest());

        // 生成阶段已开始（说明冻结阶段已走完）。
        await generator.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsFalse(generator.Barrier.Task.IsCompleted, "前提：生成阶段仍被屏障卡住");

        // 关键断言：锁已释放 ⇒ 同一个 session 的另一把锁必须立刻拿到。
        var acquire = coordinator.AcquireAsync(SessionId);
        var acquired = await Task.WhenAny(acquire, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.AreSame(acquire, acquired, "生成阶段不得持有压缩锁（否则下一条消息会被排队）");
        await using (var lease = await acquire)
        {
            Assert.IsNotNull(lease);
        }

        generator.Barrier.SetResult();
        var result = await compactTask.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(ContextCompactionOutcome.Applied, result.Outcome);
    }

    /// <summary>生成期间代次被别的提交推进 ⇒ 提交拒绝、零写入、代次不被推进到下一值。</summary>
    [TestMethod]
    public async Task StaleGenerationAtCommit_WritesNothing_AndReportsStaleCandidate()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = CreateOptions(connection);
        await using (var db = new MemoryDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            await SeedAsync(db, messageCount: 10);
        }

        var coordinator = new CompactionCoordinator();
        var generator = new BlockingSummaryGenerator();
        var service = CreateService(options, generator, coordinator);

        var compactTask = service.CompactAsync(CreateRequest());
        await generator.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // 模拟「生成期间别的持久压缩提交」：把代次从 0 推到 1。
        await using (var bump = new MemoryDbContext(options))
        {
            var session = await bump.Sessions.SingleAsync(s => s.SessionId == SessionId);
            session.CompactionGeneration = 1;
            await bump.SaveChangesAsync();
        }

        generator.Barrier.SetResult();
        var result = await compactTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.AreEqual(ContextCompactionOutcome.SkippedStaleCandidate, result.Outcome,
            "代次已变 ⇒ 提交必须拒绝（零写入）");
        Assert.AreEqual(string.Empty, result.SummaryMessageId);

        await using var verify = new MemoryDbContext(options);
        Assert.AreEqual(0, await verify.Messages.CountAsync(m => m.SessionId == SessionId && m.ContentType == "compact_summary"),
            "拒绝时不得写摘要行");
        Assert.AreEqual(0, await verify.Messages.CountAsync(m => m.SessionId == SessionId && m.CompactedBy != null),
            "拒绝时不得打覆盖标注");
        Assert.AreEqual(0, await verify.CompactionCoverageManifests.CountAsync(m => m.SessionId == SessionId),
            "拒绝时不得写覆盖清单");
        Assert.AreEqual(1, (await verify.Sessions.SingleAsync(s => s.SessionId == SessionId)).CompactionGeneration,
            "拒绝时不得把代次推进到 2");
    }

    /// <summary>后台压缩在提交前出现前台需求 ⇒ 零写入让位（不把可选维护变成阻塞）。</summary>
    [TestMethod]
    public async Task BackgroundCompaction_DefersWhenForegroundDemandAppears()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = CreateOptions(connection);
        await using (var db = new MemoryDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            await SeedAsync(db, messageCount: 10);
        }

        var coordinator = new CompactionCoordinator();
        var admission = new AgentExecutionAdmissionCoordinator();
        var generator = new BlockingSummaryGenerator();
        var service = CreateService(options, generator, coordinator, admission);

        var compactTask = service.CompactAsync(CreateRequest() with { IsBackground = true });
        await generator.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // 生成期间新消息受理 ⇒ 同会话前台需求。
        admission.ReserveSessionForeground(WorkspaceId, SessionId, TimeSpan.FromMinutes(1));

        generator.Barrier.SetResult();
        var result = await compactTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.AreEqual(ContextCompactionOutcome.SkippedForegroundDemand, result.Outcome,
            "后台压缩必须让位给下一条消息");

        await using var verify = new MemoryDbContext(options);
        Assert.AreEqual(0, await verify.Messages.CountAsync(m => m.SessionId == SessionId && m.ContentType == "compact_summary"));
        Assert.AreEqual(0, await verify.Messages.CountAsync(m => m.SessionId == SessionId && m.CompactedBy != null));
        Assert.AreEqual(0, (await verify.Sessions.SingleAsync(s => s.SessionId == SessionId)).CompactionGeneration);
    }

    /// <summary>对照：没有任何并发变化 ⇒ 仍然成功应用（证明上面的拒绝不是"一律拒绝"）。</summary>
    [TestMethod]
    public async Task UnchangedPreconditions_StillApplies()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = CreateOptions(connection);
        await using (var db = new MemoryDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            await SeedAsync(db, messageCount: 10);
        }

        var coordinator = new CompactionCoordinator();
        var generator = new BlockingSummaryGenerator();
        var service = CreateService(options, generator, coordinator);

        var compactTask = service.CompactAsync(CreateRequest());
        await generator.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        generator.Barrier.SetResult();
        var result = await compactTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.AreEqual(ContextCompactionOutcome.Applied, result.Outcome);
        await using var verify = new MemoryDbContext(options);
        Assert.AreEqual(1, (await verify.Sessions.SingleAsync(s => s.SessionId == SessionId)).CompactionGeneration);
        Assert.AreEqual(1, await verify.Messages.CountAsync(m => m.SessionId == SessionId && m.ContentType == "compact_summary"));
    }

    // ── 复核新发现的 P1：提交时必须**读库重算候选窗口**（内存副本不足为凭） ──────────
    // 生成阶段被屏障卡住期间修改数据库，释放屏障后必须拒绝且零写入。

    /// <summary>正文被改写 ⇒ 拒绝。</summary>
    [TestMethod]
    public async Task CandidateContentMutatedDuringGeneration_IsRejected()
        => await AssertMutationDuringGenerationIsRejectedAsync(async db =>
        {
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE Messages SET Content = 'tampered content' WHERE SessionId = {0} AND MessageId = 'msg-1'",
                SessionId);
        });

    /// <summary>候选行被删除 ⇒ 拒绝。</summary>
    [TestMethod]
    public async Task CandidateRowDeletedDuringGeneration_IsRejected()
        => await AssertMutationDuringGenerationIsRejectedAsync(async db =>
        {
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM Messages WHERE SessionId = {0} AND MessageId = 'msg-1'",
                SessionId);
        });

    /// <summary>追加新消息 ⇒ 候选窗口变化 ⇒ 拒绝。</summary>
    [TestMethod]
    public async Task MessageAppendedDuringGeneration_IsRejected()
        => await AssertMutationDuringGenerationIsRejectedAsync(async db =>
        {
            db.Messages.Add(new MessageEntity
            {
                MessageId = "msg-appended",
                SessionId = SessionId,
                Sequence = 99,
                Role = "user",
                ContentType = "text",
                Content = "appended during generation",
                CreatedAt = 99,
            });
            await db.SaveChangesAsync();
        });

    /// <summary>角色/顺序被改（工具调用参数等载荷变化走同一条内容 hash 判据）⇒ 拒绝。</summary>
    [TestMethod]
    public async Task CandidateRoleOrOrderChangedDuringGeneration_IsRejected()
        => await AssertMutationDuringGenerationIsRejectedAsync(async db =>
        {
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE Messages SET Role = 'system' WHERE SessionId = {0} AND MessageId = 'msg-2'",
                SessionId);
        });

    /// <summary>**保留区间**的正文被改写也必须拒绝（复核要求覆盖「候选行及保留区间」）。</summary>
    [TestMethod]
    public async Task RetainedTailMutatedDuringGeneration_IsRejected()
        => await AssertMutationDuringGenerationIsRejectedAsync(async db =>
        {
            // msg-10 属于保留尾部（RecentMessagesToKeep = 6），不在被覆盖跨度内。
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE Messages SET Content = 'retained tampered' WHERE SessionId = {0} AND MessageId = 'msg-10'",
                SessionId);
        });

    private static async Task AssertMutationDuringGenerationIsRejectedAsync(
        Func<MemoryDbContext, Task> mutate)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = CreateOptions(connection);
        await using (var db = new MemoryDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            await SeedAsync(db, messageCount: 10);
        }

        var coordinator = new CompactionCoordinator();
        var generator = new BlockingSummaryGenerator();
        var service = CreateService(options, generator, coordinator);

        var compactTask = service.CompactAsync(CreateRequest());
        await generator.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // 生成期间改动数据库（模拟别的写者/工具回执落地）。
        await using (var mutator = new MemoryDbContext(options))
        {
            await mutate(mutator);
        }

        generator.Barrier.SetResult();
        var result = await compactTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.AreEqual(
            ContextCompactionOutcome.SkippedStaleCandidate,
            result.Outcome,
            "生成期间数据库已变 ⇒ 提交必须零写入拒绝");
        Assert.AreEqual(string.Empty, result.SummaryMessageId);

        await using var verify = new MemoryDbContext(options);
        Assert.AreEqual(
            0,
            await verify.Messages.CountAsync(m => m.SessionId == SessionId && m.ContentType == "compact_summary"),
            "拒绝时不得写摘要行");
        Assert.AreEqual(
            0,
            await verify.Messages.CountAsync(m => m.SessionId == SessionId && m.CompactedBy != null),
            "拒绝时不得打覆盖标注");
        Assert.AreEqual(
            0,
            await verify.CompactionCoverageManifests.CountAsync(m => m.SessionId == SessionId),
            "拒绝时不得写覆盖清单");
        Assert.AreEqual(
            0,
            (await verify.Sessions.SingleAsync(s => s.SessionId == SessionId)).CompactionGeneration,
            "拒绝时不得推进代次");
    }

    // ── 装配 ────────────────────────────────────────────────────────────────

    private static ContextCompactionRequest CreateRequest() => new(
        WorkspaceId: WorkspaceId,
        SessionId: SessionId,
        AgentId: "agent-1",
        Mode: ContextCompactionMode.Manual,
        Level: ContextCompactionLevel.Full,
        Reason: "r2b revalidation test");

    private static ContextCompactionService CreateService(
        DbContextOptions<MemoryDbContext> options,
        IContextCompactionSummaryGenerator generator,
        CompactionCoordinator coordinator,
        AgentExecutionAdmissionCoordinator? admission = null)
        => new(
            new TestFactory(options),
            generator,
            NullLogger<ContextCompactionService>.Instance,
            compactionCoordinator: coordinator,
            admissionCoordinator: admission);

    private static async Task SeedAsync(MemoryDbContext db, int messageCount)
    {
        db.Sessions.Add(new SessionEntity
        {
            SessionId = SessionId,
            WorkspaceId = WorkspaceId,
            AgentId = "agent-1",
            Status = "Active",
            CreatedAt = 1,
            LastActivityAt = 1,
        });

        for (var i = 1; i <= messageCount; i++)
        {
            db.Messages.Add(new MessageEntity
            {
                MessageId = $"msg-{i}",
                SessionId = SessionId,
                Sequence = i,
                Role = i % 2 == 0 ? "agent" : "user",
                ContentType = "text",
                Content = $"message {i}",
                CreatedAt = i,
            });
        }

        await db.SaveChangesAsync();
    }

    private static DbContextOptions<MemoryDbContext> CreateOptions(SqliteConnection connection) =>
        new DbContextOptionsBuilder<MemoryDbContext>().UseSqlite(connection).Options;

    private sealed class TestFactory(DbContextOptions<MemoryDbContext> options) : IDbContextFactory<MemoryDbContext>
    {
        public MemoryDbContext CreateDbContext() => new(options);

        public Task<MemoryDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    /// <summary>生成阶段可被人为屏障卡住的摘要生成器（用于证明锁已释放 / 注入并发变化）。</summary>
    private sealed class BlockingSummaryGenerator : IContextCompactionSummaryGenerator
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Barrier { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<string> GenerateSummaryAsync(
            ContextCompactionSummaryRequest request,
            CancellationToken ct = default)
        {
            Started.TrySetResult();
            await Barrier.Task.WaitAsync(ct);
            return "## 用户目标\n- r2b：生成阶段不持锁，提交前重验。";
        }
    }
}
