using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Runtime;
using PuddingMemoryEngine.Data;
using PuddingMemoryEngine.Entities;
using PuddingRuntime.Services;

namespace PuddingRuntimeTests.Services;

/// <summary>
/// P0 子代理终态事务 Phase 2：压缩提交点（commit point）回滚路径的故障注入测试。
///
/// 事故1 的防线不止 Guard：即使围栏判断通过、压缩走到最终提交，也必须保证
/// 「summary 行 + 逐条 CompactedBy 软标记 + 代际推进」在单次 SaveChanges 中原子落库——
/// 提交失败时数据库零残留（无摘要行、无 CompactedBy、代际不变、原始消息逐字未动），
/// 否则半提交会把当前轮推入 EnsureCurrentTurnInputPresent 的 fail-closed 报废路径。
/// </summary>
[TestClass]
public sealed class ContextCompactionCommitRollbackTests
{
    private const string SessionId = "session-commit-rollback";

    [TestMethod]
    public async Task CompactAsync_CommitPointFailure_LeavesZeroPartialWrites()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        // 种子上下文：无拦截器，正常写入历史。
        var seedOptions = CreateOptions(connection, interceptFault: false);
        await using (var db = new MemoryDbContext(seedOptions))
        {
            await db.Database.EnsureCreatedAsync();
            await SeedMessagesAsync(db, SessionId, 26);
        }

        // 服务上下文：提交点拦截器——仅当 compact_summary 摘要行被暂存时抛出。
        var faultOptions = CreateOptions(connection, interceptFault: true);
        var service = new ContextCompactionService(
            new TestMemoryDbContextFactory(faultOptions),
            new FixedSummaryGenerator("## 用户目标\n测试摘要。"),
            NullLogger<ContextCompactionService>.Instance,
            contentSummaryService: null,
            dataPaths: null);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => service.CompactAsync(CreateRequest()));

        // 回滚断言：摘要/软标记/代际三处零残留，历史逐字保留。
        await using var verify = new MemoryDbContext(seedOptions);
        Assert.AreEqual(0, await verify.Messages.CountAsync(m =>
            m.SessionId == SessionId && m.ContentType == "compact_summary"));
        Assert.AreEqual(0, await verify.Messages.CountAsync(m =>
            m.SessionId == SessionId && m.CompactedBy != null));
        Assert.AreEqual(26, await verify.Messages.CountAsync(m => m.SessionId == SessionId));

        var session = await verify.Sessions.SingleAsync(s => s.SessionId == SessionId);
        Assert.AreEqual(0, session.CompactionGeneration, "代际推进必须随提交一起回滚");

        var fenceRow = await verify.Messages.SingleAsync(m =>
            m.SessionId == SessionId && m.Content != null && m.Content.Contains("[CURRENT USER TURN input_sha256="));
        Assert.IsNull(fenceRow.CompactedBy);
        StringAssert.Contains(fenceRow.Content!, "[CURRENT USER TURN input_sha256=");
        StringAssert.Contains(fenceRow.Content!, "[/CURRENT USER TURN input_sha256=");
    }

    // ─────────────────────────────── 帮助方法 ───────────────────────────────

    private static DbContextOptions<MemoryDbContext> CreateOptions(SqliteConnection connection, bool interceptFault)
    {
        var builder = new DbContextOptionsBuilder<MemoryDbContext>()
            .UseSqlite(connection);
        if (interceptFault)
            builder.AddInterceptors(new CommitPointFaultInterceptor());
        return builder.Options;
    }

    private static ContextCompactionRequest CreateRequest()
        => new(
            WorkspaceId: "workspace-1",
            SessionId: SessionId,
            AgentId: "agent-1",
            Mode: ContextCompactionMode.Manual,
            Level: ContextCompactionLevel.Full,
            Reason: "P0-TXN Phase 2 commit rollback");

    /// <summary>
    /// 仅在压缩提交点触发：暂存了 compact_summary 摘要行（ContextCompactionService 的
    /// summary+CompactedBy+代际 原子提交）时抛出。其余 SaveChanges（如转录镜像导入）不受影响。
    /// </summary>
    private sealed class CommitPointFaultInterceptor : SaveChangesInterceptor
    {
        public override InterceptionResult<int> SavingChanges(
            DbContextEventData eventData,
            InterceptionResult<int> result)
        {
            ThrowIfCompactSummaryStaged(eventData.Context);
            return base.SavingChanges(eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            ThrowIfCompactSummaryStaged(eventData.Context);
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }

        private static void ThrowIfCompactSummaryStaged(DbContext? context)
        {
            if (context is null)
                return;

            var stagingCompactSummary = context.ChangeTracker.Entries<MessageEntity>()
                .Any(entry => entry.State == EntityState.Added
                    && string.Equals(entry.Entity.ContentType, "compact_summary", StringComparison.OrdinalIgnoreCase));
            if (stagingCompactSummary)
                throw new InvalidOperationException("simulated compaction commit-point failure");
        }
    }

    private static async Task SeedMessagesAsync(MemoryDbContext db, string sessionId, int count)
    {
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
            + "]\n当前轮请求：验证提交点回滚\n[/CURRENT USER TURN input_sha256="
            + new string('a', 64)
            + "]";

        for (var i = 1; i <= count; i++)
        {
            string role;
            string content;
            if (i == 23)
            {
                // 围栏消息落在保留窗口（RecentMessagesToKeep=6 → 21..26 保留）内，
                // Guard 放行，压缩可以走到提交点。
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

    private sealed class FixedSummaryGenerator(string summary) : IContextCompactionSummaryGenerator
    {
        public Task<string> GenerateSummaryAsync(
            ContextCompactionSummaryRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(summary);
    }
}
