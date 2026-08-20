using PuddingCode.Runtime;
using PuddingRuntime.Services;

namespace PuddingRuntimeTests.Services;

/// <summary>
/// P0-5 缺陷修复：<see cref="PersistentCompositionVersionRegistry.RecoverFromStoreAsync"/>
/// 从 store 恢复已持久化版本，保证重启后写穿继续单调递增（不再 append rejected）。
/// </summary>
[TestClass]
public sealed class PersistentCompositionVersionRegistryRecoveryTests
{
    private static SessionCompositionRecord Record(long version, string sysHash = "sys-a", string toolHash = "tool-a")
        => new()
        {
            SessionId = "s1",
            CompositionVersion = version,
            SystemPromptHash = sysHash,
            ToolSpecHash = toolHash,
            PrefixHash = CompositionSnapshot.ComputePrefixHash(sysHash, toolHash),
            ToolIds = new[] { "file_read" },
        };

    /// <summary>
    /// v1..v10 已持久化记录：v1 组合为 (sys-a/tool-a)（与恢复后首次观察的组合一致），
    /// v2..v10 为另一组合（sys-b/tool-a）——模拟真实多组合版本账本，
    /// 验证「同组合复用已存版本（1）、新组合从 max+1（11）继续」的语义。
    /// </summary>
    private static SessionCompositionRecord[] RecordsV1ToV10()
        => Enumerable.Range(1, 10)
            .Select(v => v == 1
                ? Record(1, "sys-a", "tool-a")
                : Record(v, "sys-b", "tool-a"))
            .ToArray();

    [TestMethod]
    public async Task RecoverFromStoreAsync_SameCombo_ReusesVersion_NoAppend()
    {
        var store = new RecordingCompositionStore(RecordsV1ToV10());
        var registry = new PersistentCompositionVersionRegistry(store);

        await registry.RecoverFromStoreAsync("s1");

        // 同组合（sys-a/tool-a，已持久化 v1）→ 复用 v1，而不是重新从 1 分配后触发写穿。
        var observation = registry.Observe("s1", "sys-a", "tool-a");
        Assert.AreEqual(1, observation.Version);

        // 写穿为异步 fire-and-forget：留窗口确认未触发 AppendAsync。
        await Task.Delay(150);
        Assert.AreEqual(0, store.AppendCount, "恢复后同组合不得触发写穿（版本未超 persisted max）。");
    }

    [TestMethod]
    public async Task RecoverFromStoreAsync_NewCombo_ReturnsMaxPlusOne_AppendsOnce()
    {
        var store = new RecordingCompositionStore(RecordsV1ToV10());
        var registry = new PersistentCompositionVersionRegistry(store);

        await registry.RecoverFromStoreAsync("s1");

        // 新组合 → 从 max+1=11 继续。
        var observation = registry.Observe("s1", "sys-new", "tool-a");
        Assert.AreEqual(11, observation.Version);

        // 等待异步写穿完成（AppendAsync 返回 true → _persistedVersions 更新为 11）。
        var deadline = DateTime.UtcNow.AddMilliseconds(3000);
        while (store.AppendCount < 1 && DateTime.UtcNow < deadline)
            await Task.Delay(20);

        Assert.AreEqual(1, store.AppendCount);
        Assert.AreEqual(11, store.Records[^1].CompositionVersion);

        // 再次同组合 → 复用 v11，不再写穿。
        var again = registry.Observe("s1", "sys-new", "tool-a");
        Assert.AreEqual(11, again.Version);
        await Task.Delay(150);
        Assert.AreEqual(1, store.AppendCount, "同组合复用版本后不得重复写穿。");
    }

    [TestMethod]
    public async Task RecoverFromStoreAsync_NullStore_IsNoOp()
    {
        var registry = new PersistentCompositionVersionRegistry(store: null);

        await registry.RecoverFromStoreAsync("s1"); // 不抛

        // 纯内存降级：未恢复，从 1 开始。
        Assert.AreEqual(1, registry.Observe("s1", "sys-a", "tool-a").Version);
    }

    [TestMethod]
    public async Task RecoverFromStoreAsync_EmptyRecords_IsNoOp()
    {
        var store = new RecordingCompositionStore();
        var registry = new PersistentCompositionVersionRegistry(store);

        await registry.RecoverFromStoreAsync("s1");

        Assert.AreEqual(1, registry.Observe("s1", "sys-a", "tool-a").Version);
    }

    [TestMethod]
    public async Task RecoverFromStoreAsync_StoreThrows_DoesNotPropagate()
    {
        var store = new ThrowingLoadStore();
        var registry = new PersistentCompositionVersionRegistry(store);

        await registry.RecoverFromStoreAsync("s1"); // 不抛，静默降级

        Assert.AreEqual(1, registry.Observe("s1", "sys-a", "tool-a").Version);
    }

    // ── 测试替身 ───────────────────────────────────────

    /// <summary>记录 AppendAsync 调用次数与记录列表的 mock store。</summary>
    private sealed class RecordingCompositionStore : ICompositionStore
    {
        private readonly List<SessionCompositionRecord> _records = new();

        public RecordingCompositionStore(params SessionCompositionRecord[] records)
            => _records.AddRange(records);

        public int AppendCount { get; private set; }

        public IReadOnlyList<SessionCompositionRecord> Records => _records;

        public Task<SessionCompositionRecord?> GetLatestAsync(string sessionId, CancellationToken ct = default)
            => Task.FromResult(_records.Count == 0 ? null : _records[^1]);

        public Task<bool> AppendAsync(SessionCompositionRecord record, CancellationToken ct = default)
        {
            AppendCount++;
            _records.Add(record);
            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<SessionCompositionRecord>> LoadAsync(string sessionId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SessionCompositionRecord>>(_records.ToArray());
    }

    private sealed class ThrowingLoadStore : ICompositionStore
    {
        public Task<SessionCompositionRecord?> GetLatestAsync(string sessionId, CancellationToken ct = default)
            => throw new InvalidOperationException("boom");

        public Task<bool> AppendAsync(SessionCompositionRecord record, CancellationToken ct = default)
            => throw new InvalidOperationException("boom");

        public Task<IReadOnlyList<SessionCompositionRecord>> LoadAsync(string sessionId, CancellationToken ct = default)
            => throw new InvalidOperationException("boom");
    }
}
