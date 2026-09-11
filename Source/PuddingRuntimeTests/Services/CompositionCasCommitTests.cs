using PuddingCode.Runtime;
using PuddingRuntime.Services;

namespace PuddingRuntimeTests.Services;

/// <summary>
/// C01-B-1 测试方案 B（C01B-AC2 CAS / AC7 失败可观察）：
/// 提交必须携带 <c>expectedRevision</c> 做 CAS；失败/冲突/不可用必须是**结构化、可观察**的结果，
/// 不得静默降级为「纯内存继续」，也不得出现两个提交者都成功。
/// </summary>
[TestClass]
public sealed class CompositionCasCommitTests
{
    /// <summary>可脚本化的 store 替身：真实实现 CAS head 比较，并可强制 Unavailable。</summary>
    private sealed class ScriptedStore : ICompositionStore
    {
        private readonly List<SessionCompositionRecord> _records = new();

        public List<long> ObservedExpectedRevisions { get; } = new();

        public CompositionAppendOutcome? ForcedOutcome { get; set; }

        public string ForcedReason { get; set; } = "scripted unavailable";

        public int RecordCount => _records.Count;

        public IReadOnlyList<SessionCompositionRecord> Records => _records;

        /// <summary>注入一条「他方提交者」已写入的记录（模拟 head 已被推进）。</summary>
        public void InjectForeignCommit(long revision, string contentId) => _records.Add(new SessionCompositionRecord
        {
            SessionId = "s1",
            CompositionVersion = revision,
            ContentId = contentId,
            SystemPromptHash = "foreign-sys",
            ToolSpecHash = "foreign-tool",
            PrefixHash = CompositionSnapshot.ComputePrefixHash("foreign-sys", "foreign-tool"),
            ToolIds = Array.Empty<string>(),
        });

        public Task<SessionCompositionRecord?> GetLatestAsync(string sessionId, CancellationToken ct = default)
            => Task.FromResult(_records.Count == 0 ? null : _records[^1]);

        public Task<CompositionAppendResult> AppendAsync(
            SessionCompositionRecord record, long expectedRevision, CancellationToken ct = default)
        {
            ObservedExpectedRevisions.Add(expectedRevision);

            if (ForcedOutcome is CompositionAppendOutcome.Unavailable)
                return Task.FromResult(CompositionAppendResult.Unavailable(ForcedReason));

            var head = _records.Count == 0 ? 0 : _records[^1].CompositionVersion;
            if (head != expectedRevision)
                return Task.FromResult(CompositionAppendResult.Conflict(expectedRevision, head));

            _records.Add(record);
            return Task.FromResult(CompositionAppendResult.Committed(record.CompositionVersion));
        }

        public Task<IReadOnlyList<SessionCompositionRecord>> LoadAsync(string sessionId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SessionCompositionRecord>>(_records.ToArray());
    }

    private static CompositionObservation Observation(long revision, string contentId) =>
        new(revision, contentId, "initial");

    [TestMethod]
    public async Task Append_StaleExpectedRevision_ReturnsConflict_NotInsert()
    {
        var store = new ScriptedStore();
        store.InjectForeignCommit(5, "cid-foreign");
        var registry = new PersistentCompositionVersionRegistry(store);

        var result = await registry.CommitAsync("s1", Observation(3, "cid-3"), "sys", "tool");

        Assert.IsTrue(result.IsConflict, "过期 expectedRevision 必须得到明确 Conflict，而不是静默插入。");
        Assert.AreEqual(0L, result.ExpectedRevision);
        Assert.AreEqual(5L, result.ActualRevision);
        Assert.AreEqual(1, store.RecordCount, "Conflict 不得写入新记录（append-only 只插入合法 revision）。");
        Assert.IsTrue(store.ObservedExpectedRevisions.Contains(0L), "CAS 必须携带 expectedRevision（不得用 MAX 后 INSERT）。");
    }

