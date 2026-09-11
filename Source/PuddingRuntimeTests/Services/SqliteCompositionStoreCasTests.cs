using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

using PuddingCode.Runtime;
using PuddingMemoryEngine.Data;
using PuddingRuntime.Services;

namespace PuddingRuntimeTests.Services;

/// <summary>
/// C01-B-1 测试方案 C（C01B-AC2 CAS）：真实 SQLite 文件库上的 CAS 行为。
/// 使用临时 SQLite **文件**（非 EF InMemory provider），保证事务/锁语义真实。
/// </summary>
[TestClass]
public sealed class SqliteCompositionStoreCasTests
{
    private string _dbPath = string.Empty;
    private string _connectionString = string.Empty;
    private IDbContextFactory<MemoryDbContext>? _factory;
    private SqliteCompositionStore? _store;

    [TestInitialize]
    public void SetUp()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"c01b1-composition-{Guid.NewGuid():N}.db");
        _connectionString = $"Data Source={_dbPath}";

        _factory = CreateFactory(_connectionString);
        using (var db = _factory.CreateDbContext())
        {
            db.Database.EnsureCreated();
        }

        _store = new SqliteCompositionStore(_factory);
    }

    [TestCleanup]
    public void TearDown()
    {
        _store = null;
        _factory = null;
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
    }

    private static IDbContextFactory<MemoryDbContext> CreateFactory(string connectionString)
    {
        var options = new DbContextOptionsBuilder<MemoryDbContext>()
            .UseSqlite(connectionString)
            .Options;
        return new PooledDbContextFactory<MemoryDbContext>(options);
    }

    private static SessionCompositionRecord Record(string sessionId, long revision, string contentId, params string[] toolIds) => new()
    {
        SessionId = sessionId,
        CompositionVersion = revision,
        ContentId = contentId,
        SystemPromptHash = $"sys-{revision}",
        ToolSpecHash = $"tool-{revision}",
        PrefixHash = $"prefix-{revision}",
        ToolIds = toolIds,
        ChangeReason = revision == 1 ? "initial" : "system_prompt_changed",
    };

    [TestMethod]
    public async Task Append_WithExpectedRevision_MatchesHead_Commits()
    {
        var first = await _store!.AppendAsync(Record("session-a", 1, "cid-a", "search_tools"), expectedRevision: 0);
        var second = await _store.AppendAsync(Record("session-a", 2, "cid-b", "search_tools", "file_read"), expectedRevision: 1);

        Assert.IsTrue(first.IsCommitted);
        Assert.AreEqual(1L, first.Revision);
        Assert.IsTrue(second.IsCommitted);
        Assert.AreEqual(2L, second.Revision);

        var latest = await _store.GetLatestAsync("session-a");
        Assert.AreEqual(2L, latest!.CompositionVersion);
    }

    [TestMethod]
    public async Task Append_WithExpectedRevision_Mismatch_ReturnsConflict()
    {
        await _store!.AppendAsync(Record("session-a", 1, "cid-a"), expectedRevision: 0);

        // expectedRevision 过期（真实 head=1）→ 明确 Conflict，且不得写入。
        var conflict = await _store.AppendAsync(Record("session-a", 2, "cid-b"), expectedRevision: 0);

        Assert.IsTrue(conflict.IsConflict);
        Assert.AreEqual(0L, conflict.ExpectedRevision);
        Assert.AreEqual(1L, conflict.ActualRevision, "Conflict 必须回报实际 head，供调用方重读重算。");

        var all = await _store.LoadAsync("session-a");
        Assert.AreEqual(1, all.Count, "CAS 冲突不得插入任何记录。");
    }

    [TestMethod]
    public async Task Append_ContentId_RoundTrips_AndLegacyRowIsNull()
    {
        await _store!.AppendAsync(Record("session-a", 1, "cid-1", "search_tools", "file_read"), expectedRevision: 0);

        var latest = await _store.GetLatestAsync("session-a");
        Assert.AreEqual("cid-1", latest!.ContentId, "ContentId 必须随内容身份落库并可恢复。");
        CollectionAssert.AreEqual(new[] { "search_tools", "file_read" }, latest.ToolIds.ToArray());

        // 历史行（C01-B 之前）无 ContentId → null：调用方须据此判定「无法证明精确内容」，不得抛异常。
        var legacy = await _store.AppendAsync(Record("session-legacy", 1, "cid-legacy") with { ContentId = null }, expectedRevision: 0);
        Assert.IsTrue(legacy.IsCommitted);
        var legacyLatest = await _store.GetLatestAsync("session-legacy");
        Assert.IsNull(legacyLatest!.ContentId);
    }

    [TestMethod]
    public async Task Append_ConcurrentSameExpectedRevision_OneConflict()
    {
        var otherStore = new SqliteCompositionStore(CreateFactory(_connectionString));

        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstTask = Task.Run(async () =>
        {
            await start.Task;
            return await _store!.AppendAsync(Record("session-a", 1, "cid-a"), expectedRevision: 0);
        });
        var secondTask = Task.Run(async () =>
        {
            await start.Task;
            return await otherStore.AppendAsync(Record("session-a", 1, "cid-b"), expectedRevision: 0);
        });

        start.SetResult();
        var results = await Task.WhenAll(firstTask, secondTask);

        Assert.AreEqual(1, results.Count(r => r.IsCommitted), "两个提交者撞同一 expectedRevision 时恰好一个成功。");
        var loser = results.Single(r => !r.IsCommitted);
        Assert.IsTrue(
            loser.IsConflict || loser.IsUnavailable,
            "落败提交者必须得到明确的 Conflict 或可重试 Unavailable，不得抛未处理异常。");
        if (loser.IsUnavailable)
            StringAssert.Contains(loser.FailureReason ?? string.Empty, CompositionAppendResult.UnavailableErrorCode);

        var all = await _store!.LoadAsync("session-a");
        Assert.AreEqual(1, all.Count, "最终只允许一条 revision 1 记录。");
    }
}
