using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingMemoryEngine.Data;
using PuddingMemoryEngine.Entities;
using PuddingRuntime.Services;
using PuddingRuntime.Services.AgentLoop;

namespace PuddingRuntimeTests.Services;

/// <summary>
/// F2 持久化尾款的基础设施：内存消息对象 → 持久 `MessageEntity.MessageId` 的**引用相等**登记。
/// <para>
/// 为什么必须先有它：`CompactedBy` 覆盖标注精确到行，错标等于静默隐藏真实消息；
/// 而内存历史 `List&lt;ChatMessage&gt;` 不携带持久 id，且 `ChatMessage` 是 record（加字段会进入
/// 值相等语义）。因此用旁路登记表，**未登记即"没有持久行"**，调用方必须据此放弃标注。
/// </para>
/// </summary>
[TestClass]
public sealed class DurableMessageIdentityTests
{
    private const string SessionId = "session-durable-identity";

    [TestMethod]
    public async Task HydratedMessages_ResolveToTheirDurableMessageIds()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = CreateOptions(connection);
        await using var db = new MemoryDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await SeedAsync(db, ("user", "u1"), ("agent", "a1"), ("user", "u2"));

        var manager = CreateManager(new Factory(options));
        var persisted = await manager.BuildContextFromDbAsync(SessionId, 100_000, CancellationToken.None);

        Assert.AreEqual(3, persisted.Count);
        var resolution = manager.ResolveDurableSpan(SessionId, persisted);