    [TestMethod]
    public async Task Append_TwoCommitters_SameExpectedRevision_ExactlyOneWins()
    {
        var store = new ScriptedStore();
        var committerA = new PersistentCompositionVersionRegistry(store);
        var committerB = new PersistentCompositionVersionRegistry(store);

        // 两个提交者各自认为 head=0，都想提交 revision 1。
        var a = await committerA.CommitAsync("s1", Observation(1, "cid-A"), "sys-A", "tool-A");
        var b = await committerB.CommitAsync("s1", Observation(1, "cid-B"), "sys-B", "tool-B");

        Assert.IsTrue(a.IsCommitted, "第一个提交者必须成功。");
        Assert.IsTrue(b.IsConflict, "第二个提交者必须得到 Conflict（不得两个都成功，也不得抛未处理异常）。");
        Assert.AreEqual(1L, b.ActualRevision);
        Assert.AreEqual(1, store.RecordCount, "恰好一条记录落库。");
    }

    [TestMethod]
    public async Task Append_StoreBusy_ReturnsRetryableUnavailable_RequestNotSent()
    {
        var store = new ScriptedStore { ForcedOutcome = CompositionAppendOutcome.Unavailable, ForcedReason = "database is locked" };
        var registry = new PersistentCompositionVersionRegistry(store);

        var result = await registry.CommitAsync("s1", Observation(1, "cid-1"), "sys", "tool");

        Assert.IsTrue(result.IsUnavailable, "store busy 必须得到 Unavailable。");
        StringAssert.Contains(
            result.FailureReason ?? string.Empty,
            CompositionAppendResult.UnavailableErrorCode,
            "对外语义必须是可重试的 composition_commit_unavailable。");
        Assert.IsFalse(result.IsCommitted, "store busy 绝不伪装已提交。");

        // 只有 IsCommitted 才允许发出 Provider 请求。
        var providerCallSent = result.IsCommitted;
        Assert.IsFalse(providerCallSent, "未提交成功时不得发出 Provider 请求。");
        Assert.AreEqual(0, store.RecordCount);
    }

    [TestMethod]
    public async Task Commit_Conflict_RecomputesProposedComposition_BeforeRetry()
    {
        var store = new ScriptedStore();
        store.InjectForeignCommit(5, "cid-foreign");
        var registry = new PersistentCompositionVersionRegistry(store);

        var conflict = await registry.CommitAsync("s1", Observation(1, "cid-1"), "sys", "tool");
        Assert.IsTrue(conflict.IsConflict);

        // 冲突后重读 head 并重算 proposed composition：revision 必须从实际 head 之后继续。
        var recomputed = registry.Observe("s1", "sys", "tool");
        Assert.AreEqual(6L, recomputed.Revision, "冲突重读后必须从 actual head(5) 之后重算 revision。");

        var retry = await registry.CommitAsync("s1", recomputed, "sys", "tool");
        Assert.IsTrue(retry.IsCommitted, "重算后的提交必须成功。");
        Assert.AreEqual(6L, retry.Revision);
    }

    [TestMethod]
    public async Task Commit_BeforeProviderCall_CurrentRequestBindsCommittedRevision()
    {
        var store = new ScriptedStore();
        var registry = new PersistentCompositionVersionRegistry(store);

        var committed = await registry.CommitAsync("s1", Observation(1, "cid-1"), "sys", "tool");

        // 提交点先于 Provider 调用：只有提交成功才发请求，且请求绑定已提交 revision。
        var providerCalled = false;
        long boundRevision = -1;
        if (committed.IsCommitted)
        {
            providerCalled = true;
            boundRevision = committed.Revision;
        }

        Assert.IsTrue(providerCalled, "提交成功后才允许 Provider 调用。");
        Assert.AreEqual(1L, boundRevision);
        Assert.AreEqual(store.Records[0].CompositionVersion, boundRevision, "当前请求必须绑定已提交 revision。");

        // 提交前崩溃（store 不可用）⇒ 不发请求；提交后崩溃可从 store 恢复该 revision。
        var failingStore = new ScriptedStore { ForcedOutcome = CompositionAppendOutcome.Unavailable };
        var failingRegistry = new PersistentCompositionVersionRegistry(failingStore);
        var failed = await failingRegistry.CommitAsync("s1", Observation(1, "cid-1"), "sys", "tool");
        var crashedProviderCalled = failed.IsCommitted;
        Assert.IsFalse(crashedProviderCalled, "提交前崩溃不得发出模型请求。");
        Assert.AreEqual(1, store.RecordCount, "已提交的 revision 必须可从 store 恢复。");
    }
}
