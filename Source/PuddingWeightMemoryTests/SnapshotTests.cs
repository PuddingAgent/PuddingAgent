namespace PuddingWeightMemoryTests;

/// <summary>
/// 快照生命周期：失效跨快照生效、比较交换、回滚到旧快照。
/// <para>
/// 这些用例都同时读"活动"与"候选"两份落盘权重，因为只断言排序无法区分
/// "反馈没训练"和"训练了但没被读到"。
/// </para>
/// </summary>
[TestClass]
public sealed class SnapshotTests
{
    private static readonly (string Text, float[] Vector)[] Vectors =
    [
        ("chapter-a", [1f, 0f, 0f, 0f]),
        ("chapter-b", [0f, 1f, 0f, 0f]),
        ("chapter-c", [0f, 0f, 1f, 0f]),
    ];

    [TestMethod]
    public async Task Invalidate_HidesChapterFromActiveAndCandidateSnapshots()
    {
        await using var harness = new WeightMemoryHarness(Vectors);
        var ct = TestContext.CancellationToken;
        await harness.RegisterAsync("ch-a", "chapter-a", ct);
        await harness.RegisterAsync("ch-b", "chapter-c", ct);

        var snapshots = await harness.Memory.ReadSnapshotsAsync(harness.Scope, ct);
        Assert.IsNotNull(snapshots);

        await harness.Memory.InvalidateChapterAsync(harness.Scope, "ch-a", "evt-delete-1", ct);

        var active = await harness.PredictAsync("chapter-a", ct: ct);
        var candidate = await harness.PredictAsync("chapter-a", snapshotId: snapshots.CandidateSnapshotId, ct: ct);

        Assert.IsFalse(active.Hits.Any(hit => hit.ChapterId == "ch-a"), "活动快照不得再返回失效章节。");
        Assert.IsFalse(candidate.Hits.Any(hit => hit.ChapterId == "ch-a"), "候选快照同样不得返回失效章节。");
        Assert.IsTrue(active.Hits.Any(hit => hit.ChapterId == "ch-b"), "未失效的章节必须仍然可见。");
        Assert.AreEqual(1L, harness.Memory.Counters.InvalidationsApplied);
    }

    [TestMethod]
    public async Task Invalidate_SameEventTwice_IsIdempotent()
    {
        await using var harness = new WeightMemoryHarness(Vectors);
        var ct = TestContext.CancellationToken;
        await harness.RegisterAsync("ch-a", "chapter-a", ct);

        await harness.Memory.InvalidateChapterAsync(harness.Scope, "ch-a", "evt-delete-1", ct);
        await harness.Memory.InvalidateChapterAsync(harness.Scope, "ch-a", "evt-delete-1", ct);

        Assert.AreEqual(1L, harness.Memory.Counters.InvalidationsApplied);
        Assert.AreEqual(0L, harness.Memory.Counters.InvalidationsDropped);
    }

    [TestMethod]
    public async Task Publish_CompareAndSwap_SucceedsOnceThenRejectsStaleExpectation()
    {
        await using var harness = new WeightMemoryHarness(Vectors);
        var ct = TestContext.CancellationToken;
        await harness.RegisterAsync("ch-a", "chapter-a", ct);

        var initial = await harness.Memory.ReadSnapshotsAsync(harness.Scope, ct);
        Assert.IsNotNull(initial);

        var published = await harness.Memory.TryPublishAsync(
            harness.Scope, initial.ActiveSnapshotId, initial.CandidateSnapshotId, ct);
        Assert.IsTrue(published, "活动快照号匹配时必须切换成功。");

        var afterPublish = await harness.Memory.ReadSnapshotsAsync(harness.Scope, ct);
        Assert.IsNotNull(afterPublish);
        Assert.AreEqual(initial.CandidateSnapshotId, afterPublish.ActiveSnapshotId);
        Assert.AreNotEqual(initial.CandidateSnapshotId, afterPublish.CandidateSnapshotId, "发布后必须派生新的候选快照，否则后续反馈会改动活动版本。");

        var stale = await harness.Memory.TryPublishAsync(
            harness.Scope, initial.ActiveSnapshotId, afterPublish.CandidateSnapshotId, ct);

        var afterStale = await harness.Memory.ReadSnapshotsAsync(harness.Scope, ct);
        Assert.IsFalse(stale, "期望的活动快照号已过期，比较交换必须失败。");
        Assert.IsNotNull(afterStale);
        Assert.AreEqual(afterPublish.ActiveSnapshotId, afterStale.ActiveSnapshotId, "失败路径不得改动指针。");
        Assert.AreEqual(afterPublish.CandidateSnapshotId, afterStale.CandidateSnapshotId);
        Assert.AreEqual(1L, harness.Memory.Counters.PublishSucceeded);
        Assert.AreEqual(1L, harness.Memory.Counters.PublishRejected);
    }

