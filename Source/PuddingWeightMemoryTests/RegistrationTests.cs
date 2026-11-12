using PuddingWeightMemory;

namespace PuddingWeightMemoryTests;

/// <summary>
/// 章节登记：幂等、版本前进、事件标识被复用时的硬失败。
/// </summary>
[TestClass]
public sealed class RegistrationTests
{
    private static readonly (string Text, float[] Vector)[] Vectors =
    [
        ("chapter-a", [1f, 0f, 0f, 0f]),
        ("chapter-b", [0f, 1f, 0f, 0f]),
    ];

    [TestMethod]
    public async Task Register_SameEventTwice_ProducesOneRowAndCountsDuplicate()
    {
        await using var harness = new WeightMemoryHarness(Vectors);
        var chapter = harness.NewChapter("ch-a", "chapter-a");
        var ct = TestContext.CancellationToken;

        await harness.Memory.RegisterAsync(chapter, ct);
        await harness.Memory.RegisterAsync(chapter, ct);

        Assert.AreEqual(1L, await harness.CountChapterRowsAsync("ch-a"), "同一事件重复登记不能产生第二条章节记录。");
        Assert.AreEqual(1L, await harness.CountWeightsAsync("active", "ch-a"), "活动快照里也不能出现第二行权重。");
        Assert.AreEqual(1L, await harness.CountWeightsAsync("candidate", "ch-a"), "候选快照同理。");
        Assert.AreEqual(1L, harness.Memory.Counters.RegistrationsApplied);
        Assert.AreEqual(1L, harness.Memory.Counters.RegistrationsDuplicate);
    }

    [TestMethod]
    public async Task Register_SameEventWithDifferentContent_ThrowsInsteadOfSilentlyOverwriting()
    {
        await using var harness = new WeightMemoryHarness(Vectors);
        var ct = TestContext.CancellationToken;
        var chapter = harness.NewChapter("ch-a", "chapter-a");
        await harness.Memory.RegisterAsync(chapter, ct);

        var reused = chapter with { SourceCue = "chapter-b" };

        await Assert.ThrowsExactlyAsync<WeightMemoryException>(
            () => harness.Memory.RegisterAsync(reused, ct));
    }

    [TestMethod]
    public async Task Register_NewEventForSameChapter_AdvancesRevisionKeepingSingleRow()
    {
        await using var harness = new WeightMemoryHarness(Vectors);
        var ct = TestContext.CancellationToken;

        await harness.RegisterAsync("ch-a", "chapter-a", ct);
        await harness.RegisterAsync("ch-a", "chapter-a", ct);

        Assert.AreEqual(2L, await harness.ReadRevisionAsync("ch-a"), "第二次登记是新版本，版本号前进。");
        Assert.AreEqual(1L, await harness.CountChapterRowsAsync("ch-a"), "版本链在章节表里仍是一行。");
    }

    [TestMethod]
    public async Task Register_InitialWeightIsTheNormalisedSourceCue()
    {
        await using var harness = new WeightMemoryHarness(Vectors);
        await harness.RegisterAsync("ch-a", "chapter-a", TestContext.CancellationToken);

        var weight = await harness.ReadWeightAsync("active", "ch-a");

        // 合同里没有章节正文，登记线索是唯一可编码文本：权重 = unit(线索向量)。
        Assert.AreEqual(1f, weight[0], 1e-6f);
        Assert.AreEqual(0f, weight[1], 1e-6f);
    }

    public TestContext TestContext { get; set; } = null!;
}
