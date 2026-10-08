using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Models;
using PuddingCode.Runtime;
using PuddingMemoryEngine.Data;
using PuddingMemoryEngine.Entities;
using PuddingRuntime.Services;
using PuddingRuntime.Services.AgentLoop;

namespace PuddingRuntimeTests.Services;

/// <summary>
/// F2 持久化尾款主体：warm-prefix checkpoint 的**事务落库**（摘要行 + 精确到行的 CompactedBy +
/// 覆盖清单 + 代际自增），以及不满足前置条件时「一律不写、绝不部分标注」。
/// <para>
/// 另含重启恢复证据：新 manager + 新 DbContext 在同一库上水合，必须看到摘要、且看不到被覆盖的行。
/// </para>
/// </summary>
[TestClass]
public sealed class WarmPrefixCheckpointCommitTests
{
    private const string SessionId = "session-warm-prefix-commit";
    private const string Summary = "## warm prefix summary\ncovered span digest";

    [TestMethod]
    public async Task Commit_WritesSummaryCoverageManifestAndGeneration_InOneTransaction()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = CreateOptions(connection);
        await using (var seed = new MemoryDbContext(options))
        {
            await seed.Database.EnsureCreatedAsync();
            await SeedAsync(seed, 6);
        }

        var service = CreateService(options);
        var result = await service.CommitWarmPrefixCheckpointAsync(CreateRequest(
            expectedGeneration: 0,
            coveredMessageIds: ["m1", "m2", "m3"]));

