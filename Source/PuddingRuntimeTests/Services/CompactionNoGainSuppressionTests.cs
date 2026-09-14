using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Runtime;
using PuddingMemoryEngine.Data;
using PuddingMemoryEngine.Entities;
using PuddingRuntime.Services;

namespace PuddingRuntimeTests.Services;

/// <summary>
/// A2 无收益抑制：同一候选窗口（压缩代际 + MessageId + 正文 hash 指纹）未变且上次压缩无收益
/// （no-op 或摘要不缩小）时，自动压缩不再重复调用摘要模型；新原文 / 代际前进使指纹变化后重新评估；
/// 手动压缩为显式重试（清除抑制并放行）；异常失败不写抑制标记（可恢复，不永久屏蔽）。
/// 用 fake <see cref="IContextCompactionSummaryGenerator"/> 计数调用次数，不依赖真实 LLM。
/// fixture 模式照抄 ContextCompactionCommitRollbackTests（Sqlite 内存 + 26 条种子消息 + 保留窗口围栏）。
/// </summary>
[TestClass]
public sealed class CompactionNoGainSuppressionTests
{
    private const string SessionId = "session-no-gain-suppression";

    /// <summary>「摘要不缩小」触发摘要：token 数必然超过 21 条短消息组成的输入窗口。</summary>
    private static readonly string NoGainSummary = "## 用户目标\n" + new string('x', 64_000);

    private const string ShortSummary = "## 用户目标\n压缩后的短摘要。";

    // ── T1：窗口不变 + 首轮摘要不缩小 → 第二次 Auto 被抑制，摘要模型不再被调用 ──
    [TestMethod]
    public async Task Auto_SameCandidateWindowAfterNoGain_SecondAttemptSkipsWithoutGeneratorCall()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = CreateOptions(connection);
        await SeedMessagesAsync(options, SessionId, 26);

        var generator = new CountingSummaryGenerator(_ => NoGainSummary);
        var coordinator = new CompactionCoordinator(cooldown: TimeSpan.Zero);
        var service = CreateService(options, generator, coordinator);

        // 第一轮 Auto：摘要不缩小 → 记录抑制，显式 SkippedNoGain。
        var first = await service.CompactAsync(CreateRequest(ContextCompactionMode.Auto));
        Assert.AreEqual(ContextCompactionOutcome.SkippedNoGain, first.Outcome);
        Assert.IsTrue(first.SkippedDueToTokenIncrease, "首轮应命中 SkipWhenSummaryIncreasesTokens");
        Assert.AreEqual(1, generator.CallCount);

        // 第二轮 Auto：候选窗口未变（无新原文、代际未前进）→ 抑制闸门生效，不调用摘要模型。
        var second = await service.CompactAsync(CreateRequest(ContextCompactionMode.Auto));
        Assert.AreEqual(ContextCompactionOutcome.SkippedNoGain, second.Outcome);
        Assert.AreEqual(0, second.CompactedMessageCount);
        Assert.AreEqual(1, generator.CallCount, "同窗口无收益抑制生效后不得再次调用摘要模型");
        Assert.AreEqual(first.BeforeTokens, second.BeforeTokens);

