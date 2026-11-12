using PuddingWeightMemory;

namespace PuddingWeightMemoryTests;

/// <summary>
/// 反馈写入：只动候选、按事件与"消息-章节"双重幂等、跳过已领先的正确章、可接受集合不进负例、
/// 以及事务中断的整体回滚。
/// <para>
/// 这里的期望向量来自 <c>.work/weight-memory/golden_oracle.py</c>（由参考实现
/// <c>keys.py::train_key</c> 逐行转写、纯标准库运行）算出的金标准，容差 1e-5。
/// </para>
/// </summary>
[TestClass]
public sealed class FeedbackTests
{
    /// <summary>单章、无其它地址时的四步结果（competing = [0.5]）。</summary>
    private static readonly float[] SingleChapterGolden = [0.5705831320266406f, 0.8212398489154487f, 0f, 0f];

    /// <summary>一个零分负例时的四步结果（competing = [0.0, 0.5]）。</summary>
    private static readonly float[] OnePeerGolden = [0.5688835293830192f, 0.8224180992638231f, 0f, 0f];

    private static readonly (string Text, float[] Vector)[] Vectors =
    [
        ("chapter-a", [1f, 0f, 0f, 0f]),
        ("chapter-b", [0f, 1f, 0f, 0f]),
        ("chapter-c", [0f, 0f, 1f, 0f]),
    ];

    [TestMethod]
    public async Task Feedback_SameEventTwice_TrainsOnce()
    {
        await using var harness = new WeightMemoryHarness(Vectors);
        var ct = TestContext.CancellationToken;
        await harness.RegisterAsync("ch-a", "chapter-a", ct);

        var feedback = harness.NewFeedback("ch-a", "chapter-b");
        await harness.Memory.ApplyFeedbackAsync(feedback, ct);
        var afterFirst = await harness.ReadWeightAsync("candidate", "ch-a");

        await harness.Memory.ApplyFeedbackAsync(feedback, ct);
        var afterSecond = await harness.ReadWeightAsync("candidate", "ch-a");

        AssertVector(SingleChapterGolden, afterFirst);
        AssertVector(afterFirst, afterSecond);
        Assert.AreEqual(1L, harness.Memory.Counters.FeedbacksApplied);
        Assert.AreEqual(1L, harness.Memory.Counters.FeedbacksDuplicate);
    }

    [TestMethod]
    public async Task Feedback_SameMessageWithNewEventId_IsStillDuplicate()
    {
        await using var harness = new WeightMemoryHarness(Vectors);
        var ct = TestContext.CancellationToken;
        await harness.RegisterAsync("ch-a", "chapter-a", ct);

        var first = harness.NewFeedback("ch-a", "chapter-b", messageId: "msg-shared");
        var retry = harness.NewFeedback("ch-a", "chapter-b", messageId: "msg-shared");
        await harness.Memory.ApplyFeedbackAsync(first, ct);
        var afterFirst = await harness.ReadWeightAsync("candidate", "ch-a");

        await harness.Memory.ApplyFeedbackAsync(retry, ct);
        var afterRetry = await harness.ReadWeightAsync("candidate", "ch-a");

        AssertVector(SingleChapterGolden, afterFirst);
        AssertVector(afterFirst, afterRetry);
        Assert.AreEqual(1L, harness.Memory.Counters.FeedbacksApplied);
        Assert.AreEqual(1L, harness.Memory.Counters.FeedbacksDuplicate);
    }

    [TestMethod]
    public async Task Feedback_TrainsCandidateOnly_ActiveSnapshotStaysFrozen()
    {
        await using var harness = new WeightMemoryHarness(Vectors);
        var ct = TestContext.CancellationToken;
        await harness.RegisterAsync("ch-a", "chapter-a", ct);

        await harness.Memory.ApplyFeedbackAsync(harness.NewFeedback("ch-a", "chapter-b"), ct);

        var active = await harness.ReadWeightAsync("active", "ch-a");
        var candidate = await harness.ReadWeightAsync("candidate", "ch-a");

        Assert.AreEqual(1f, active[0], 1e-6f, "活动快照不因反馈变化。");
        Assert.AreEqual(0f, active[1], 1e-6f);
        AssertVector(SingleChapterGolden, candidate);
    }

