using PuddingCode.Runtime;
using PuddingRuntime.Services;

namespace PuddingRuntimeTests.Services;

/// <summary>
/// C01-A：Composition 恢复的 single-flight、取消传播与「失败有明确状态」。
///
/// 修复前：每次调用都全量重载该 session 全部记录（无去重）、以 CancellationToken.None 调用外部依赖、
/// 失败只 LogWarning 后静默降级为空集合即视为成功。
/// </summary>
[TestClass]
public sealed class CompositionRecoverySingleFlightTests
{
    [TestMethod]
    public async Task RecoverAsync_ConcurrentCalls_SingleFlight_LoadsStoreOnce()
    {
        var store = new CountingStore(CompositionRecord("s1", "file_read", "search_grep"));
        var registry = new PersistentCompositionVersionRegistry(store);
        var manager = new AgentSessionManager();
        var service = new CompositionRecoveryService(manager, store, persistentRegistry: registry);

        var results = await Task.WhenAll(
            Enumerable.Range(0, 10).Select(_ => service.RecoverAsync("s1")));

        Assert.HasCount(10, results);
        Assert.IsTrue(results.All(result => !result.IsFailure));
        Assert.AreEqual(1, store.LoadCount, "并发恢复必须 single-flight：版本全量加载只执行一次。");
        Assert.AreEqual(1, store.GetLatestCount, "并发恢复必须 single-flight：最新记录读取只执行一次。");
        CollectionAssert.AreEquivalent(
            new[] { "file_read", "search_grep" },
            manager.GetLoadedToolIds("s1").ToArray());
    }

    [TestMethod]
    public async Task RecoverAsync_AfterCompletion_NextCallReloadsOnce()
    {
        var store = new CountingStore(CompositionRecord("s1", "file_read"));
        var registry = new PersistentCompositionVersionRegistry(store);
        var service = new CompositionRecoveryService(new AgentSessionManager(), store, persistentRegistry: registry);

        var first = await service.RecoverAsync("s1");
        var second = await service.RecoverAsync("s1");

        Assert.AreEqual(CompositionRecoveryStatus.Recovered, first.Status);
        Assert.AreEqual(CompositionRecoveryStatus.Recovered, second.Status);
        Assert.AreEqual(2, store.LoadCount, "single-flight 只去重并发调用；串行调用按新一轮恢复处理。");
    }

    [TestMethod]
    public async Task RecoverAsync_CallerCancelled_DoesNotFakeSuccess()
    {
        var store = new CountingStore(CompositionRecord("s1", "file_read"));
        var registry = new PersistentCompositionVersionRegistry(store);
        var manager = new AgentSessionManager();
        var service = new CompositionRecoveryService(manager, store, persistentRegistry: registry);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        OperationCanceledException? thrown = null;
        try
        {
            await service.RecoverAsync("s1", cts.Token);
        }
        catch (OperationCanceledException ex)
        {
            thrown = ex;
        }

        Assert.IsNotNull(
            thrown,
            "调用方取消必须可观察（应抛 OperationCanceledException 或其派生类型），不得伪装成成功。");
    }

    [TestMethod]
    public async Task RecoverAsync_StoreThrows_ReturnsFailedStatusWithReason()
    {
        var store = new CountingStore(CompositionRecord("s1", "file_read")) { ThrowOnRead = new InvalidOperationException("store busy") };
        var registry = new PersistentCompositionVersionRegistry(store);
        var manager = new AgentSessionManager();
        var service = new CompositionRecoveryService(manager, store, persistentRegistry: registry);

        var result = await service.RecoverAsync("s1");

        Assert.AreEqual(CompositionRecoveryStatus.Failed, result.Status);
        Assert.IsTrue(result.IsFailure);
        Assert.IsFalse(result.ToolsHydrated, "失败时不得声称工具已水合。");
        Assert.IsNotNull(result.FailureReason);
        StringAssert.Contains(result.FailureReason, "store busy");
    }

    [TestMethod]
    public async Task RecoverAsync_VersionRecoveryFailure_ToolsStillHydrated_StatusReportsFailure()
    {
        var store = new CountingStore(CompositionRecord("s1", "file_read")) { ThrowOnLoad = new InvalidOperationException("load boom") };
        var registry = new PersistentCompositionVersionRegistry(store);
        var manager = new AgentSessionManager();
        var service = new CompositionRecoveryService(manager, store, persistentRegistry: registry);

        var result = await service.RecoverAsync("s1");

        Assert.IsTrue(result.ToolsHydrated, "版本恢复失败不得阻断工具集合水合。");
        Assert.IsTrue(result.IsFailure, "部分成功仍需如实上报失败状态，不得静默降级为成功。");
        StringAssert.Contains(result.FailureReason!, "load boom");
        CollectionAssert.AreEquivalent(
            new[] { "file_read" },
            manager.GetLoadedToolIds("s1").ToArray());
    }

    [TestMethod]
    public async Task RecoverAsync_NoSource_ReturnsSkipped()
    {
        var service = new CompositionRecoveryService(new AgentSessionManager());

        var result = await service.RecoverAsync("s1");

        Assert.AreEqual(CompositionRecoveryStatus.Skipped, result.Status);
        Assert.IsFalse(result.IsFailure);
    }

    [TestMethod]
    public async Task RecoverAsync_EmptySessionId_ReturnsSkipped()
    {
        var store = new CountingStore(CompositionRecord("s1", "file_read"));
        var service = new CompositionRecoveryService(new AgentSessionManager(), store);

        var result = await service.RecoverAsync("   ");

        Assert.AreEqual(CompositionRecoveryStatus.Skipped, result.Status);
        Assert.AreEqual(0, store.LoadCount);
        Assert.AreEqual(0, store.GetLatestCount);
    }

    // ── 测试替身 ───────────────────────────────────────

    private static SessionCompositionRecord CompositionRecord(string sessionId, params string[] toolIds) => new()
    {
        SessionId = sessionId,
        CompositionVersion = 1,
        SystemPromptHash = "sys",
        ToolSpecHash = "tool",
        PrefixHash = CompositionSnapshot.ComputePrefixHash("sys", "tool"),
        ToolIds = toolIds,
    };

    /// <summary>真实异步 IO 语义（含 await 让出）的 store：让并发调用真正重叠，并统计读写次数。</summary>
    private sealed class CountingStore : ICompositionStore
    {
        private readonly List<SessionCompositionRecord> _records = new();

        public CountingStore(params SessionCompositionRecord[] records) => _records.AddRange(records);

        public int LoadCount { get; private set; }

        public int GetLatestCount { get; private set; }

        public Exception? ThrowOnRead { get; set; }

        public Exception? ThrowOnLoad { get; set; }

        public async Task<SessionCompositionRecord?> GetLatestAsync(string sessionId, CancellationToken ct = default)
        {
            GetLatestCount++;
            await Task.Delay(30, ct);
            if (ThrowOnRead is not null)
                throw ThrowOnRead;
            return _records.Count == 0 ? null : _records[^1];
        }

        public Task<bool> AppendAsync(SessionCompositionRecord record, CancellationToken ct = default)
        {
            _records.Add(record);
            return Task.FromResult(true);
        }

        public async Task<IReadOnlyList<SessionCompositionRecord>> LoadAsync(string sessionId, CancellationToken ct = default)
        {
            LoadCount++;
            await Task.Delay(30, ct);
            if (ThrowOnLoad is not null)
                throw ThrowOnLoad;
            return _records.ToArray();
        }
    }
}
