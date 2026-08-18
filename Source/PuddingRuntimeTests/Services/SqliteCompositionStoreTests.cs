using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

using PuddingCode.Runtime;
using PuddingMemoryEngine.Data;
using PuddingRuntime.Services;

namespace PuddingRuntimeTests.Services;

[TestClass]
public sealed class SqliteCompositionStoreTests
{
    private SqliteConnection? _connection;
    private IDbContextFactory<MemoryDbContext>? _factory;
    private SqliteCompositionStore? _store;

    [TestInitialize]
    public void SetUp()
    {
        // in-memory SQLite：连接必须保持打开，否则每次新建连接都会得到空库。
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<MemoryDbContext>()
            .UseSqlite(_connection)
            .Options;

        _factory = new PooledDbContextFactory<MemoryDbContext>(options);
        using (var db = _factory.CreateDbContext())
        {
            db.Database.EnsureCreated();
        }

        _store = new SqliteCompositionStore(_factory);
    }

    [TestCleanup]
    public void TearDown()
    {
        _connection?.Dispose();
        _connection = null;
        _factory = null;
        _store = null;
    }

    // ── 辅助 ─────────────────────────────────────────────

    private static SessionCompositionRecord Record(string sessionId, long version, params string[] toolIds) => new()
    {
        SessionId = sessionId,
        CompositionVersion = version,
        SystemPromptHash = $"sys-{version}",
        ToolSpecHash = $"tool-{version}",
        PrefixHash = $"prefix-{version}",
        SkillManifestHash = $"skill-{version}",
        ToolIds = toolIds,
        ChangeReason = version == 1 ? "initial" : "tool_spec_changed",
        PermissionEpoch = 0,
        CanonicalSystemPrefixHash = $"canonical-{version}",
    };

    // ── Append + GetLatest 取最大版本 ────────────────────

    [TestMethod]
    public async Task Append_ThenGetLatest_RoundTripsAllFields()
    {
        var record = Record("session-a", 1, "search_tools", "file_read", "file_write");

        await _store!.AppendAsync(record);

        var latest = await _store.GetLatestAsync("session-a");
        Assert.IsNotNull(latest);
        Assert.AreEqual("session-a", latest.SessionId);
        Assert.AreEqual(1, latest.CompositionVersion);
        Assert.AreEqual("sys-1", latest.SystemPromptHash);
        Assert.AreEqual("tool-1", latest.ToolSpecHash);
        Assert.AreEqual("prefix-1", latest.PrefixHash);
        Assert.AreEqual("skill-1", latest.SkillManifestHash);
        Assert.AreEqual("canonical-1", latest.CanonicalSystemPrefixHash);
        Assert.AreEqual("initial", latest.ChangeReason);
        Assert.AreEqual("prefix-v1", latest.SerializationVersion);
        CollectionAssert.AreEqual(
            new[] { "search_tools", "file_read", "file_write" },
            latest.ToolIds.ToArray());
    }

    [TestMethod]
    public async Task GetLatest_AfterMultipleAppends_ReturnsMaxVersion()
    {
        await _store!.AppendAsync(Record("session-a", 1, "search_tools"));
        await _store.AppendAsync(Record("session-a", 2, "search_tools", "file_read"));
        await _store.AppendAsync(Record("session-a", 3, "search_tools", "file_read", "file_write"));

        var latest = await _store.GetLatestAsync("session-a");
        Assert.IsNotNull(latest);
        Assert.AreEqual(3, latest.CompositionVersion);
        CollectionAssert.AreEqual(
            new[] { "search_tools", "file_read", "file_write" },
            latest.ToolIds.ToArray());
    }

    [TestMethod]
    public async Task GetLatest_NoRecords_ReturnsNull()
    {
        var latest = await _store!.GetLatestAsync("session-unknown");
        Assert.IsNull(latest);
    }