    [TestMethod]
    public async Task Feedback_ExcludesEveryAcceptableChapterFromHardNegatives()
    {
        await using var harness = new WeightMemoryHarness(Vectors);
        var ct = TestContext.CancellationToken;
        await harness.RegisterAsync("ch-a", "chapter-a", ct);   // 首选正确章，cue 上得分 0
        await harness.RegisterAsync("ch-b", "chapter-b", ct);   // 也算对：若进负例会让结果变成另一组数
        await harness.RegisterAsync("ch-c", "chapter-c", ct);   // 真正的困难负例，cue 上得分 0

        // cue = [0,1,0,0]：可接受章 ch-b 的分数是 1.0（最像），负例章 ch-c 是 0.0。
        await harness.Memory.ApplyFeedbackAsync(
            harness.NewFeedback("ch-a", "chapter-b", acceptable: ["ch-b"]), ct);

        var weight = await harness.ReadWeightAsync("candidate", "ch-a");

        // 金标准 competing = [0.0, 0.5]：只含 ch-c 与固定空对手。
        // 若 ch-b 未排除，会得到含 1.0 的另一组权重；若 ch-c 也被排除，会得到单章金标准。
        AssertVector(OnePeerGolden, weight);
    }

    [TestMethod]
    public async Task Feedback_SkipsGradientWhenPrimaryChapterAlreadyLeads()
    {
        await using var harness = new WeightMemoryHarness(Vectors);
        var ct = TestContext.CancellationToken;
        await harness.RegisterAsync("ch-b", "chapter-b", ct);   // 首选章：cue 上得分 1.0
        await harness.RegisterAsync("ch-c", "chapter-c", ct);

        await harness.Memory.ApplyFeedbackAsync(harness.NewFeedback("ch-b", "chapter-b"), ct);

        var weight = await harness.ReadWeightAsync("candidate", "ch-b");

        Assert.AreEqual(1f, weight[1], 1e-6f, "已经 1.0 领先，按门槛跳过梯度，权重不动。");
        Assert.AreEqual(0f, weight[0], 1e-6f);
        Assert.AreEqual(1L, harness.Memory.Counters.FeedbacksSkippedByMargin);
        Assert.AreEqual(0L, harness.Memory.Counters.FeedbacksApplied);
    }

    [TestMethod]
    public async Task Feedback_OnDeletedChapter_IsRecordedButTrainsNothing()
    {
        await using var harness = new WeightMemoryHarness(Vectors);
        var ct = TestContext.CancellationToken;
        await harness.RegisterAsync("ch-a", "chapter-a", ct);
        await harness.Memory.InvalidateChapterAsync(harness.Scope, "ch-a", "evt-delete-1", ct);

        await harness.Memory.ApplyFeedbackAsync(harness.NewFeedback("ch-a", "chapter-b"), ct);

        var weight = await harness.ReadWeightAsync("candidate", "ch-a");

        Assert.AreEqual(1f, weight[0], 1e-6f, "失效章节不再训练。");
        Assert.AreEqual(1L, harness.Memory.Counters.FeedbacksSkippedDeleted);
    }

    [TestMethod]
    public async Task Feedback_OnUnknownChapter_Throws()
    {
        await using var harness = new WeightMemoryHarness(Vectors);
        var ct = TestContext.CancellationToken;
        await harness.RegisterAsync("ch-a", "chapter-a", ct);

        await Assert.ThrowsExactlyAsync<WeightMemoryException>(
            () => harness.Memory.ApplyFeedbackAsync(harness.NewFeedback("ch-missing", "chapter-b"), ct));
    }

    [TestMethod]
    public async Task Feedback_MidTransactionFailure_RollsBackWeightAndReceipt()
    {
        const string stage = "feedback.after-weight-write";
        // 只中断第一次：第二次提交必须能成功，才能证明回执也没有落盘（否则会被判重复而不训练）。
        var fired = 0;
        await using var harness = new WeightMemoryHarness(
            Vectors,
            WeightMemoryHarness.OptionsFactory(faultHook: point =>
            {
                if (point == stage && Interlocked.Exchange(ref fired, 1) == 0)
                    throw new InvalidOperationException("测试注入的中断。");
            }));

        var ct = TestContext.CancellationToken;
        await harness.RegisterAsync("ch-a", "chapter-a", ct);
        var feedback = harness.NewFeedback("ch-a", "chapter-b");

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => harness.Memory.ApplyFeedbackAsync(feedback, ct));

        var rolledBack = await harness.ReadWeightAsync("candidate", "ch-a");
        Assert.AreEqual(1f, rolledBack[0], 1e-6f, "权重写入必须随事务一起回滚。");
        Assert.AreEqual(0f, rolledBack[1], 1e-6f);
        Assert.AreEqual(0L, harness.Memory.Counters.FeedbacksApplied);

        // 同一个实例、同一个事件号重试：能成功即证明上一次的回执也没有落盘。
        await harness.Memory.ApplyFeedbackAsync(feedback, ct);
        var applied = await harness.ReadWeightAsync("candidate", "ch-a");
        AssertVector(SingleChapterGolden, applied);
        Assert.AreEqual(1L, harness.Memory.Counters.FeedbacksApplied);
    }

    /// <summary>逐分量比较，失配时报出第一个越界分量，避免"两个向量不相等"这种不可定位的失败。</summary>
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
