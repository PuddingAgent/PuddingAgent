using PuddingCode.Runtime;
using PuddingRuntime.Services;

namespace PuddingRuntimeTests.Services;

/// <summary>
/// P0-5 缺陷修复：<see cref="CompositionRecoveryService.RecoverAsync"/> 串联版本恢复
/// 与工具集合恢复；版本恢复失败不阻断工具恢复。
/// </summary>
[TestClass]
public sealed class CompositionRecoveryServiceRecoveryTests
{
    private static SessionCompositionRecord Record(long version, string sysHash, string toolHash, params string[] toolIds) => new()
    {
        SessionId = "s1",
        CompositionVersion = version,
        SystemPromptHash = sysHash,
        ToolSpecHash = toolHash,
        PrefixHash = CompositionSnapshot.ComputePrefixHash(sysHash, toolHash),
        ToolIds = toolIds,
    };

    [TestMethod]
    public async Task RecoverAsync_RestoresVersionsAndToolIds_Together()
    {
        var manager = new AgentSessionManager();
        var store = new DualStore();
        store.Add(
            Record(1, "sys-a", "tool-a", "file_read"),
            Record(2, "sys-b", "tool-a", "file_read"),
            Record(3, "sys-b", "tool-a", "file_read", "search_grep"));

        var persistentRegistry = new PersistentCompositionVersionRegistry(store);
        var service = new CompositionRecoveryService(manager, store, persistentRegistry: persistentRegistry);

        await service.RecoverAsync("s1");

        // 版本恢复：同组合复用已存版本（3），不触发写穿。
        var observation = persistentRegistry.Observe("s1", "sys-b", "tool-a");
        Assert.AreEqual(3, observation.Version);
        await Task.Delay(150);
        Assert.AreEqual(0, store.AppendCount, "恢复后同组合不得触发写穿。");

        // 工具集合恢复：append-only 水合最新 ToolIds。
        CollectionAssert.AreEquivalent(
            new[] { "file_read", "search_grep" },
            manager.GetLoadedToolIds("s1").ToArray());
    }

    [TestMethod]
    public async Task RecoverAsync_VersionRecoveryFailure_DoesNotBlockToolHydration()
    {
        var manager = new AgentSessionManager();
        var inner = new DualStore();
        inner.Add(Record(1, "sys-a", "tool-a", "file_read"));
        var store = new ThrowingLoadStore(inner); // LoadAsync 抛、GetLatestAsync 正常

        var persistentRegistry = new PersistentCompositionVersionRegistry(store);
        var service = new CompositionRecoveryService(manager, store, persistentRegistry: persistentRegistry);

        await service.RecoverAsync("s1"); // 版本恢复失败静默，工具恢复继续

        CollectionAssert.AreEquivalent(
            new[] { "file_read" },
            manager.GetLoadedToolIds("s1").ToArray());
    }

    // ── 测试替身 ───────────────────────────────────────

    private sealed class DualStore : ICompositionStore
    {
        private readonly List<SessionCompositionRecord> _records = new();

        public int AppendCount { get; private set; }

        public void Add(params SessionCompositionRecord[] records) => _records.AddRange(records);

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
        private readonly ICompositionStore _inner;

        public ThrowingLoadStore(ICompositionStore inner) => _inner = inner;

        public Task<SessionCompositionRecord?> GetLatestAsync(string sessionId, CancellationToken ct = default)
            => _inner.GetLatestAsync(sessionId, ct);

        public Task<bool> AppendAsync(SessionCompositionRecord record, CancellationToken ct = default)
            => _inner.AppendAsync(record, ct);

        public Task<IReadOnlyList<SessionCompositionRecord>> LoadAsync(string sessionId, CancellationToken ct = default)
            => throw new InvalidOperationException("load boom");
    }
}