        // 无新增 summary 写入、无 CompactedBy、代际未前进。
        await using var verify = new MemoryDbContext(options);
        Assert.AreEqual(0, await verify.Messages.CountAsync(m =>
            m.SessionId == SessionId && m.ContentType == "compact_summary"));
        Assert.AreEqual(0, await verify.Messages.CountAsync(m =>
            m.SessionId == SessionId && m.CompactedBy != null));
        var session = await verify.Sessions.SingleAsync(s => s.SessionId == SessionId);
        Assert.AreEqual(0, session.CompactionGeneration);
    }

    // ── T2：抑制生效后导入新原文（新 MessageId/内容）→ 指纹变化，生成器被再次调用 ──
    [TestMethod]
    public async Task Auto_NewRawMessageInWindow_SuppressionLifted_GeneratorCalledAgain()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = CreateOptions(connection);
        await SeedMessagesAsync(options, SessionId, 26);

        var generator = new CountingSummaryGenerator(_ => NoGainSummary);
        var service = CreateService(options, generator, new CompactionCoordinator(cooldown: TimeSpan.Zero));

        var first = await service.CompactAsync(CreateRequest(ContextCompactionMode.Auto));
        Assert.AreEqual(ContextCompactionOutcome.SkippedNoGain, first.Outcome);
        Assert.AreEqual(1, generator.CallCount);

        // 导入新原文：新 MessageId/新内容追加到窗口尾部 → 压缩窗口前移 → 指纹变化。
        await using (var db = new MemoryDbContext(options))
        {
            db.Messages.Add(new MessageEntity
            {
                MessageId = $"{SessionId}-m27",
                SessionId = SessionId,
                Sequence = 27,
                Role = "agent",
                ContentType = "text",
                Content = "brand new raw text imported before second attempt",
                CreatedAt = 27,
            });
            await db.SaveChangesAsync();
        }

        generator.RespondWith(_ => ShortSummary);
        var second = await service.CompactAsync(CreateRequest(ContextCompactionMode.Auto));

        Assert.AreEqual(ContextCompactionOutcome.Applied, second.Outcome);
        Assert.AreEqual(2, generator.CallCount, "新原文使指纹变化，抑制必须失效并重新调用生成器");
        Assert.IsTrue(second.CompactedMessageCount > 0);

        await using var verify = new MemoryDbContext(options);
        var session = await verify.Sessions.SingleAsync(s => s.SessionId == SessionId);
        Assert.AreEqual(1, session.CompactionGeneration);
    }

    // ── T3：抑制生效后改为 Manual → 显式重试放行（生成器被再次调用），且抑制被清除 ──
    [TestMethod]
    public async Task Manual_CompactionAfterNoGain_ExplicitRetryClearsSuppressionAndCallsGenerator()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = CreateOptions(connection);
        await SeedMessagesAsync(options, SessionId, 26);

        var generator = new CountingSummaryGenerator(_ => NoGainSummary);
        var coordinator = new CompactionCoordinator(cooldown: TimeSpan.Zero);
        var service = CreateService(options, generator, coordinator);

        var first = await service.CompactAsync(CreateRequest(ContextCompactionMode.Auto));
        Assert.AreEqual(ContextCompactionOutcome.SkippedNoGain, first.Outcome);
        Assert.AreEqual(1, generator.CallCount);

        generator.RespondWith(_ => ShortSummary);
        var manual = await service.CompactAsync(CreateRequest(ContextCompactionMode.Manual));

        Assert.AreEqual(ContextCompactionOutcome.Applied, manual.Outcome);
        Assert.AreEqual(2, generator.CallCount, "手动压缩 = 显式重试，必须放行并调用生成器");

        // 手动成功写入 → RecordCompactionCompleted → 抑制被清除（对任意指纹均不再命中）。
        Assert.IsFalse(coordinator.TryGetNoGainSkipReason(SessionId, "any-fingerprint", out _));
        await using var verify = new MemoryDbContext(options);
        var session = await verify.Sessions.SingleAsync(s => s.SessionId == SessionId);
        Assert.AreEqual(1, session.CompactionGeneration);
    }

    // ── T4：生成器抛异常 → 不写抑制，下一次 Auto 仍会调用生成器（可恢复） ──
    [TestMethod]
    public async Task Auto_GeneratorThrows_SuppressionNotRecorded_NextAutoRetries()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = CreateOptions(connection);
        await SeedMessagesAsync(options, SessionId, 26);

        var generator = new CountingSummaryGenerator(
            _ => throw new InvalidOperationException("simulated summary failure"));
        var service = CreateService(options, generator, new CompactionCoordinator(cooldown: TimeSpan.Zero));

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => service.CompactAsync(CreateRequest(ContextCompactionMode.Auto)));
        Assert.AreEqual(1, generator.CallCount);

        // 异常路径零残留：无摘要写入、无 CompactedBy、代际未前进（抑制亦未记录）。
        await using (var verify = new MemoryDbContext(options))
        {
            Assert.AreEqual(0, await verify.Messages.CountAsync(m =>
                m.SessionId == SessionId && m.ContentType == "compact_summary"));
            var session = await verify.Sessions.SingleAsync(s => s.SessionId == SessionId);
            Assert.AreEqual(0, session.CompactionGeneration);
        }

        generator.RespondWith(_ => ShortSummary);
        var retry = await service.CompactAsync(CreateRequest(ContextCompactionMode.Auto));

        Assert.AreEqual(ContextCompactionOutcome.Applied, retry.Outcome);
        Assert.AreEqual(2, generator.CallCount, "异常失败不得永久屏蔽：下一次 Auto 仍调用生成器");
    }

    // ── T5：CompactionGeneration 前进（同一窗口、内容不变）→ 指纹变化，抑制失效 ──
    [TestMethod]
    public async Task Auto_CompactionGenerationAdvances_FingerprintChanges_SuppressionLifted()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = CreateOptions(connection);
        await SeedMessagesAsync(options, SessionId, 26);

        var generator = new CountingSummaryGenerator(_ => NoGainSummary);
        var service = CreateService(options, generator, new CompactionCoordinator(cooldown: TimeSpan.Zero));

        var first = await service.CompactAsync(CreateRequest(ContextCompactionMode.Auto));
        Assert.AreEqual(ContextCompactionOutcome.SkippedNoGain, first.Outcome);
        Assert.AreEqual(1, generator.CallCount);

        // 代际前进：同一候选窗口、内容不变，仅推进 SessionEntity.CompactionGeneration。
        await using (var db = new MemoryDbContext(options))
        {
            var session = await db.Sessions.SingleAsync(s => s.SessionId == SessionId);
            session.CompactionGeneration = 7;
            await db.SaveChangesAsync();
        }

        generator.RespondWith(_ => ShortSummary);
        var second = await service.CompactAsync(CreateRequest(ContextCompactionMode.Auto));

        Assert.AreEqual(ContextCompactionOutcome.Applied, second.Outcome);
        Assert.AreEqual(2, generator.CallCount, "代际前进参与指纹，抑制必须失效并重新评估");
    }

    // ── T6：无候选（candidates.Count == 0）→ SkippedNoCandidate，且不记录抑制 ──
    [TestMethod]
    public async Task Auto_NoCandidates_SkipRecordedAsNoCandidate_WithoutSuppression()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = CreateOptions(connection);

        // 空会话：只有 Session 行，无任何 text 消息 → candidates.Count == 0。
        await using (var db = new MemoryDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            db.Sessions.Add(new SessionEntity
            {
                SessionId = SessionId,
                WorkspaceId = "workspace-1",
                AgentId = "agent-1",
                CreatedAt = 1,
                LastActivityAt = 1,
            });
            await db.SaveChangesAsync();
        }

        var generator = new CountingSummaryGenerator(_ => ShortSummary);
        var service = CreateService(options, generator, new CompactionCoordinator(cooldown: TimeSpan.Zero));

        var first = await service.CompactAsync(CreateRequest(ContextCompactionMode.Auto));
        Assert.AreEqual(ContextCompactionOutcome.SkippedNoCandidate, first.Outcome);
        Assert.AreEqual(0, first.CompactedMessageCount);
        Assert.AreEqual(0, generator.CallCount);

        // 未记录抑制：后续有新原文仍正常压缩。
        await SeedMessagesAsync(options, SessionId, 26);
        var second = await service.CompactAsync(CreateRequest(ContextCompactionMode.Auto));

        Assert.AreEqual(ContextCompactionOutcome.Applied, second.Outcome);
        Assert.AreEqual(1, generator.CallCount, "无候选跳过不得记录抑制，后续正常压缩");
    }

    // ─────────────────────────────── 帮助方法 ───────────────────────────────

    private static DbContextOptions<MemoryDbContext> CreateOptions(SqliteConnection connection)
        => new DbContextOptionsBuilder<MemoryDbContext>()
            .UseSqlite(connection)
            .Options;

    private static ContextCompactionService CreateService(
        DbContextOptions<MemoryDbContext> options,
        IContextCompactionSummaryGenerator generator,
        CompactionCoordinator coordinator)
        => new(
            new TestMemoryDbContextFactory(options),
            generator,
            NullLogger<ContextCompactionService>.Instance,
            contentSummaryService: null,
            options: new ContextCompactionOptions { SkipWhenSummaryIncreasesTokens = true },
            compactionCoordinator: coordinator);

    private static ContextCompactionRequest CreateRequest(ContextCompactionMode mode)
        => new(
            WorkspaceId: "workspace-1",
            SessionId: SessionId,
            AgentId: "agent-1",
            Mode: mode,
            Level: ContextCompactionLevel.Full,
            Reason: "A2 no-gain suppression");

    private static async Task SeedMessagesAsync(
        DbContextOptions<MemoryDbContext> options,
        string sessionId,
        int count)
    {
        await using var db = new MemoryDbContext(options);
        await db.Database.EnsureCreatedAsync();
        if (!await db.Sessions.AnyAsync(session => session.SessionId == sessionId))
            db.Sessions.Add(new SessionEntity
            {
                SessionId = sessionId,
                WorkspaceId = "workspace-1",
                AgentId = "agent-1",
                CreatedAt = 1,
                LastActivityAt = 1,
            });

        var fence = "[CURRENT USER TURN input_sha256="
            + new string('a', 64)
            + "]\n当前轮请求：验证无收益抑制\n[/CURRENT USER TURN input_sha256="
            + new string('a', 64)
            + "]";

        for (var i = 1; i <= count; i++)
        {
            string role;
            string content;
            if (i == 23)
            {
                // 围栏消息落在保留窗口（RecentMessagesToKeep=6 → 21..26 保留）内，
                // CurrentTurnCompactionGuard 放行，压缩可正常推进。
                role = "user";
                content = fence;
            }
            else if (i > 20)
            {
                role = "agent";
                content = $"assistant reply {i}";
            }
            else
            {
                role = i % 2 == 1 ? "user" : "agent";
                content = $"message {i}";
            }

            db.Messages.Add(new MessageEntity
            {
                MessageId = $"{sessionId}-m{i}",
                SessionId = sessionId,
                Sequence = i,
                Role = role,
                ContentType = "text",
                Content = content,
                CreatedAt = i,
            });
        }

        await db.SaveChangesAsync();
    }

    private sealed class TestMemoryDbContextFactory(DbContextOptions<MemoryDbContext> options) : IDbContextFactory<MemoryDbContext>
    {
        public MemoryDbContext CreateDbContext() => new(options);

        public Task<MemoryDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    /// <summary>可计数、可换响应的摘要生成器 fake：禁止真实 LLM。</summary>
    private sealed class CountingSummaryGenerator(Func<ContextCompactionSummaryRequest, string> respond) : IContextCompactionSummaryGenerator
    {
        private Func<ContextCompactionSummaryRequest, string> _respond = respond;

        public int CallCount { get; private set; }

        public void RespondWith(Func<ContextCompactionSummaryRequest, string> respond) => _respond = respond;

        public Task<string> GenerateSummaryAsync(
            ContextCompactionSummaryRequest request,
            CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult(_respond(request));
        }
    }
}