    [TestMethod]
    public async Task Load_ReturnsAllVersionsAscending()
    {
        await _store!.AppendAsync(Record("session-a", 1, "a"));
        await _store.AppendAsync(Record("session-a", 2, "a", "b"));
        await _store.AppendAsync(Record("session-a", 3, "a", "b", "c"));

        var all = await _store.LoadAsync("session-a");
        Assert.AreEqual(3, all.Count);
        CollectionAssert.AreEqual(
            new long[] { 1, 2, 3 },
            all.Select(r => r.CompositionVersion).ToArray());
    }

    // ── append-only 语义 ─────────────────────────────────

    [TestMethod]
    public async Task Append_SameVersion_ReturnsFalse()
    {
        await _store!.AppendAsync(Record("session-a", 1, "search_tools"));

        var ok = await _store.AppendAsync(Record("session-a", 1, "search_tools", "file_read"));
        Assert.IsFalse(ok);
    }

    [TestMethod]
    public async Task Append_LowerVersion_ReturnsFalse()
    {
        await _store!.AppendAsync(Record("session-a", 2, "a", "b"));

        var ok = await _store.AppendAsync(Record("session-a", 1, "a"));
        Assert.IsFalse(ok);
    }

    // ── ToolIds 边界 ─────────────────────────────────────

    [TestMethod]
    public async Task Append_EmptyToolIds_RoundTripsAsEmpty()
    {
        await _store!.AppendAsync(Record("session-a", 1));

        var latest = await _store.GetLatestAsync("session-a");
        Assert.IsNotNull(latest);
        Assert.AreEqual(0, latest.ToolIds.Count);
    }

    [TestMethod]
    public async Task Append_ThenLoad_IndependentSessions_DoNotInterfere()
    {
        await _store!.AppendAsync(Record("session-a", 1, "a"));
        await _store.AppendAsync(Record("session-b", 1, "x", "y"));

        var latestA = await _store.GetLatestAsync("session-a");
        var latestB = await _store.GetLatestAsync("session-b");
        Assert.AreEqual(1, latestA!.CompositionVersion);
        Assert.AreEqual(1, latestB!.CompositionVersion);
        CollectionAssert.AreEqual(new[] { "a" }, latestA.ToolIds.ToArray());
        CollectionAssert.AreEqual(new[] { "x", "y" }, latestB.ToolIds.ToArray());
    }

    // ── ToolIds 只增不减 ─────────────────────────────────

    [TestMethod]
    public async Task Append_ToolIdsGrowOnly_AcrossVersions()
    {
        await _store!.AppendAsync(Record("session-a", 1, "search_tools"));
        await _store.AppendAsync(Record("session-a", 2, "search_tools", "file_read"));
        await _store.AppendAsync(Record("session-a", 3, "search_tools", "file_read", "file_write"));

        var latest = await _store.GetLatestAsync("session-a");
        Assert.IsNotNull(latest);
        // 最新版本 ToolIds 必须包含全部已提交工具（只增不减，不收缩）。
        CollectionAssert.AreEqual(
            new[] { "search_tools", "file_read", "file_write" },
            latest.ToolIds.ToArray());
    }

    [TestMethod]
    public async Task Append_PermissionEpoch_RoundTrips()
    {
        var record = Record("session-a", 1, "search_tools") with { PermissionEpoch = 3 };

        await _store!.AppendAsync(record);

        var latest = await _store.GetLatestAsync("session-a");
        Assert.IsNotNull(latest);
        Assert.AreEqual(3, latest.PermissionEpoch);
        Assert.AreEqual("prefix-v1", latest.SerializationVersion);
    }

    [TestMethod]
    public async Task Append_ArgumentValidation_Throws()
    {
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(
            () => _store!.AppendAsync(Record("session-a", 0)));
        await Assert.ThrowsExactlyAsync<ArgumentNullException>(
            () => _store!.AppendAsync(null!));
    }
}
