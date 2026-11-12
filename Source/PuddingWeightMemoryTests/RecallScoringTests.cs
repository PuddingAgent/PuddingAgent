namespace PuddingWeightMemoryTests;

/// <summary>
/// 打分与排序：归一化点积、同分序数序、两个阈值、Limit 边界。
/// <para>
/// 每个用例的期望数字都能手算：查询 <c>[3,4,0,0]</c> 归一化为 <c>[0.6,0.8,0,0]</c>，
/// 与 <c>[1,0,0,0]</c> 的点积是 0.6、与 <c>[0,1,0,0]</c> 是 0.8。阈值用例都配一条"关掉阈值就通过"
/// 的对照，否则断言无法证明是阈值在起作用。
/// </para>
/// </summary>
[TestClass]
public sealed class RecallScoringTests
{
    private static readonly (string Text, float[] Vector)[] AxisVectors =
    [
        ("chapter-a", [1f, 0f, 0f, 0f]),
        ("chapter-b", [0f, 1f, 0f, 0f]),
        ("chapter-c", [0f, 0f, 1f, 0f]),
        ("query-34", [3f, 4f, 0f, 0f]),
        ("query-11", [1f, 1f, 0f, 0f]),
    ];

    [TestMethod]
    public async Task Predict_ScoresAreNormalisedDotProducts_MatchingHandComputedValues()
    {
        await using var harness = new WeightMemoryHarness(AxisVectors);
        await harness.RegisterAsync("ch-a", "chapter-a", TestContext.CancellationToken);
        await harness.RegisterAsync("ch-b", "chapter-b", TestContext.CancellationToken);

        var result = await harness.PredictAsync("query-34", ct: TestContext.CancellationToken);

        Assert.HasCount(2, result.Hits, "两个章节都应命中。");
        Assert.AreEqual("ch-b", result.Hits[0].ChapterId);
        Assert.AreEqual(0.8, result.Hits[0].Score, 1e-6, "dot([0,1,0,0], [0.6,0.8,0,0]) = 0.8。");
        Assert.AreEqual("ch-a", result.Hits[1].ChapterId);
        Assert.AreEqual(0.6, result.Hits[1].Score, 1e-6, "dot([1,0,0,0], [0.6,0.8,0,0]) = 0.6。");
    }

    [TestMethod]
    public async Task Predict_EqualScores_TieBreakByChapterIdOrdinal()
    {
        await using var harness = new WeightMemoryHarness(AxisVectors);
        // 注册顺序与期望顺序相反：证明排序不是靠插入顺序。
        await harness.RegisterAsync("ch-zulu", "chapter-a", TestContext.CancellationToken);
        await harness.RegisterAsync("ch-alpha", "chapter-a", TestContext.CancellationToken);

        var result = await harness.PredictAsync("query-11", ct: TestContext.CancellationToken);

        Assert.HasCount(2, result.Hits);
        Assert.AreEqual(result.Hits[0].Score, result.Hits[1].Score, 0d, "两个章节向量相同，分数必须完全相等。");
        Assert.AreEqual("ch-alpha", result.Hits[0].ChapterId, "同分按章节 ID 序数序：alpha < zulu。");
        Assert.AreEqual("ch-zulu", result.Hits[1].ChapterId);
    }

    [TestMethod]
    public async Task Predict_TopScoreBelowThreshold_ReturnsEmpty_AndControlWithoutThresholdReturnsHit()
    {
        await using (var control = new WeightMemoryHarness(AxisVectors))
        {
            await control.RegisterAsync("ch-b", "chapter-b", TestContext.CancellationToken);
            var open = await control.PredictAsync("query-34", ct: TestContext.CancellationToken);
            Assert.HasCount(1, open.Hits, "对照：门槛为 0 时 0.8 分必须返回。");
        }

        await using var harness = new WeightMemoryHarness(
            AxisVectors,
            WeightMemoryHarness.OptionsFactory(minimumTopScore: 0.85f));
        await harness.RegisterAsync("ch-b", "chapter-b", TestContext.CancellationToken);

        var result = await harness.PredictAsync("query-34", ct: TestContext.CancellationToken);

        Assert.IsEmpty(result.Hits, "最高分 0.8 低于门槛 0.85，必须返回空结果。");
        Assert.IsFalse(string.IsNullOrEmpty(result.SnapshotId), "空结果仍要带回实际读取的快照号。");
    }

