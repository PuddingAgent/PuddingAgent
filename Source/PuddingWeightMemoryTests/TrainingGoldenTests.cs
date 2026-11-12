namespace PuddingWeightMemoryTests;

/// <summary>
/// 四步局部梯度的金标准：与参考实现 <c>keys.py::train_key</c> 的纯 Python 转写
/// （<c>.work/weight-memory/golden_oracle.py</c>）逐位对照，容差 1e-5。
/// <para>
/// 为什么要 vector 级断言而不是只看分数：分数只是 <c>weight·cue</c> 的一个投影，
/// 多个不同的权重能给同一个分数。落盘向量才是"训练公式对不对"的证据。
/// </para>
/// <para>
/// 三组用例分别锁定：只有固定空对手、有一个零分负例、以及非轴对齐（对角线）情形。
/// 步数也单独锁定：1 步与 4 步必须是两组不同的数，否则"步数配置"是死参数。
/// </para>
/// </summary>
[TestClass]
public sealed class TrainingGoldenTests
{
    private static readonly (string Text, float[] Vector)[] Vectors =
    [
        ("chapter-a", [1f, 0f, 0f, 0f]),
        ("chapter-b", [0f, 1f, 0f, 0f]),
        ("chapter-c", [0f, 0f, 1f, 0f]),
        ("chapter-diag", [1f, 1f, 0f, 0f]),
        ("chapter-peer", [1f, 0f, 1f, 0f]),
        ("chapter-diag-cue", [0f, 1f, 1f, 0f]),
    ];

    [TestMethod]
    public async Task Training_SingleChapter_FourStepsMatchesOracle()
    {
        await using var harness = new WeightMemoryHarness(Vectors);
        var ct = TestContext.CancellationToken;
        await harness.RegisterAsync("ch-a", "chapter-a", ct);

        await harness.Memory.ApplyFeedbackAsync(harness.NewFeedback("ch-a", "chapter-b"), ct);

        AssertVector(
            [0.5705831320266406f, 0.8212398489154487f, 0f, 0f],
            await harness.ReadWeightAsync("candidate", "ch-a"));
    }

    [TestMethod]
    public async Task Training_SingleChapter_OneStepMatchesOracleAndDiffersFromFourSteps()
    {
        await using var harness = new WeightMemoryHarness(
            Vectors,
            WeightMemoryHarness.OptionsFactory(trainingSteps: 1));
        var ct = TestContext.CancellationToken;
        await harness.RegisterAsync("ch-a", "chapter-a", ct);

        await harness.Memory.ApplyFeedbackAsync(harness.NewFeedback("ch-a", "chapter-b"), ct);

        var weight = await harness.ReadWeightAsync("candidate", "ch-a");
        AssertVector([0.8337633093538647f, 0.5521220372121473f, 0f, 0f], weight);
        Assert.AreNotEqual(0.5705831320266406f, weight[0], 1e-5f, "1 步的结果必须区别于 4 步，否则步数是死参数。");
    }

    [TestMethod]
    public async Task Training_WithOneZeroScoreCompetitor_FourStepsMatchesOracle()
    {
        await using var harness = new WeightMemoryHarness(Vectors);
        var ct = TestContext.CancellationToken;
        await harness.RegisterAsync("ch-a", "chapter-a", ct);
        await harness.RegisterAsync("ch-c", "chapter-c", ct);

        await harness.Memory.ApplyFeedbackAsync(harness.NewFeedback("ch-a", "chapter-b"), ct);

        var weight = await harness.ReadWeightAsync("candidate", "ch-a");
        AssertVector([0.5688835293830192f, 0.8224180992638231f, 0f, 0f], weight);
        Assert.AreNotEqual(0.5705831320266406f, weight[0], 1e-5f, "有负例时必须区别于无负例，证明负例真的进了损失。");
    }

    [TestMethod]
    public async Task Training_DiagonalVectors_FourStepsMatchesOracle()
    {
        await using var harness = new WeightMemoryHarness(Vectors);
        var ct = TestContext.CancellationToken;
        await harness.RegisterAsync("ch-diag", "chapter-diag", ct);
        await harness.RegisterAsync("ch-peer", "chapter-peer", ct);

        await harness.Memory.ApplyFeedbackAsync(harness.NewFeedback("ch-diag", "chapter-diag-cue"), ct);

        var weight = await harness.ReadWeightAsync("candidate", "ch-diag");
        AssertVector([0.3627744470942592f, 0.8148665250255526f, 0.45209207793129347f, 0f], weight);
    }

    [TestMethod]
    public async Task Training_TrainedWeightIsWhatPredictionScores()
    {
        await using var harness = new WeightMemoryHarness(Vectors);
        var ct = TestContext.CancellationToken;
        await harness.RegisterAsync("ch-a", "chapter-a", ct);
        await harness.Memory.ApplyFeedbackAsync(harness.NewFeedback("ch-a", "chapter-b"), ct);

        var snapshots = await harness.Memory.ReadSnapshotsAsync(harness.Scope, ct);
        Assert.IsNotNull(snapshots);
        var result = await harness.PredictAsync("chapter-b", snapshotId: snapshots.CandidateSnapshotId!, ct: ct);

        // 与上面的向量同源：dot([0.5705831,0.8212398,0,0], [0,1,0,0]) = 0.8212398。
        Assert.HasCount(1, result.Hits);
        Assert.AreEqual(0.8212398489154487, result.Hits[0].Score, 1e-5, "候选快照的分数必须等于训练后权重的投影。");
    }

    /// <summary>逐分量比较，失配时报出第一个越界分量。</summary>
    private static void AssertVector(float[] expected, float[] actual, float tolerance = 1e-5f)
    {
        Assert.HasCount(expected.Length, actual, "向量维度不一致。");
        for (var index = 0; index < expected.Length; index++)
        {
            Assert.AreEqual(
                expected[index],
                actual[index],
                tolerance,
                $"第 {index} 个分量不符：期望 {expected[index]:R}，实际 {actual[index]:R}。");
        }
    }

    public TestContext TestContext { get; set; } = null!;
}
