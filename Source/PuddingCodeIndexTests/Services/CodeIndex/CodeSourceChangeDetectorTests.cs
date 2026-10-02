using PuddingCodeIndex.Contracts;
using PuddingCodeIndex.Services.CodeIndex;

namespace PuddingCodeIndexTests.Services.CodeIndex;

/// <summary>
/// D2（2026-10-02 高磁盘读取修复）第一阶段的纯逻辑门禁：**源指纹 + 三源提示 + manifest + 消费者输入
/// 指纹 → 真实变更集**。
/// <para>
/// 判定过程不读文件、不访问数据库、不看时钟，因此每条不变量都可以用最小夹具精确锁定：
/// 哪些路径真的需要读正文/解析（<see cref="CodeSourceAction.ReindexContent"/>）、哪些只需重新绑定、
/// 哪些可以复用已提交结果、哪些本轮不能定论（尤其**绝不能**误删除）。
/// </para>
/// </summary>
[TestClass]
public sealed class CodeSourceChangeDetectorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 2, 0, 0, 0, TimeSpan.Zero);

    private const string Root = @"C:\repo";

    // ── 夹具 ────────────────────────────────────────────────────────────────────────────────────────

    private static string Path(string relative) => $@"{Root}\{relative}";

    private static SourceFingerprint Fingerprint(int minutes, long length, string hash) =>
        new(T0.AddMinutes(minutes), length, hash);

    private static CodeSourceObservation Observation(string path, int minutes, long length) =>
        new(path, T0.AddMinutes(minutes), length);

    private static CodeSourceEntry Entry(
        string path,
        SourceFingerprint? fingerprint,
        params AppliedFileVersion[] applied) =>
        new(path, fingerprint, applied);

    private static AppliedFileVersion Applied(
        string provider = "csharp",
        string policy = "policy-1",
        string semantic = "semantic-1",
        long version = 1) =>
        new(provider, policy, semantic, version);

    private static CodeConsumerInputFingerprint Input(
        string provider = "csharp",
        string policy = "policy-1",
        string semantic = "semantic-1") =>
        new(provider, policy, semantic);

    private static Dictionary<string, CodeSourceEntry> Manifest(params CodeSourceEntry[] entries)
    {
        var map = new Dictionary<string, CodeSourceEntry>(StringComparer.Ordinal);
        foreach (var entry in entries)
            map[entry.FilePath] = entry;
        return map;
    }

    private static CodeSourceChangeSet Detect(
        IReadOnlyList<CodeSourceObservation> observations,
        IReadOnlyDictionary<string, CodeSourceEntry>? manifest = null,
        IReadOnlyDictionary<string, SourceFingerprint>? hashes = null,
        IReadOnlyCollection<string>? hints = null,
        IReadOnlyCollection<string>? changedDuringScan = null,
        IReadOnlyCollection<CodeConsumerInputFingerprint>? consumerInputs = null,
        DateTimeOffset? scanStarted = null,
        DateTimeOffset? previousWatermark = null,
        TimeSpan? racyOverlap = null,
        bool deepVerify = false,
        bool scopeComplete = true,
        bool rootUsable = true) =>
        CodeSourceChangeDetector.Detect(new CodeSourceScanRequest(
            observations,
            manifest ?? new Dictionary<string, CodeSourceEntry>(),
            hashes,
            hints,
            changedDuringScan,
            consumerInputs ?? [Input()],
            scanStarted,
            previousWatermark,
            racyOverlap,
            deepVerify,
            scopeComplete,
            rootUsable));

    private static CodeSourceChange Single(CodeSourceChangeSet set, CodeSourceAction action)
    {
        Assert.HasCount(1, set.Changes, "本用例只应产生一条变更：" + Describe(set));
        Assert.AreEqual(action, set.Changes[0].Action, Describe(set));
        return set.Changes[0];
    }

    private static string Describe(CodeSourceChangeSet set) =>
        string.Join(
            "; ",
            set.Changes.Select(change =>
                $"{change.FilePath} => {change.Action} [{string.Join(",", change.Reasons)}]"));

    // ── §1 watcher 只是提示；§2 新路径即使 mtime 很旧也必须处理 ──────────────────────────────────

    [TestMethod]
    public void UnknownPath_IsAlwaysAReindexCandidate_EvenWithAVeryOldMtime()
    {
        // 停机期间新增的文件：mtime 可能远早于本轮水位，绝不能因为「比上次索引时间旧」而跳过。
        var set = Detect(
            [Observation(Path("new.cs"), minutes: -100_000, length: 10)],
            scanStarted: T0,
            previousWatermark: T0.AddMinutes(-1));

        var change = Single(set, CodeSourceAction.ReindexContent);
        Assert.IsTrue(change.RequiresContentHash, "新路径必须先稳定读取并 hash 再提交");
        CollectionAssert.Contains(change.Reasons.ToArray(), CodeSourceChangeReasons.NewFile);
        Assert.AreEqual(1, set.ReindexCount);
        Assert.AreEqual(0, set.ReuseCount);
    }

    // ── §3 无信号时直接复用已提交结果（不读正文、不打开 workspace）────────────────────────────────

    [TestMethod]
    public void KnownPath_WithIdenticalStat_AndNoSignals_IsReusedWithoutHashing()
    {
        var path = Path("a.cs");
        var set = Detect(
            [Observation(path, minutes: 5, length: 100)],
            Manifest(Entry(path, Fingerprint(minutes: 5, length: 100, hash: "h1"), Applied())),
            scanStarted: T0.AddMinutes(10),
            previousWatermark: T0.AddMinutes(10));

        Assert.IsEmpty(set.Changes, "stat 未变、无提示、消费者输入未变 ⇒ 不产生任何动作：" + Describe(set));
        Assert.AreEqual(1, set.ReuseCount);
        Assert.AreEqual(0, set.ReindexCount);
    }

    [TestMethod]
    public void KnownPath_WithChangedStat_RequiresAContentHash()
    {
        var path = Path("a.cs");
        var set = Detect(
            [Observation(path, minutes: 6, length: 100)],
            Manifest(Entry(path, Fingerprint(minutes: 5, length: 100, hash: "h1"), Applied())),
            scanStarted: T0.AddMinutes(10));

        var change = Single(set, CodeSourceAction.ReindexContent);
        Assert.IsTrue(change.RequiresContentHash);
        CollectionAssert.Contains(change.Reasons.ToArray(), CodeSourceChangeReasons.StatChanged);
    }

    [TestMethod]
    public void LengthChange_Alone_IsAStatChange()
    {
        var path = Path("a.cs");
        var set = Detect(
            [Observation(path, minutes: 5, length: 101)],
            Manifest(Entry(path, Fingerprint(minutes: 5, length: 100, hash: "h1"), Applied())),
            scanStarted: T0.AddMinutes(10));

        var change = Single(set, CodeSourceAction.ReindexContent);
        CollectionAssert.Contains(change.Reasons.ToArray(), CodeSourceChangeReasons.StatChanged);
    }

    [TestMethod]
    public void UnreadableStat_IsNeverTreatedAsUnchanged()
    {
        var path = Path("a.cs");
        var set = Detect(
            [new CodeSourceObservation(path, null, null)],
            Manifest(Entry(path, Fingerprint(minutes: 5, length: 100, hash: "h1"), Applied())),
            scanStarted: T0.AddMinutes(10));

        var change = Single(set, CodeSourceAction.ReindexContent);
        Assert.IsTrue(change.RequiresContentHash, "读不到 stat 时必须核验内容，不能假定未变化");
        CollectionAssert.Contains(change.Reasons.ToArray(), CodeSourceChangeReasons.StatUnreadable);
    }

    // ── §2/§4 watcher 提示与 racy 窗口：必须核验内容，hash 相同则只刷新元数据 ─────────────────────

    [TestMethod]
    public void WatcherHint_WithUnchangedContent_OnlyRefreshesTheSourceMetadata()
    {
        var path = Path("a.cs");
        var set = Detect(
            [Observation(path, minutes: 5, length: 100)],
            Manifest(Entry(path, Fingerprint(minutes: 5, length: 100, hash: "h1"), Applied())),
            hashes: new Dictionary<string, SourceFingerprint> { [path] = Fingerprint(5, 100, "h1") },
            hints: [path],
            scanStarted: T0.AddMinutes(10));

        var change = Single(set, CodeSourceAction.RefreshFingerprintOnly);
        Assert.IsFalse(change.RequiresContentHash, "内容已核验一致");
        CollectionAssert.Contains(change.Reasons.ToArray(), CodeSourceChangeReasons.WatcherHint);
        CollectionAssert.Contains(change.Reasons.ToArray(), CodeSourceChangeReasons.ContentUnchanged);
        Assert.AreEqual(1, set.MetadataRefreshCount);
        Assert.AreEqual(0, set.ReindexCount);
    }

    [TestMethod]
    public void WatcherHint_WithChangedContent_Reindexes()
    {
        var path = Path("a.cs");
        var set = Detect(
            [Observation(path, minutes: 5, length: 100)],
            Manifest(Entry(path, Fingerprint(minutes: 5, length: 100, hash: "h1"), Applied())),
            hashes: new Dictionary<string, SourceFingerprint> { [path] = Fingerprint(5, 100, "h2") },
            hints: [path],
            scanStarted: T0.AddMinutes(10));

        var change = Single(set, CodeSourceAction.ReindexContent);
        CollectionAssert.Contains(change.Reasons.ToArray(), CodeSourceChangeReasons.ContentChanged);
        CollectionAssert.Contains(change.Consumers.ToArray(), "csharp");
    }

    [TestMethod]
    public void WatcherHint_WithoutAHash_DoesNotClaimFreshness()
    {
        var path = Path("a.cs");
        var set = Detect(
            [Observation(path, minutes: 5, length: 100)],
            Manifest(Entry(path, Fingerprint(minutes: 5, length: 100, hash: "h1"), Applied())),
            hints: [path],
            scanStarted: T0.AddMinutes(10));

        var change = Single(set, CodeSourceAction.ReindexContent);
        Assert.IsTrue(change.RequiresContentHash, "有提示就必须核验内容，不得跳过 hash 推进水位");
    }

    [TestMethod]
    public void RacyWindow_ProducesACandidateInsteadOfReuse()
    {
        var path = Path("a.cs");
        var scanStarted = T0.AddMinutes(10);

        // mtime 恰好等于水位 - 重叠窗口之内：同时间粒度/时钟回拨的边界候选。
        var racy = Detect(
            [new CodeSourceObservation(path, scanStarted.AddSeconds(-1), 100)],
            Manifest(Entry(path, new SourceFingerprint(scanStarted.AddSeconds(-1), 100, "h1"), Applied())),
            scanStarted: scanStarted,
            racyOverlap: TimeSpan.FromSeconds(2));

        var change = Single(racy, CodeSourceAction.ReindexContent);
        CollectionAssert.Contains(change.Reasons.ToArray(), CodeSourceChangeReasons.RacyWindow);

        // 同一份观察，落在窗口之外（且无其它信号）⇒ 复用。
        var outside = Detect(
            [new CodeSourceObservation(path, scanStarted.AddSeconds(-30), 100)],
            Manifest(Entry(path, new SourceFingerprint(scanStarted.AddSeconds(-30), 100, "h1"), Applied())),
            scanStarted: scanStarted,
            racyOverlap: TimeSpan.FromSeconds(2));

        Assert.IsEmpty(outside.Changes);
        Assert.AreEqual(1, outside.ReuseCount);
    }

    [TestMethod]
    public void DeepVerify_HashesKnownPathsEvenWhenNothingElseChanged()
    {
        var path = Path("a.cs");
        var set = Detect(
            [Observation(path, minutes: 5, length: 100)],
            Manifest(Entry(path, Fingerprint(5, 100, "h1"), Applied())),
            scanStarted: T0.AddMinutes(10),
            deepVerify: true);

        var change = Single(set, CodeSourceAction.ReindexContent);
        Assert.IsTrue(change.RequiresContentHash);
        CollectionAssert.Contains(change.Reasons.ToArray(), CodeSourceChangeReasons.DeepVerify);
        Assert.AreEqual(
            (int)CodeSourceChangeSource.MTimeScan | (int)CodeSourceChangeSource.IntegrityCheck,
            (int)change.Sources);
    }

    // ── §5 消费者输入指纹：内容一致时只重新绑定 ──────────────────────────────────────────────────

    [TestMethod]
    public void ParserPolicyChange_RebindsWithoutReExtracting()
    {
        var path = Path("a.cs");
        var set = Detect(
            [Observation(path, minutes: 5, length: 100)],
            Manifest(Entry(path, Fingerprint(5, 100, "h1"), Applied(policy: "policy-1"))),
            consumerInputs: [Input(policy: "policy-2")],
            scanStarted: T0.AddMinutes(10));

        var change = Single(set, CodeSourceAction.RebindConsumers);
        Assert.IsFalse(change.RequiresContentHash, "策略变了但内容没变：不重新提取正文");
        CollectionAssert.Contains(change.Reasons.ToArray(), CodeSourceChangeReasons.ParserPolicyChanged);
        CollectionAssert.Contains(change.Consumers.ToArray(), "csharp");
        Assert.AreEqual(1, set.RebindCount);
        Assert.AreEqual(0, set.ReindexCount);
    }

    [TestMethod]
    public void SemanticInputChange_RebindsWithoutReExtracting()
    {
        var path = Path("a.cs");
        var set = Detect(
            [Observation(path, minutes: 5, length: 100)],
            Manifest(Entry(path, Fingerprint(5, 100, "h1"), Applied(semantic: "semantic-1"))),
            consumerInputs: [Input(semantic: "semantic-2")],
            scanStarted: T0.AddMinutes(10));

        var change = Single(set, CodeSourceAction.RebindConsumers);
        CollectionAssert.Contains(change.Reasons.ToArray(), CodeSourceChangeReasons.SemanticInputChanged);
    }

    [TestMethod]
    public void ConsumerThatNeverApplied_IsRebound()
    {
        var path = Path("a.cs");
        var set = Detect(
            [Observation(path, minutes: 5, length: 100)],
            Manifest(Entry(path, Fingerprint(5, 100, "h1"))),
            consumerInputs: [Input(provider: "python")],
            scanStarted: T0.AddMinutes(10));

        var change = Single(set, CodeSourceAction.RebindConsumers);
        CollectionAssert.Contains(change.Reasons.ToArray(), CodeSourceChangeReasons.ConsumerNeverApplied);
        CollectionAssert.Contains(change.Consumers.ToArray(), "python");
    }

    [TestMethod]
    public void RebindWinsOverMetadataRefreshWhenBothApply()
    {
        // 内容 hash 与 stat 都未变，但该消费者策略变了：必须重新绑定，而不是只刷新元数据。
        var path = Path("a.cs");
        var set = Detect(
            [Observation(path, minutes: 5, length: 100)],
            Manifest(Entry(path, Fingerprint(5, 100, "h1"), Applied(policy: "policy-1"))),
            hashes: new Dictionary<string, SourceFingerprint> { [path] = Fingerprint(5, 100, "h1") },
            consumerInputs: [Input(policy: "policy-2")],
            scanStarted: T0.AddMinutes(10));

        // 无提示/无 stat 变化 ⇒ 不需要核验内容，直接按输入变化重新绑定。
        var change = Single(set, CodeSourceAction.RebindConsumers);
        CollectionAssert.Contains(change.Consumers.ToArray(), "csharp");
    }

    [TestMethod]
    public void FingerprintChange_WithChangedContent_ReindexesEveryConsumer()
    {
        var path = Path("a.cs");
        var set = Detect(
            [Observation(path, minutes: 6, length: 120)],
            Manifest(Entry(path, Fingerprint(5, 100, "h1"), Applied(policy: "policy-1", semantic: "s1"))),
            hashes: new Dictionary<string, SourceFingerprint> { [path] = Fingerprint(6, 120, "h2") },
            consumerInputs: [Input(policy: "policy-2", semantic: "s2")],
            scanStarted: T0.AddMinutes(10));

        var change = Single(set, CodeSourceAction.ReindexContent);
        Assert.IsTrue(change.RequiresContentHash);
        CollectionAssert.Contains(change.Reasons.ToArray(), CodeSourceChangeReasons.ContentChanged);
        CollectionAssert.Contains(change.Reasons.ToArray(), CodeSourceChangeReasons.ParserPolicyChanged);
    }

    [TestMethod]
    public void UnstableRead_IsRejectedInsteadOfCommitted()
    {
        // 提供的 hash 来自另一份 stat（读取与写入竞争）：不得提交，重新观察后下一轮再说。
        var path = Path("a.cs");
        var set = Detect(
            [Observation(path, minutes: 6, length: 100)],
            Manifest(Entry(path, Fingerprint(5, 100, "h1"), Applied())),
            hashes: new Dictionary<string, SourceFingerprint> { [path] = Fingerprint(7, 100, "h2") },
            scanStarted: T0.AddMinutes(10));

        var change = Single(set, CodeSourceAction.Deferred);
        CollectionAssert.Contains(change.Reasons.ToArray(), CodeSourceChangeReasons.UnstableRead);
        Assert.AreEqual(0, set.ReindexCount);
    }

    // ── §6 删除必须可证实；§7 水位不掩盖失败 ─────────────────────────────────────────────────────

    [TestMethod]
    public void CompleteScan_ConfirmsDeletionOfAManifestPathThatIsGone()
    {
        var path = Path("gone.cs");
        var set = Detect(
            observations: [],
            manifest: Manifest(Entry(path, Fingerprint(5, 100, "h1"), Applied())),
            scanStarted: T0.AddMinutes(10),
            previousWatermark: T0.AddMinutes(5));

        var change = Single(set, CodeSourceAction.Delete);
        CollectionAssert.Contains(change.Reasons.ToArray(), CodeSourceChangeReasons.MissingOnDisk);
        Assert.AreEqual(1, set.DeletedCount);
        Assert.IsTrue(set.ScanComplete);
        Assert.AreEqual(T0.AddMinutes(10), set.NextScanWatermarkUtc, "完整成功的扫描才推进水位");
    }

    [TestMethod]
    public void IncompleteScan_RetainsMissingPathsAndNeverDeletes()
    {
        var path = Path("gone.cs");
        var set = Detect(
            observations: [],
            manifest: Manifest(Entry(path, Fingerprint(5, 100, "h1"), Applied())),
            scanStarted: T0.AddMinutes(10),
            previousWatermark: T0.AddMinutes(5),
            scopeComplete: false);

        var change = Single(set, CodeSourceAction.Deferred);
        CollectionAssert.Contains(change.Reasons.ToArray(), CodeSourceChangeReasons.ScanIncomplete);
        Assert.AreEqual(0, set.DeletedCount);
        Assert.AreEqual(1, set.UnresolvedMissingCount);
        Assert.IsFalse(set.ScanComplete);
        Assert.AreEqual(T0.AddMinutes(5), set.NextScanWatermarkUtc, "不完整扫描不得推进水位");
    }

    [TestMethod]
    public void UnusableRoot_NeverConcludesDeletion()
    {
        var path = Path("gone.cs");
        var set = Detect(
            observations: [],
            manifest: Manifest(Entry(path, Fingerprint(5, 100, "h1"), Applied())),
            scanStarted: T0.AddMinutes(10),
            previousWatermark: T0.AddMinutes(5),
            rootUsable: false);

        var change = Single(set, CodeSourceAction.Deferred);
        CollectionAssert.Contains(change.Reasons.ToArray(), CodeSourceChangeReasons.RootUnusable);
        Assert.AreEqual(0, set.DeletedCount);
        Assert.AreEqual(T0.AddMinutes(5), set.NextScanWatermarkUtc);
    }

    [TestMethod]
    public void ChangedDuringScan_IsDeferredForBothObservedAndMissingPaths()
    {
        var observed = Path("touched.cs");
        var missing = Path("gone.cs");

        var set = Detect(
            [Observation(observed, minutes: 6, length: 100)],
            Manifest(
                Entry(observed, Fingerprint(5, 100, "h1"), Applied()),
                Entry(missing, Fingerprint(5, 100, "h1"), Applied())),
            changedDuringScan: [observed, missing],
            scanStarted: T0.AddMinutes(10));

        Assert.AreEqual(2, set.Changes.Count, Describe(set));
        Assert.IsTrue(set.Changes.All(change => change.Action == CodeSourceAction.Deferred), Describe(set));
        Assert.IsTrue(
            set.Changes.All(change =>
                change.Reasons.Contains(CodeSourceChangeReasons.ChangedDuringScan)),
            Describe(set));
        Assert.AreEqual(0, set.DeletedCount);
    }

    [TestMethod]
    public void WatcherOnlyBatch_NeverAdvancesTheScanWatermark()
    {
        var path = Path("a.cs");
        var set = Detect(
            [Observation(path, minutes: 6, length: 100)],
            Manifest(Entry(path, Fingerprint(5, 100, "h1"), Applied())),
            hints: [path],
            scanStarted: null,
            previousWatermark: T0.AddMinutes(5));

        Assert.AreEqual(T0.AddMinutes(5), set.NextScanWatermarkUtc, "watcher 批次不推进扫描水位");
        Assert.IsFalse(set.ScanComplete, "watcher 批次没有枚举磁盘：不算完整核对，也不得出删除结论");
    }

    [TestMethod]
    public void MissingObservation_WithoutAScanIsNeverDeleted()
    {
        // watcher-only 批次（ScanStartedUtc = null）：manifest 有、本次未见 ⇒ 保留，交由扫描核对。
        var path = Path("gone.cs");
        var set = Detect(
            observations: [],
            manifest: Manifest(Entry(path, Fingerprint(5, 100, "h1"), Applied())),
            scanStarted: null);

        Assert.AreEqual(0, set.DeletedCount);
        Assert.AreEqual(1, set.UnresolvedMissingCount);
        Assert.IsTrue(set.Changes.All(change => change.Action == CodeSourceAction.Deferred), Describe(set));
    }

    // ── 其它不变量：去重、路径比较、完整性 ────────────────────────────────────────────────────────

    [TestMethod]
    public void DuplicateObservations_AreMergedIntoOneChange()
    {
        var path = Path("a.cs");
        var set = Detect(
            [Observation(path, minutes: 6, length: 100), Observation(path, minutes: 6, length: 100)],
            Manifest(Entry(path, Fingerprint(5, 100, "h1"), Applied())),
            scanStarted: T0.AddMinutes(10));

        Assert.HasCount(1, set.Changes, Describe(set));
        Assert.AreEqual(1, set.ReindexCount);
    }

    [TestMethod]
    public void IncompleteManifestEntry_IsReVerified()
    {
        var path = Path("a.cs");
        var set = Detect(
            [Observation(path, minutes: 5, length: 100)],
            Manifest(new CodeSourceEntry(
                path,
                Fingerprint(5, 100, "h1"),
                [Applied()],
                Complete: false)),
            scanStarted: T0.AddMinutes(10));

        var change = Single(set, CodeSourceAction.ReindexContent);
        Assert.IsTrue(change.RequiresContentHash, "不完整的 manifest 行不得当作已应用");
    }

    [TestMethod]
    public void PathComparison_FollowsTheComponentPathIdentity()
    {
        var set = Detect(
            [Observation(@"c:\repo\A.cs", minutes: 6, length: 100)],
            Manifest(Entry(@"C:\repo\a.cs", Fingerprint(5, 100, "h1"), Applied())),
            scanStarted: T0.AddMinutes(10));

        if (OperatingSystem.IsWindows())
        {
            // Windows 上路径身份不区分大小写：这是「同一个已索引文件」而不是新文件。
            Assert.HasCount(1, set.Changes, Describe(set));
            Assert.AreEqual(CodeSourceAction.ReindexContent, set.Changes[0].Action);
            Assert.IsFalse(
                set.Changes[0].Reasons.Contains(CodeSourceChangeReasons.NewFile),
                Describe(set));
        }
        else
        {
            CollectionAssert.Contains(
                set.Changes[0].Reasons.ToArray(),
                CodeSourceChangeReasons.NewFile);
        }
    }
}