    [TestMethod]
    public async Task Predict_GapBelowThreshold_ReturnsEmpty_AndControlWithLowerGapReturnsHits()
    {
        await using (var control = new WeightMemoryHarness(
                         AxisVectors,
                         WeightMemoryHarness.OptionsFactory(minimumScoreGap: 0.1f)))
        {
            await control.RegisterAsync("ch-a", "chapter-a", TestContext.CancellationToken);
            await control.RegisterAsync("ch-b", "chapter-b", TestContext.CancellationToken);
            var open = await control.PredictAsync("query-34", ct: TestContext.CancellationToken);
            Assert.HasCount(2, open.Hits, "对照：分差 0.2 高于门槛 0.1，必须返回。");
        }

        await using var harness = new WeightMemoryHarness(
            AxisVectors,
            WeightMemoryHarness.OptionsFactory(minimumScoreGap: 0.3f));
        await harness.RegisterAsync("ch-a", "chapter-a", TestContext.CancellationToken);
        await harness.RegisterAsync("ch-b", "chapter-b", TestContext.CancellationToken);

        var result = await harness.PredictAsync("query-34", ct: TestContext.CancellationToken);

        Assert.IsEmpty(result.Hits, "第一二名分差 0.2 低于门槛 0.3，必须返回空结果。");
    }

    [TestMethod]
    public async Task Predict_LimitBoundaries_ZeroOneAndBeyondHitCount()
    {
        await using var harness = new WeightMemoryHarness(AxisVectors);
        await harness.RegisterAsync("ch-a", "chapter-a", TestContext.CancellationToken);
        await harness.RegisterAsync("ch-b", "chapter-b", TestContext.CancellationToken);
        await harness.RegisterAsync("ch-c", "chapter-c", TestContext.CancellationToken);

        var zero = await harness.PredictAsync("query-34", limit: 0, ct: TestContext.CancellationToken);
        var one = await harness.PredictAsync("query-34", limit: 1, ct: TestContext.CancellationToken);
        var many = await harness.PredictAsync("query-34", limit: 99, ct: TestContext.CancellationToken);

        Assert.IsEmpty(zero.Hits, "Limit = 0 视为空结果。");
        Assert.HasCount(1, one.Hits);
        Assert.AreEqual("ch-b", one.Hits[0].ChapterId);
        Assert.HasCount(3, many.Hits, "Limit 超过命中数时返回全部。");
    }

    [TestMethod]
    public async Task Predict_OnEmptyScope_ReturnsEmptyWithNoSnapshot()
    {
        await using var harness = new WeightMemoryHarness(AxisVectors);

        var result = await harness.PredictAsync("query-34", ct: TestContext.CancellationToken);

        Assert.AreEqual(string.Empty, result.SnapshotId, "分区尚未建立时没有可报告的快照号。");
        Assert.IsEmpty(result.Hits);
        Assert.AreEqual(1L, harness.Memory.Counters.Predictions);
    }

    [TestMethod]
    public async Task Predict_UnknownPinnedSnapshot_ReturnsEmptyAndDoesNotFallBack()
    {
        await using var harness = new WeightMemoryHarness(AxisVectors);
        await harness.RegisterAsync("ch-a", "chapter-a", TestContext.CancellationToken);

        var result = await harness.PredictAsync(
            "query-34", snapshotId: "snap-999999", ct: TestContext.CancellationToken);

        Assert.AreEqual("snap-999999", result.SnapshotId, "必须回显被钉住的快照号，而不是偷偷换成活动快照。");
        Assert.IsEmpty(result.Hits);
        Assert.AreEqual(1L, harness.Memory.Counters.PredictionsUnknownSnapshot);
    }

    /// <summary>MSTest 注入的每测试取消令牌，用于让所有异步调用可被宿主取消。</summary>
    public TestContext TestContext { get; set; } = null!;
}