        Assert.IsTrue(resolution.AllResolved, "库水合出来的消息必须全部能解析到持久行");
        Assert.AreEqual(0, resolution.UnresolvedCount);
        CollectionAssert.AreEqual(new[] { "msg-1", "msg-2", "msg-3" }, resolution.MessageIds.ToArray());
    }

    /// <summary>本 Turn 新追加（尚未落库）的消息**不得**被当成可覆盖：这是防止静默隐藏真实消息的关键。</summary>
    [TestMethod]
    public async Task InTurnAppendedMessage_IsUnresolved_SoTheSpanMustNotBeMarked()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = CreateOptions(connection);
        await using var db = new MemoryDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await SeedAsync(db, ("user", "u1"), ("agent", "a1"));

        var manager = CreateManager(new Factory(options));
        var persisted = await manager.BuildContextFromDbAsync(SessionId, 100_000, CancellationToken.None);

        var span = new List<ChatMessage>(persisted) { new(ChatRole.User, "live turn, not persisted yet") };
        var resolution = manager.ResolveDurableSpan(SessionId, span);

        Assert.IsFalse(resolution.AllResolved, "含未落库消息的跨度必须报告不可全解析");
        Assert.AreEqual(1, resolution.UnresolvedCount);
        Assert.AreEqual(2, resolution.MessageIds.Count, "只解析出来的 id 才可被标注（且调用方须因 AllResolved=false 放弃）");
    }

    /// <summary>按**引用**而不是按值：完全同值的另一个实例不得被当成同一行。</summary>
    [TestMethod]
    public async Task LookupIsByReference_NotByValueEquality()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = CreateOptions(connection);
        await using var db = new MemoryDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await SeedAsync(db, ("user", "same-content"), ("agent", "same-content"));

        var manager = CreateManager(new Factory(options));
        var persisted = await manager.BuildContextFromDbAsync(SessionId, 100_000, CancellationToken.None);

        // `with { }` 生成新实例、各字段同值 ⇒ 值相等为真，但引用不同。
        var clone = persisted[0] with { };
        Assert.AreEqual(persisted[0], clone, "前置条件：值相等");

        Assert.IsTrue(manager.ResolveDurableSpan(SessionId, persisted).AllResolved);
        var cloneOnly = manager.ResolveDurableSpan(SessionId, [clone]);
        Assert.IsFalse(cloneOnly.AllResolved, "同值不同引用不得被误认为同一持久行");
        Assert.AreEqual(0, cloneOnly.MessageIds.Count);
    }

    /// <summary>内容重复的两条消息各自解析到自己那一行（引用相等 ⇒ 不会串行）。</summary>
    [TestMethod]
    public async Task DuplicateContents_ResolveToTheirOwnRows()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = CreateOptions(connection);
        await using var db = new MemoryDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await SeedAsync(db, ("user", "dup"), ("agent", "dup"));

        var manager = CreateManager(new Factory(options));
        var persisted = await manager.BuildContextFromDbAsync(SessionId, 100_000, CancellationToken.None);

        Assert.AreEqual(persisted[0].Content, persisted[1].Content, "前置条件：内容相同");
        CollectionAssert.AreEqual(
            new[] { "msg-1" },
            manager.ResolveDurableSpan(SessionId, [persisted[0]]).MessageIds.ToArray());
        CollectionAssert.AreEqual(
            new[] { "msg-2" },
            manager.ResolveDurableSpan(SessionId, [persisted[1]]).MessageIds.ToArray());
    }

    /// <summary>失效内存历史后登记必须一并失效（避免旁路表长期引用旧消息对象）。</summary>
    [TestMethod]
    public async Task InvalidateHistory_DropsRegistrations()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = CreateOptions(connection);
        await using var db = new MemoryDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await SeedAsync(db, ("user", "u1"), ("agent", "a1"));

        var manager = CreateManager(new Factory(options));
        var persisted = await manager.BuildContextFromDbAsync(SessionId, 100_000, CancellationToken.None);
        Assert.IsTrue(manager.ResolveDurableSpan(SessionId, persisted).AllResolved);

        manager.InvalidateHistory(SessionId);

        var afterInvalidate = manager.ResolveDurableSpan(SessionId, persisted);
        Assert.IsFalse(afterInvalidate.AllResolved);
        Assert.AreEqual(persisted.Count, afterInvalidate.UnresolvedCount);
    }

    /// <summary>整体替换语义：重新水合后新实例可解析，旧实例不再解析（有界，不泄漏旧对象）。</summary>
    [TestMethod]
    public async Task Rehydration_ReplacesRegistrations()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = CreateOptions(connection);
        await using var db = new MemoryDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await SeedAsync(db, ("user", "u1"), ("agent", "a1"));

        var manager = CreateManager(new Factory(options));
        var first = await manager.BuildContextFromDbAsync(SessionId, 100_000, CancellationToken.None);
        var second = await manager.BuildContextFromDbAsync(SessionId, 100_000, CancellationToken.None);

        Assert.IsTrue(manager.ResolveDurableSpan(SessionId, second).AllResolved, "重新水合的实例必须可解析");
        Assert.IsFalse(
            manager.ResolveDurableSpan(SessionId, first).AllResolved,
            "旧实例应随整体替换失效，否则旁路表会无限增长");
    }

    /// <summary>空跨度不是「全部解析」：调用方不得据此写出空的覆盖清单。</summary>
    [TestMethod]
    public void EmptySpan_IsNotAllResolved()
    {
        var manager = CreateManager(memoryDbFactory: null);
        var resolution = manager.ResolveDurableSpan(SessionId, []);
        Assert.IsFalse(resolution.AllResolved);
        Assert.AreEqual(0, resolution.MessageIds.Count);
    }

    /// <summary>F2：冻结候选时必须能读到**真实持久代际**（落库前 CAS 的输入）。</summary>
    [TestMethod]
    public async Task GetCompactionGeneration_ReadsDurableColumn()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = CreateOptions(connection);
        await using (var db = new MemoryDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            await SeedAsync(db, ("user", "u1"));
        }

        var manager = CreateManager(new Factory(options));
        Assert.AreEqual(0, await manager.GetCompactionGenerationAsync(SessionId, CancellationToken.None));

        await using (var db = new MemoryDbContext(options))
        {
            var session = await db.Sessions.SingleAsync(s => s.SessionId == SessionId);
            session.CompactionGeneration = 5;
            await db.SaveChangesAsync();
        }

        Assert.AreEqual(5, await manager.GetCompactionGenerationAsync(SessionId, CancellationToken.None));
    }

    [TestMethod]
    public async Task GetCompactionGeneration_UnknownSessionOrNoDatabase_IsZero()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = CreateOptions(connection);
        await using (var db = new MemoryDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
        }

        var manager = CreateManager(new Factory(options));
        Assert.AreEqual(0, await manager.GetCompactionGenerationAsync("no-such-session", CancellationToken.None));

        var databaseLess = CreateManager(memoryDbFactory: null);
        Assert.AreEqual(0, await databaseLess.GetCompactionGenerationAsync(SessionId, CancellationToken.None));
    }

    private static DbContextOptions<MemoryDbContext> CreateOptions(SqliteConnection connection) =>
        new DbContextOptionsBuilder<MemoryDbContext>()
            .UseSqlite(connection)
            .Options;

    private static async Task SeedAsync(MemoryDbContext db, params (string Role, string Content)[] messages)
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

        for (var i = 0; i < messages.Length; i++)
        {
            db.Messages.Add(new MessageEntity
            {
                MessageId = $"msg-{i + 1}",
                SessionId = SessionId,
                Sequence = i + 1,
                Role = messages[i].Role,
                ContentType = "text",
                Content = messages[i].Content,
                CreatedAt = i + 1,
            });
        }

        await db.SaveChangesAsync();
    }

    private static ContextWindowManager CreateManager(IDbContextFactory<MemoryDbContext>? memoryDbFactory)
        => new(
            new AgentSessionManager(NullLogger<AgentSessionManager>.Instance),
            new InMemoryRuntimeSessionStore(),
            new ExecutionControlRegistry(),
            new ExecutionJournal(),
            NullLogger<ContextWindowManager>.Instance,
            memoryDbFactory: memoryDbFactory,
            canonicalMessageStore: memoryDbFactory is null ? null : new EmptyCanonicalStore());

    private sealed class Factory(DbContextOptions<MemoryDbContext> options) : IDbContextFactory<MemoryDbContext>
    {
        public MemoryDbContext CreateDbContext() => new(options);
    }

    /// <summary>测试直接往 `chat_messages` 播种，canonical 源为空即可（同步不会导入任何东西）。</summary>
    private sealed class EmptyCanonicalStore : ICompactionChatMessageStore
    {
        public Task<IReadOnlyList<ChatMessageRow>> GetForSessionAfterIdAsync(
            string sessionId, long afterId, int limit, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ChatMessageRow>>([]);

        public Task<IReadOnlyList<ChatMessageRow>> GetRecentForSessionAsync(
            string sessionId, int limit, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ChatMessageRow>>([]);

        public Task<int> GetCountForSessionAsync(string sessionId, CancellationToken ct = default)
            => Task.FromResult(0);
    }
}