    [TestMethod]
    public async Task Publish_UnknownCandidateSnapshot_RejectedWithoutSideEffects()
    {
        await using var harness = new WeightMemoryHarness(Vectors);
        var ct = TestContext.CancellationToken;
        await harness.RegisterAsync("ch-a", "chapter-a", ct);

        var initial = await harness.Memory.ReadSnapshotsAsync(harness.Scope, ct);
        Assert.IsNotNull(initial);

        var published = await harness.Memory.TryPublishAsync(
            harness.Scope, initial.ActiveSnapshotId, "snap-999999", ct);

        Assert.IsFalse(published);
        Assert.AreEqual(1L, harness.Memory.Counters.PublishRejected);
    }

    [TestMethod]
    public async Task Publish_RollbackToOlderSnapshot_RestoresFrozenWeights()
    {
        await using var harness = new WeightMemoryHarness(Vectors);
        var ct = TestContext.CancellationToken;
        await harness.RegisterAsync("ch-a", "chapter-a", ct);
        await harness.RegisterAsync("ch-b", "chapter-c", ct);

        var initial = await harness.Memory.ReadSnapshotsAsync(harness.Scope, ct);
        Assert.IsNotNull(initial);

        // 反馈只训练候选：cue = [0,1,0,0]，ch-a 的候选权重变成金标准第 2 组。
        await harness.Memory.ApplyFeedbackAsync(harness.NewFeedback("ch-a", "chapter-b"), ct);

        var trainedScore = await ScoreOfAsync(harness, "chapter-b", initial.CandidateSnapshotId, "ch-a", ct);
        var frozenScore = await ScoreOfAsync(harness, "chapter-b", initial.ActiveSnapshotId, "ch-a", ct);
        Assert.AreEqual(0.8224180992638231, trainedScore, 1e-5, "候选快照里 ch-a 已学会这句话。");
        Assert.AreEqual(0d, frozenScore, 1e-5, "活动快照仍未训练。");

        Assert.IsTrue(await harness.Memory.TryPublishAsync(
            harness.Scope, initial.ActiveSnapshotId, initial.CandidateSnapshotId, ct));

        var promoted = await harness.Memory.ReadSnapshotsAsync(harness.Scope, ct);
        Assert.IsNotNull(promoted);
        Assert.AreEqual(
            0.8224180992638231,
            await ScoreOfAsync(harness, "chapter-b", promoted.ActiveSnapshotId, "ch-a", ct),
            1e-5,
            "发布后活动快照读到的就是已训练权重。");

        // 回滚：把发布前的活动快照重新推为活动。
        Assert.IsTrue(await harness.Memory.TryPublishAsync(
            harness.Scope, promoted.ActiveSnapshotId, initial.ActiveSnapshotId, ct));

        var rolledBack = await harness.Memory.ReadSnapshotsAsync(harness.Scope, ct);
        Assert.IsNotNull(rolledBack);
        Assert.AreEqual(initial.ActiveSnapshotId, rolledBack.ActiveSnapshotId, "回滚后活动指针指回旧快照。");
        Assert.AreEqual(
            0d,
            await ScoreOfAsync(harness, "chapter-b", rolledBack.ActiveSnapshotId, "ch-a", ct),
            1e-5,
            "回滚后必须读到旧权重，而不是被训练过的候选。");
    }

    private static async Task<double> ScoreOfAsync(
        WeightMemoryHarness harness,
        string query,
        string snapshotId,
        string chapterId,
        CancellationToken ct)
    {
        var result = await harness.PredictAsync(query, snapshotId: snapshotId, ct: ct);
        var hit = result.Hits.SingleOrDefault(candidate => candidate.ChapterId == chapterId);
        Assert.IsNotNull(hit, $"快照 {snapshotId} 的预测结果里没有章节 {chapterId}。");
        return hit.Score;
    }

    public TestContext TestContext { get; set; } = null!;
}