        Assert.AreEqual(WarmPrefixCheckpointCommitStatus.Committed, result.Status);
        Assert.IsTrue(result.Committed);
        Assert.AreEqual(3, result.CoveredCount);
        Assert.AreEqual(1, result.TargetGeneration, "代际必须 +1");
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.SummaryMessageId));

        await using var verify = new MemoryDbContext(options);
        var summary = await verify.Messages.SingleAsync(m => m.MessageId == result.SummaryMessageId);
        Assert.AreEqual("compact_summary", summary.ContentType);
        Assert.AreEqual("warm_prefix_checkpoint", summary.Source);
        Assert.AreEqual(Summary, summary.Content);

        var covered = await verify.Messages
            .Where(m => m.SessionId == SessionId && m.CompactedBy != null)
            .Select(m => m.MessageId)
            .OrderBy(id => id)
            .ToListAsync();
        CollectionAssert.AreEqual(new[] { "m1", "m2", "m3" }, covered, "只有被覆盖的三行可被打标");

        var untouched = await verify.Messages.SingleAsync(m => m.MessageId == "m4");
        Assert.IsNull(untouched.CompactedBy, "跨度之外的行不得被打标");

        var manifest = await verify.CompactionCoverageManifests.SingleAsync(m => m.SessionId == SessionId);
        Assert.AreEqual("warm_prefix_checkpoint", manifest.Generator);
        Assert.AreEqual(3, manifest.CoveredCount);
        Assert.AreEqual(0, manifest.OmittedCount);
        Assert.AreEqual(0, manifest.SourceGeneration);
        Assert.AreEqual(1, manifest.TargetGeneration);
        Assert.AreEqual(result.SummaryMessageId, manifest.FinalSummaryId);

        var session = await verify.Sessions.SingleAsync(s => s.SessionId == SessionId);
        Assert.AreEqual(1, session.CompactionGeneration);
    }

    /// <summary>代际 CAS 失败 ⇒ 一律不写（不得用旧候选覆盖新的持久事实）。</summary>
    [TestMethod]
    public async Task Commit_WithStaleGeneration_WritesNothing()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = CreateOptions(connection);
        await using (var seed = new MemoryDbContext(options))
        {
            await seed.Database.EnsureCreatedAsync();
            await SeedAsync(seed, 6);
        }

        var service = CreateService(options);
        var result = await service.CommitWarmPrefixCheckpointAsync(CreateRequest(
            expectedGeneration: 7,
            coveredMessageIds: ["m1", "m2"]));

        Assert.AreEqual(WarmPrefixCheckpointCommitStatus.GenerationMismatch, result.Status);
        Assert.AreEqual(0, result.TargetGeneration, "应回传**当前**代际供调用方重取候选");

        await using var verify = new MemoryDbContext(options);
        Assert.AreEqual(0, await verify.Messages.CountAsync(m => m.ContentType == "compact_summary"));
        Assert.AreEqual(0, await verify.Messages.CountAsync(m => m.CompactedBy != null));
        Assert.AreEqual(0, await verify.CompactionCoverageManifests.CountAsync());
        Assert.AreEqual(0, (await verify.Sessions.SingleAsync(s => s.SessionId == SessionId)).CompactionGeneration);
    }

    /// <summary>有 id 找不到 ⇒ **整批**拒绝：绝不给「找得到的那部分」打标（否则跨度与摘要脱节）。</summary>
    [TestMethod]
    public async Task Commit_WithMissingMessageId_MarksNothing()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = CreateOptions(connection);
        await using (var seed = new MemoryDbContext(options))
        {
            await seed.Database.EnsureCreatedAsync();
            await SeedAsync(seed, 6);
        }

        var service = CreateService(options);
        var result = await service.CommitWarmPrefixCheckpointAsync(CreateRequest(
            expectedGeneration: 0,
            coveredMessageIds: ["m1", "m2", "does-not-exist"]));

        Assert.AreEqual(WarmPrefixCheckpointCommitStatus.CoveredMessagesNotFound, result.Status);
        CollectionAssert.AreEqual(new[] { "does-not-exist" }, result.ProblemMessageIds!.ToArray());

        await using var verify = new MemoryDbContext(options);
        Assert.AreEqual(0, await verify.Messages.CountAsync(m => m.CompactedBy != null), "m1/m2 也不得被打标");
        Assert.AreEqual(0, await verify.Messages.CountAsync(m => m.ContentType == "compact_summary"));
        Assert.AreEqual(0, (await verify.Sessions.SingleAsync(s => s.SessionId == SessionId)).CompactionGeneration);
    }

    /// <summary>已被别的摘要覆盖 ⇒ 拒绝（合并会串链）。</summary>
    [TestMethod]
    public async Task Commit_WithAlreadyCoveredMessage_IsRejected()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = CreateOptions(connection);
        await using (var seed = new MemoryDbContext(options))
        {
            await seed.Database.EnsureCreatedAsync();
            await SeedAsync(seed, 6);
            // CompactedBy 有 FK 约束 ⇒ 必须指向真实的摘要行。
            seed.Messages.Add(new MessageEntity
            {
                MessageId = "previous-summary",
                SessionId = SessionId,
                Sequence = 7,
                Role = "system",
                ContentType = "compact_summary",
                Content = "older summary",
                CreatedAt = 7,
            });
            var row = await seed.Messages.SingleAsync(m => m.MessageId == "m2");
            row.CompactedBy = "previous-summary";
            await seed.SaveChangesAsync();
        }

        var service = CreateService(options);
        var result = await service.CommitWarmPrefixCheckpointAsync(CreateRequest(
            expectedGeneration: 0,
            coveredMessageIds: ["m1", "m2"]));

        Assert.AreEqual(WarmPrefixCheckpointCommitStatus.AlreadyCovered, result.Status);
        CollectionAssert.AreEqual(new[] { "m2" }, result.ProblemMessageIds!.ToArray());

        await using var verify = new MemoryDbContext(options);
        Assert.IsNull((await verify.Messages.SingleAsync(m => m.MessageId == "m1")).CompactedBy);
        Assert.AreEqual(
            1,
            await verify.Messages.CountAsync(m => m.ContentType == "compact_summary"),
            "不得写入新的摘要行（只剩种子里那条 previous-summary）");
        Assert.AreEqual(0, await verify.CompactionCoverageManifests.CountAsync(), "不得写入覆盖清单");
        Assert.AreEqual(0, (await verify.Sessions.SingleAsync(s => s.SessionId == SessionId)).CompactionGeneration);
    }

    [TestMethod]
    public async Task Commit_WithEmptyCoveredSet_WritesNothing()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = CreateOptions(connection);
        await using (var seed = new MemoryDbContext(options))
        {
            await seed.Database.EnsureCreatedAsync();
            await SeedAsync(seed, 3);
        }

        var service = CreateService(options);
        var result = await service.CommitWarmPrefixCheckpointAsync(CreateRequest(
            expectedGeneration: 0,
            coveredMessageIds: []));

        Assert.AreEqual(WarmPrefixCheckpointCommitStatus.CoveredMessagesNotFound, result.Status);
        await using var verify = new MemoryDbContext(options);
        Assert.AreEqual(0, await verify.Messages.CountAsync(m => m.ContentType == "compact_summary"));
    }

    [TestMethod]
    public async Task Commit_ForUnknownSession_IsRejected()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = CreateOptions(connection);
        await using (var seed = new MemoryDbContext(options))
        {
            await seed.Database.EnsureCreatedAsync();
        }

        var service = CreateService(options);
        var result = await service.CommitWarmPrefixCheckpointAsync(CreateRequest(
            expectedGeneration: 0,
            coveredMessageIds: ["m1"]));

        Assert.AreEqual(WarmPrefixCheckpointCommitStatus.SessionNotFound, result.Status);
    }

    /// <summary>
    /// **重启恢复**：提交后用全新的 manager + 新 DbContext 在同一库上水合 ——
    /// 必须看到摘要（作为 compact_summary 行进入历史），且被覆盖的行不再出现。
    /// 这是 F2「重启后不丢压缩成果」的直接证据。
    /// </summary>
    [TestMethod]
    public async Task CommittedCheckpoint_SurvivesRestart_AndHydratesAsCompactedHistory()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = CreateOptions(connection);
        await using (var seed = new MemoryDbContext(options))
        {
            await seed.Database.EnsureCreatedAsync();
            await SeedAsync(seed, 6);
        }

        var service = CreateService(options);
        var commit = await service.CommitWarmPrefixCheckpointAsync(CreateRequest(
            expectedGeneration: 0,
            coveredMessageIds: ["m1", "m2", "m3", "m4"]));
        Assert.AreEqual(WarmPrefixCheckpointCommitStatus.Committed, commit.Status);

        // 「重启」：全新的 manager（无内存历史）+ 新 DbContext，同一个库。
        var restarted = CreateManager(options);
        var hydrated = await restarted.BuildContextFromDbAsync(SessionId, 100_000, CancellationToken.None);
        var contents = hydrated.Select(m => m.Content ?? string.Empty).ToList();

        Assert.IsTrue(contents.Any(c => c == Summary), "重启后必须水合出摘要");
        foreach (var coveredId in new[] { "m1", "m2", "m3", "m4" })
            Assert.IsFalse(contents.Any(c => c.Contains(coveredId, StringComparison.Ordinal)),
                $"被覆盖的 {coveredId} 不应再进入上下文");
        Assert.IsTrue(contents.Any(c => c.Contains("m5", StringComparison.Ordinal)), "跨度之后的行必须保留");
    }

    /// <summary>事务原子性（故障注入）：写摘要行时抛错 ⇒ 覆盖标注/清单/代际全部零残留。</summary>
    [TestMethod]
    public async Task Commit_WhenSaveFails_LeavesZeroPartialWrites()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var seedOptions = CreateOptions(connection);
        await using (var seed = new MemoryDbContext(seedOptions))
        {
            await seed.Database.EnsureCreatedAsync();
            await SeedAsync(seed, 6);
        }

        var service = CreateService(CreateFaultingOptions(connection));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            service.CommitWarmPrefixCheckpointAsync(CreateRequest(
                expectedGeneration: 0,
                coveredMessageIds: ["m1", "m2", "m3"])));

        await using var verify = new MemoryDbContext(seedOptions);
        Assert.AreEqual(0, await verify.Messages.CountAsync(m => m.ContentType == "compact_summary"),
            "摘要行必须随事务回滚");
        Assert.AreEqual(0, await verify.Messages.CountAsync(m => m.CompactedBy != null),
            "覆盖标注必须随事务回滚");
        Assert.AreEqual(0, await verify.CompactionCoverageManifests.CountAsync(), "覆盖清单必须随事务回滚");
        Assert.AreEqual(0, (await verify.Sessions.SingleAsync(s => s.SessionId == SessionId)).CompactionGeneration,
            "代际必须随事务回滚");
    }

    private static WarmPrefixCheckpointCommitRequest CreateRequest(
        int expectedGeneration,
        IReadOnlyList<string> coveredMessageIds) =>
        new(
            SessionId,
            WorkspaceId: "workspace-1",
            AgentId: "agent-1",
            CompactionId: "warm-prefix-test-1",
            ExpectedSourceGeneration: expectedGeneration,
            CoveredMessageIds: coveredMessageIds,
            Summary: Summary,
            MessagesBefore: 6,
            MessagesAfter: 3,
            TokensBefore: 90_000,
            MeasuredTokensAfter: 40_000,
            Reason: "warm_prefix_hard_protection");

    private static ContextCompactionService CreateService(DbContextOptions<MemoryDbContext> options)
        => new(
            new TestFactory(options),
            new FixedSummaryGenerator(Summary),
            NullLogger<ContextCompactionService>.Instance,
            contentSummaryService: null,
            dataPaths: null);

    private static ContextWindowManager CreateManager(DbContextOptions<MemoryDbContext> options)
    {
        var factory = new TestFactory(options);
        return new ContextWindowManager(
            new AgentSessionManager(NullLogger<AgentSessionManager>.Instance),
            new InMemoryRuntimeSessionStore(),
            new ExecutionControlRegistry(),
            new ExecutionJournal(),
            NullLogger<ContextWindowManager>.Instance,
            memoryDbFactory: factory,
            canonicalMessageStore: new EmptyCanonicalStore());
    }

    private static DbContextOptions<MemoryDbContext> CreateOptions(SqliteConnection connection) =>
        new DbContextOptionsBuilder<MemoryDbContext>()
            .UseSqlite(connection)
            .Options;

    /// <summary>故障注入：暂存 compact_summary 行时抛错，验证单事务回滚。</summary>
    private static DbContextOptions<MemoryDbContext> CreateFaultingOptions(SqliteConnection connection) =>
        new DbContextOptionsBuilder<MemoryDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(new CompactSummaryFaultInterceptor())
            .Options;

    private static async Task SeedAsync(MemoryDbContext db, int messageCount)
    {
        db.Sessions.Add(new SessionEntity
        {
            SessionId = SessionId,
            WorkspaceId = "workspace-1",
            AgentId = "agent-1",
            Status = "Active",
            CreatedAt = 1,
            LastActivityAt = 1,
        });

        for (var i = 1; i <= messageCount; i++)
        {
            db.Messages.Add(new MessageEntity
            {
                MessageId = $"m{i}",
                SessionId = SessionId,
                Sequence = i,
                Role = i % 2 == 1 ? "user" : "agent",
                ContentType = "text",
                Content = $"message m{i}",
                CreatedAt = i,
            });
        }

        await db.SaveChangesAsync();
    }

    private sealed class TestFactory(DbContextOptions<MemoryDbContext> options) : IDbContextFactory<MemoryDbContext>
    {
        public MemoryDbContext CreateDbContext() => new(options);
    }

    /// <summary>提交点故障注入：暂存 compact_summary 行时抛错（与既有回滚测试同一手法）。</summary>
    private sealed class CompactSummaryFaultInterceptor : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
    {
        public override Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> SavingChanges(
            Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result)
        {
            ThrowIfCompactSummaryStaged(eventData.Context);
            return base.SavingChanges(eventData, result);
        }

        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> SavingChangesAsync(
            Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            ThrowIfCompactSummaryStaged(eventData.Context);
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }

        private static void ThrowIfCompactSummaryStaged(DbContext? context)
        {
            if (context is null)
                return;

            var staging = context.ChangeTracker.Entries<MessageEntity>()
                .Any(entry => entry.State == EntityState.Added
                    && string.Equals(entry.Entity.ContentType, "compact_summary", StringComparison.OrdinalIgnoreCase));
            if (staging)
                throw new InvalidOperationException("simulated warm-prefix commit-point failure");
        }
    }

    private sealed class FixedSummaryGenerator(string summary) : IContextCompactionSummaryGenerator
    {
        public Task<string> GenerateSummaryAsync(
            ContextCompactionSummaryRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(summary);
    }

    private sealed class EmptyCanonicalStore : PuddingCode.Platform.ICompactionChatMessageStore
    {
        public Task<IReadOnlyList<PuddingCode.Platform.ChatMessageRow>> GetForSessionAfterIdAsync(
            string sessionId, long afterId, int limit, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PuddingCode.Platform.ChatMessageRow>>([]);

        public Task<IReadOnlyList<PuddingCode.Platform.ChatMessageRow>> GetRecentForSessionAsync(
            string sessionId, int limit, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PuddingCode.Platform.ChatMessageRow>>([]);

        public Task<int> GetCountForSessionAsync(string sessionId, CancellationToken ct = default)
            => Task.FromResult(0);
    }
}
