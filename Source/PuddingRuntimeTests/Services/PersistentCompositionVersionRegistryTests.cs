using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

using PuddingCode.Runtime;
using PuddingMemoryEngine.Data;
using PuddingRuntime.Services;

namespace PuddingRuntimeTests.Services;

[TestClass]
public sealed class PersistentCompositionVersionRegistryTests
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

    private PersistentCompositionVersionRegistry NewRegistry(ICompositionStore? store) => new(store);

    /// <summary>轮询等待 store 中 session 记录数达到 expected（写穿为异步 fire-and-forget）。</summary>
    private static async Task<IReadOnlyList<SessionCompositionRecord>> WaitForCountAsync(
        ICompositionStore store,
        string sessionId,
        int expected,
        int timeoutMs = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var records = await store.LoadAsync(sessionId);
            if (records.Count >= expected)
                return records;
            await Task.Delay(20);
        }

        Assert.Fail($"store 未在 {timeoutMs}ms 内收到 {expected} 条 composition 记录（实际：{(await store.LoadAsync(sessionId)).Count}）。");
        return Array.Empty<SessionCompositionRecord>();
    }

    // ── 写穿：版本变更 → store 收到 append 记录 ──────────

    [TestMethod]
    public async Task Observe_FirstVersion_WriteThroughPersistsRecord()
    {
        var registry = NewRegistry(_store);
        var toolIds = new[] { "search_tools", "file_read" };

        var observation = registry.Observe("session-wt", "sys-hash-1", "tool-hash-1", toolIds, permissionEpoch: 2);

        var records = await WaitForCountAsync(_store!, "session-wt", 1);
        var record = records[0];
        Assert.AreEqual(1, observation.Version);
        Assert.AreEqual("initial", observation.ChangeReason);
        Assert.AreEqual(1, record.CompositionVersion);
        Assert.AreEqual("session-wt", record.SessionId);
        Assert.AreEqual("sys-hash-1", record.SystemPromptHash);
        Assert.AreEqual("tool-hash-1", record.ToolSpecHash);
        Assert.AreEqual(
            CompositionSnapshot.ComputePrefixHash("sys-hash-1", "tool-hash-1"),
            record.PrefixHash);
        Assert.IsNull(record.SkillManifestHash);
        Assert.IsNull(record.CanonicalSystemPrefixHash);
        Assert.AreEqual("prefix-v1", record.SerializationVersion);
        Assert.AreEqual(2, record.PermissionEpoch);
        Assert.AreEqual("initial", record.ChangeReason);
        CollectionAssert.AreEqual(toolIds, record.ToolIds.ToArray());
    }

    [TestMethod]
    public async Task Observe_CompositionChange_AppendsSecondVersion()
    {
        var registry = NewRegistry(_store);
        registry.Observe("session-wt", "sys-hash-1", "tool-hash-1");

        var changed = registry.Observe("session-wt", "sys-hash-2", "tool-hash-1");

        var records = await WaitForCountAsync(_store!, "session-wt", 2);
        Assert.AreEqual(2, changed.Version);
        Assert.AreEqual("system_prompt_changed", changed.ChangeReason);
        CollectionAssert.AreEqual(
            new long[] { 1, 2 },
            records.Select(r => r.CompositionVersion).ToArray());
        Assert.AreEqual("sys-hash-2", records[1].SystemPromptHash);
    }

    // ── 相同 hash 复用版本：内存命中不重复写 ────────────

    [TestMethod]
    public async Task Observe_SameHash_ReusesVersion_NoDuplicateWrite()
    {
        var registry = NewRegistry(_store);
        var toolIds = new[] { "search_tools", "file_read", "file_write" };

        var first = registry.Observe("session-hit", "sys-hash-1", "tool-hash-1", toolIds);
        await WaitForCountAsync(_store!, "session-hit", 1);

        var second = registry.Observe("session-hit", "sys-hash-1", "tool-hash-1", toolIds);
        Assert.AreEqual(1, first.Version);
        Assert.AreEqual(1, second.Version);
        Assert.AreEqual("none", second.ChangeReason);

        // 给异步写穿留出窗口，确认没有重复写。
        await Task.Delay(150);
        var records = await _store!.LoadAsync("session-hit");
        Assert.AreEqual(1, records.Count, "相同 hash 组合必须复用版本，不得重复写穿。");
    }

    // ── 无 store：降级纯内存，不抛 ──────────────────────

    [TestMethod]
    public void Observe_NoStore_DegradesToInMemory_NoThrow()
    {
        var registry = NewRegistry(store: null);

        var first = registry.Observe("session-ns", "sys-hash-1", "tool-hash-1", new[] { "a" }, 1);
        var same = registry.Observe("session-ns", "sys-hash-1", "tool-hash-1");
        var changed = registry.Observe("session-ns", "sys-hash-2", "tool-hash-1");

        Assert.AreEqual(1, first.Version);
        Assert.AreEqual("initial", first.ChangeReason);
        Assert.AreEqual(1, same.Version);
        Assert.AreEqual("none", same.ChangeReason);
        Assert.AreEqual(2, changed.Version);
        Assert.AreEqual("system_prompt_changed", changed.ChangeReason);
    }

    // ── 写穿失败（AppendAsync 抛异常/false）：不阻断 Observe ──

    [TestMethod]
    public void Observe_StoreThrows_DoesNotPropagate()
    {
        var failingStore = new ThrowingCompositionStore();
        var registry = NewRegistry(failingStore);

        var observation = registry.Observe("session-err", "sys-hash-1", "tool-hash-1");

        Assert.AreEqual(1, observation.Version);
        Assert.AreEqual("initial", observation.ChangeReason);
        // 写穿在后台失败被吞掉并降级纯内存；Observe 必须正常返回。
    }

    [TestMethod]
    public void Observe_StoreReturnsFalse_DoesNotPropagate()
    {
        var rejectingStore = new RejectingCompositionStore();
        var registry = NewRegistry(rejectingStore);

        var observation = registry.Observe("session-rej", "sys-hash-1", "tool-hash-1");

        Assert.AreEqual(1, observation.Version);
        Assert.AreEqual("initial", observation.ChangeReason);
        // AppendAsync=false 仅记日志，不抛。
    }

    // ── 双 session 相互独立写穿 ─────────────────────────

    [TestMethod]
    public async Task Observe_TwoSessions_WriteThroughIndependently()
    {
        var registry = NewRegistry(_store);

        registry.Observe("session-a", "sys-hash-1", "tool-hash-1", new[] { "a" });
        registry.Observe("session-b", "sys-hash-1", "tool-hash-1", new[] { "b" });

        var recordsA = await WaitForCountAsync(_store!, "session-a", 1);
        var recordsB = await WaitForCountAsync(_store!, "session-b", 1);
        Assert.AreEqual(1, recordsA[0].CompositionVersion);
        Assert.AreEqual(1, recordsB[0].CompositionVersion);
        CollectionAssert.AreEqual(new[] { "a" }, recordsA[0].ToolIds.ToArray());
        CollectionAssert.AreEqual(new[] { "b" }, recordsB[0].ToolIds.ToArray());
    }

    // ── 测试替身 ───────────────────────────────────────

    private sealed class ThrowingCompositionStore : ICompositionStore
    {
        public Task<SessionCompositionRecord?> GetLatestAsync(string sessionId, CancellationToken ct = default)
            => Task.FromResult<SessionCompositionRecord?>(null);

        public Task<bool> AppendAsync(SessionCompositionRecord record, CancellationToken ct = default)
            => throw new InvalidOperationException("store down");

        public Task<IReadOnlyList<SessionCompositionRecord>> LoadAsync(string sessionId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SessionCompositionRecord>>(Array.Empty<SessionCompositionRecord>());
    }

    private sealed class RejectingCompositionStore : ICompositionStore
    {
        public Task<SessionCompositionRecord?> GetLatestAsync(string sessionId, CancellationToken ct = default)
            => Task.FromResult<SessionCompositionRecord?>(null);

        public Task<bool> AppendAsync(SessionCompositionRecord record, CancellationToken ct = default)
            => Task.FromResult(false);

        public Task<IReadOnlyList<SessionCompositionRecord>> LoadAsync(string sessionId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SessionCompositionRecord>>(Array.Empty<SessionCompositionRecord>());
    }
}
