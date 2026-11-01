using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PuddingMemoryEngine.Data;
using PuddingMemoryEngine.Services;

namespace PuddingMemoryEngineTests;

/// <summary>
/// 端到端：`FactMemorySink` 真的把事实写进 `MemoryFacts`。
/// <para>
/// 为什么非要这个测试：`FactMemoryService.CreateFactAsync` 长期生产调用点为 0，
/// `MemoryFacts` 恒为 0 行。接线之后如果只用单测证明「服务能跑通」，
/// 那就等于又回到"自己 new 一个实例当然绿"的老路 —— 这个测试断言的是**落库结果**。
/// </para>
/// </summary>
[TestClass]
public sealed class FactMemorySinkTests
{
    private static async Task<TestScope> CreateScopeAsync()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<MemoryLibraryDbContext>()
            .UseSqlite(connection)
            .Options;

        var factory = new TestDbContextFactory(options);
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.EnsureCreatedAsync();
        }

        return new TestScope(connection, factory);
    }

    [TestMethod]
    public async Task Sink_writes_one_fact_row()
    {
        await using var scope = await CreateScopeAsync();
        var sink = new FactMemorySink(new FactMemoryService(scope.Factory));

        await using (var before = scope.Factory.CreateDbContext())
        {
            Assert.AreEqual(0, await before.MemoryFacts.CountAsync(), "前置：空库");
        }

        await sink.SinkAsync(new FactMemoryDraft(
            WorkspaceId: "ws-sink",
            AgentId: "agent-sink",
            Statement: "用户要求回答保持中文。",
            FactType: "preference",
            SourceSessionId: "sess-1",
            SourceMessageId: "msg-1"));

        await using var after = scope.Factory.CreateDbContext();
        Assert.AreEqual(1, await after.MemoryFacts.CountAsync(), "落库后应有且只有一条事实");

        var fact = await after.MemoryFacts.SingleAsync();
        StringAssert.Contains(fact.Statement, "中文", "内容必须原样落库");
        Assert.AreEqual(1, await after.MemoryFactEvidence.CountAsync(), "证据至少一条（引擎要求）");
        Assert.AreEqual(1, await after.MemorySpaces.CountAsync(), "记忆空间应被自动创建且只有一份");
    }

    [TestMethod]
    public async Task Repeated_sink_reuses_the_same_memory_space()
    {
        await using var scope = await CreateScopeAsync();
        var sink = new FactMemorySink(new FactMemoryService(scope.Factory));

        for (var i = 0; i < 3; i++)
        {
            await sink.SinkAsync(new FactMemoryDraft(
                "ws-sink", "agent-sink", $"事实 {i}", "fact", "sess-1"));
        }

        await using var db = scope.Factory.CreateDbContext();
        Assert.AreEqual(3, await db.MemoryFacts.CountAsync());
        Assert.AreEqual(1, await db.MemorySpaces.CountAsync(), "同一 (workspace, agent) 不应产生多个空间");
    }

    [TestMethod]
    public async Task Empty_statement_is_rejected_before_any_write()
    {
        await using var scope = await CreateScopeAsync();
        var sink = new FactMemorySink(new FactMemoryService(scope.Factory));

        var rejected = false;
        try
        {
            await sink.SinkAsync(new FactMemoryDraft("ws-sink", "agent-sink", "   ", "fact", "sess-1"));
        }
        catch (ArgumentException)
        {
            rejected = true;
        }

        Assert.IsTrue(rejected, "空陈述必须在写入前被拒绝");

        await using var db = scope.Factory.CreateDbContext();
        Assert.AreEqual(0, await db.MemoryFacts.CountAsync(), "被拒绝的写入不得留下半条记录");
    }

    private sealed class TestScope(SqliteConnection connection, IDbContextFactory<MemoryLibraryDbContext> factory)
        : IAsyncDisposable
    {
        private readonly SqliteConnection _connection = connection;

        public IDbContextFactory<MemoryLibraryDbContext> Factory { get; } = factory;

        public async ValueTask DisposeAsync() => await _connection.DisposeAsync();
    }

    private sealed class TestDbContextFactory(DbContextOptions<MemoryLibraryDbContext> options)
        : IDbContextFactory<MemoryLibraryDbContext>
    {
        public MemoryLibraryDbContext CreateDbContext() => new(options);
    }
}
