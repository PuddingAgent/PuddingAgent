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

        // C01-B：种子抬高 revision 下界（已持久化 max=3）⇒ 本次观测得 max+1=4；
        // 内容身份仍复用 v2/v3 的 ContentId（内容可复用 ≠ revision 可复用），故仍写穿一次。
        var observation = persistentRegistry.Observe("s1", "sys-b", "tool-a");
        Assert.AreEqual(4, observation.Revision);
        await Task.Delay(150);
        Assert.AreEqual(1, store.AppendCount, "C01-B：新 revision 必须写穿一次（内容复用不豁免写穿）。");

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

        public Task<CompositionAppendResult> AppendAsync(SessionCompositionRecord record, long expectedRevision, CancellationToken ct = default)
        {
            AppendCount++;
            _records.Add(record);
            return Task.FromResult(CompositionAppendResult.Committed(record.CompositionVersion));
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

        public Task<CompositionAppendResult> AppendAsync(SessionCompositionRecord record, long expectedRevision, CancellationToken ct = default)
            => _inner.AppendAsync(record, expectedRevision, ct);

        public Task<IReadOnlyList<SessionCompositionRecord>> LoadAsync(string sessionId, CancellationToken ct = default)
            => throw new InvalidOperationException("load boom");
    }
}
