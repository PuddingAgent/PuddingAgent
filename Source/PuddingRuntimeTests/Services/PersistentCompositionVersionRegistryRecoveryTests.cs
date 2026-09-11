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
    /// 验证「恢复后 revision 下界抬到已持久化 max，后续观测从 max+1 继续（revision 不复用）」
    /// 且「内容身份 ContentId 仍按组合复用」的 C01-B 语义。
    /// </summary>
    private static SessionCompositionRecord[] RecordsV1ToV10()
        => Enumerable.Range(1, 10)
            .Select(v => v == 1
                ? Record(1, "sys-a", "tool-a")
                : Record(v, "sys-b", "tool-a"))
            .ToArray();

    [TestMethod]
    public async Task RecoverFromStoreAsync_SameCombo_AdvancesRevision_AppendsOnce()
    {
        var store = new RecordingCompositionStore(RecordsV1ToV10());
        var registry = new PersistentCompositionVersionRegistry(store);

        await registry.RecoverFromStoreAsync("s1");

        // 同组合（sys-a/tool-a，已持久化 v1）→ 复用 v1，而不是重新从 1 分配后触发写穿。
        var observation = registry.Observe("s1", "sys-a", "tool-a");
        Assert.AreEqual(11, observation.Revision);

                // 写穿为异步 fire-and-forget：留窗口确认本次观测已写穿一次
        // （C01-B：每次观测都是新 revision ⇒ 恢复后同内容也会再写穿一次）。
        await Task.Delay(150);
        Assert.AreEqual(1, store.AppendCount, "C01-B：每次观测都是新 revision ⇒ 恢复后同组合也会写穿一次（内容可复用 ≠ revision 可复用）。");
    }

    [TestMethod]
    public async Task RecoverFromStoreAsync_NewCombo_ReturnsMaxPlusOne_AppendsOnce()
    {
        var store = new RecordingCompositionStore(RecordsV1ToV10());
        var registry = new PersistentCompositionVersionRegistry(store);

        await registry.RecoverFromStoreAsync("s1");

        // 新组合 → 从 max+1=11 继续。
        var observation = registry.Observe("s1", "sys-new", "tool-a");
        Assert.AreEqual(11, observation.Revision);

        // 等待异步写穿完成（AppendAsync 返回 true → _persistedVersions 更新为 11）。
        var deadline = DateTime.UtcNow.AddMilliseconds(3000);
        while (store.AppendCount < 1 && DateTime.UtcNow < deadline)
            await Task.Delay(20);

        Assert.AreEqual(1, store.AppendCount);
        Assert.AreEqual(11, store.Records[^1].CompositionVersion);

                // C01-B：同内容再次观测 → revision 继续递增到 12，ContentId 复用不变。
        var again = registry.Observe("s1", "sys-new", "tool-a");
        Assert.AreEqual(12, again.Revision);
        Assert.AreEqual(observation.ContentId, again.ContentId, "内容相同 ⇒ ContentId 必须复用（与 revision 无关）。");
        await Task.Delay(150);
        Assert.AreEqual(2, store.AppendCount, "两个 revision 各自写穿一次（revision 不复用）。");
    }

    [TestMethod]
    public async Task RecoverFromStoreAsync_NullStore_IsNoOp()
    {
        var registry = new PersistentCompositionVersionRegistry(store: null);

        await registry.RecoverFromStoreAsync("s1"); // 不抛

        // 纯内存降级：未恢复，从 1 开始。
        Assert.AreEqual(1, registry.Observe("s1", "sys-a", "tool-a").Revision);
    }

    [TestMethod]
    public async Task RecoverFromStoreAsync_EmptyRecords_IsNoOp()
    {
        var store = new RecordingCompositionStore();
        var registry = new PersistentCompositionVersionRegistry(store);

        await registry.RecoverFromStoreAsync("s1");

        Assert.AreEqual(1, registry.Observe("s1", "sys-a", "tool-a").Revision);
    }

    [TestMethod]
    public async Task RecoverFromStoreAsync_StoreThrows_DoesNotPropagate()
    {
        var store = new ThrowingLoadStore();
        var registry = new PersistentCompositionVersionRegistry(store);

        await registry.RecoverFromStoreAsync("s1"); // 不抛，静默降级

        Assert.AreEqual(1, registry.Observe("s1", "sys-a", "tool-a").Revision);
    }

    /// <summary>
    /// LoadAsync 返回记录 → Seed 被调用（同组合复用已存版本）且 _persistedVersions 同步到
    /// 已持久化 max（新组合 v11 才触发写穿且不被 append-only 拒绝）。
    /// </summary>
    [TestMethod]
    public async Task RecoverFromStoreAsync_LoadsRecords_SeedsRegistry_AndSyncsPersistedVersion()
    {
        var store = new RecordingCompositionStore(RecordsV1ToV10());
        var registry = new PersistentCompositionVersionRegistry(store);

        await registry.RecoverFromStoreAsync("s1");

                // C01-B：Seed 抬高 revision 下界（已持久化 max=10）⇒ 首次观测得 max+1=11（revision 不复用）。
        Assert.AreEqual(11, registry.Observe("s1", "sys-a", "tool-a").Revision);

        // _persistedVersions 已同步到 max=10：新组合 v11 才触发写穿且成功。
        var observation = registry.Observe("s1", "sys-new", "tool-a");
        Assert.AreEqual(12, observation.Revision);
        var deadline = DateTime.UtcNow.AddMilliseconds(3000);
        while (store.AppendCount < 1 && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        Assert.AreEqual(2, store.AppendCount);
        Assert.AreEqual(12, store.Records[^1].CompositionVersion);

                // C01-B：同一内容第三次观测 → revision 13（ContentId 复用不变），并第三次写穿。
        var repeat = registry.Observe("s1", "sys-new", "tool-a");
        Assert.AreEqual(13, repeat.Revision);
        Assert.AreEqual(observation.ContentId, repeat.ContentId, "内容相同 ⇒ ContentId 必须复用。");
        await Task.Delay(150);
        Assert.AreEqual(3, store.AppendCount, "C01-B：每次观测都是新 revision ⇒ 每次都写穿。");
    }

    /// <summary>LoadAsync 抛 OperationCanceledException（无取消 token）：仍静默返回，不抛。</summary>
    [TestMethod]
    public async Task RecoverFromStoreAsync_StoreCancels_DoesNotPropagate()
    {
        var store = new CancelingLoadStore();
        var registry = new PersistentCompositionVersionRegistry(store);

        await registry.RecoverFromStoreAsync("s1"); // 不抛，静默降级

        Assert.AreEqual(1, registry.Observe("s1", "sys-a", "tool-a").Revision);
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
        public Task<SessionCompositionRecord?> GetLatestAsync(string sessionId, CancellationToken ct = default)
            => throw new InvalidOperationException("boom");

        public Task<CompositionAppendResult> AppendAsync(SessionCompositionRecord record, long expectedRevision, CancellationToken ct = default)
            => throw new InvalidOperationException("boom");

        public Task<IReadOnlyList<SessionCompositionRecord>> LoadAsync(string sessionId, CancellationToken ct = default)
            => throw new InvalidOperationException("boom");
    }

    /// <summary>LoadAsync 抛 OperationCanceledException 的 store（模拟调用方取消/下游取消）。</summary>
    private sealed class CancelingLoadStore : ICompositionStore
    {
        public Task<SessionCompositionRecord?> GetLatestAsync(string sessionId, CancellationToken ct = default)
            => throw new OperationCanceledException(ct);

        public Task<CompositionAppendResult> AppendAsync(SessionCompositionRecord record, long expectedRevision, CancellationToken ct = default)
            => throw new OperationCanceledException(ct);

        public Task<IReadOnlyList<SessionCompositionRecord>> LoadAsync(string sessionId, CancellationToken ct = default)
            => throw new OperationCanceledException(ct);
    }
}
