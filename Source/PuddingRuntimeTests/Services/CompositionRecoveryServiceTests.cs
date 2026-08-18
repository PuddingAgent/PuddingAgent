using PuddingCode.Runtime;
using PuddingRuntime.Services;

namespace PuddingRuntimeTests.Services;

[TestClass]
public sealed class CompositionRecoveryServiceTests
{
    private static SessionCompositionRecord Record(string sessionId, params string[] toolIds) => new()
    {
        SessionId = sessionId,
        CompositionVersion = 1,
        SystemPromptHash = "sys",
        ToolSpecHash = "tool",
        PrefixHash = "prefix",
        ToolIds = toolIds,
    };

    [TestMethod]
    public async Task RecoverAsync_HydratesToolIds_FromStore()
    {
        var manager = new AgentSessionManager();
        var store = new FakeCompositionStore();
        store.Add(Record("s1", "file_read", "search_grep"));
        var service = new CompositionRecoveryService(manager, store);

        await service.RecoverAsync("s1");

        CollectionAssert.AreEquivalent(
            new[] { "file_read", "search_grep" },
            manager.GetLoadedToolIds("s1").ToArray());
    }

    [TestMethod]
    public async Task RecoverAsync_AppendOnly_DoesNotShrinkExisting()
    {
        var manager = new AgentSessionManager();
        manager.GetOrCreate("s1", "global:general-assistant");
        manager.RememberLoadedToolIds("s1", ["file_read"]); // 进程内已存在

        var store = new FakeCompositionStore();
        store.Add(Record("s1", "file_read", "search_grep", "file_write"));
        var service = new CompositionRecoveryService(manager, store);

        await service.RecoverAsync("s1");

        CollectionAssert.AreEquivalent(
            new[] { "file_read", "search_grep", "file_write" },
            manager.GetLoadedToolIds("s1").ToArray());
    }

    [TestMethod]
    public async Task RecoverAsync_NoRecord_IsNoOp()
    {
        var manager = new AgentSessionManager();
        var store = new FakeCompositionStore();
        var service = new CompositionRecoveryService(manager, store);

        await service.RecoverAsync("unknown");

        Assert.IsEmpty(manager.GetLoadedToolIds("unknown"));
    }

    [TestMethod]
    public async Task RecoverAsync_EmptyToolIds_IsNoOp()
    {
        var manager = new AgentSessionManager();
        var store = new FakeCompositionStore();
        store.Add(Record("s1"));
        var service = new CompositionRecoveryService(manager, store);

        await service.RecoverAsync("s1");

        Assert.IsEmpty(manager.GetLoadedToolIds("s1"));
    }

    [TestMethod]
    public async Task RecoverAsync_StoreThrows_DoesNotPropagate()
    {
        var manager = new AgentSessionManager();
        var store = new FakeCompositionStore { ThrowOnGet = new InvalidOperationException("boom") };
        var service = new CompositionRecoveryService(manager, store);

        await service.RecoverAsync("s1"); // 不抛，静默降级

        Assert.IsEmpty(manager.GetLoadedToolIds("s1"));
    }

    [TestMethod]
    public async Task RecoverAsync_NullStore_IsNoOp()
    {
        var manager = new AgentSessionManager();
        var service = new CompositionRecoveryService(manager, compositionStore: null);

        await service.RecoverAsync("s1");

        Assert.IsEmpty(manager.GetLoadedToolIds("s1"));
    }

    private sealed class FakeCompositionStore : ICompositionStore
    {
        private readonly Dictionary<string, SessionCompositionRecord> _latest = new();

        public Exception? ThrowOnGet { get; set; }

        public void Add(SessionCompositionRecord record) => _latest[record.SessionId] = record;

        public Task<SessionCompositionRecord?> GetLatestAsync(string sessionId, CancellationToken ct = default)
        {
            if (ThrowOnGet is not null)
                throw ThrowOnGet;
            return Task.FromResult(_latest.GetValueOrDefault(sessionId));
        }

        public Task<bool> AppendAsync(SessionCompositionRecord record, CancellationToken ct = default)
            => Task.FromResult(false);

        public Task<IReadOnlyList<SessionCompositionRecord>> LoadAsync(string sessionId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SessionCompositionRecord>>(Array.Empty<SessionCompositionRecord>());
    }
}
