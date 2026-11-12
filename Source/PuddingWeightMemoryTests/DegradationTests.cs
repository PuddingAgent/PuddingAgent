namespace PuddingWeightMemoryTests;

/// <summary>
/// 写入队列饱和时的降级契约：不抛异常、记计数、给出可预期的结果。
/// <para>
/// 关闸之前先成功登记一个章节，否则预测会在"分区不存在"处提前返回，
/// 走不到饱和降级分支，测试就会绿得毫无意义。
/// </para>
/// </summary>
[TestClass]
public sealed class DegradationTests
{
    private static readonly (string Text, float[] Vector)[] Vectors =
    [
        ("chapter-a", [1f, 0f, 0f, 0f]),
        ("chapter-b", [0f, 1f, 0f, 0f]),
    ];

    [TestMethod]
    public async Task SaturatedQueue_DropsWritesAndDegradesPredictionToEmpty()
    {
        var gate = new SwitchableWriteGate();
        await using var harness = new WeightMemoryHarness(Vectors, gate: gate);
        var ct = TestContext.CancellationToken;

        await harness.RegisterAsync("ch-a", "chapter-a", ct);
        Assert.AreEqual(1L, harness.Memory.Counters.RegistrationsApplied);

        gate.Closed = true;

        await harness.RegisterAsync("ch-b", "chapter-b", ct);
        await harness.Memory.ApplyFeedbackAsync(harness.NewFeedback("ch-a", "chapter-b"), ct);
        await harness.Memory.InvalidateChapterAsync(harness.Scope, "ch-a", "evt-delete-1", ct);

        var result = await harness.PredictAsync("chapter-a", ct: ct);

        Assert.IsEmpty(result.Hits, "饱和时预测降级为空结果。");
        Assert.IsFalse(string.IsNullOrEmpty(result.SnapshotId), "降级仍要带回快照号，否则调用方无法配对。");
        Assert.AreEqual(1L, harness.Memory.Counters.RegistrationsDropped);
        Assert.AreEqual(1L, harness.Memory.Counters.FeedbacksDropped);
        Assert.AreEqual(1L, harness.Memory.Counters.InvalidationsDropped);
        Assert.AreEqual(1L, harness.Memory.Counters.PredictionsDegraded);
        Assert.AreEqual(0L, harness.Memory.Counters.FeedbacksApplied);

        // 漏写是"没写进去"，不是"写坏了"：被漏掉的章节不应存在。
        Assert.AreEqual(0L, await harness.CountChapterRowsAsync("ch-b"), "被漏掉的登记不得留下半条记录。");
    }

    [TestMethod]
    public async Task SaturatedQueue_LeavesExistingChapterReadableAfterReopening()
    {
        var gate = new SwitchableWriteGate();
        await using var harness = new WeightMemoryHarness(Vectors, gate: gate);
        var ct = TestContext.CancellationToken;
        await harness.RegisterAsync("ch-a", "chapter-a", ct);

        gate.Closed = true;
        var degraded = await harness.PredictAsync("chapter-a", ct: ct);
        gate.Closed = false;
        var recovered = await harness.PredictAsync("chapter-a", ct: ct);

        Assert.IsEmpty(degraded.Hits);
        Assert.HasCount(1, recovered.Hits, "闸门恢复后读路径应立刻恢复，不需要重建分区。");
        Assert.AreEqual("ch-a", recovered.Hits[0].ChapterId);
    }

    public TestContext TestContext { get; set; } = null!;
}
