using PuddingMemoryGraph;

namespace PuddingMemoryGraphTests;

/// <summary>
/// 召回线索提取：该提的必须提出来，不该提的一条都不能多。
/// <para>
/// 两个方向都要测：只测「能提出线索」会让它退化成「什么都算线索」，
/// 于是每轮都去召回、预算被无关记忆占满；只测「不乱提」会退化成永远不召回。
/// </para>
/// </summary>
[TestClass]
public sealed class MemoryCueExtractorTests
{
    [TestMethod]
    public void Absolute_path_is_a_strong_cue()
    {
        var cues = MemoryCueExtractor.Extract(@"打开 D:\CodeProject\PuddingAgent\PuddingAgent\Docs\README.md 看看");

        var path = cues.FirstOrDefault(c => c.Kind == MemoryCueKind.Path);
        Assert.IsNotNull(path, "绝对路径应被识别为线索");
        Assert.IsTrue(path!.IsStrong, "路径是强线索 —— 它几乎总指向具体的记忆");
        StringAssert.Contains(path.Text, "README.md");
    }

    [TestMethod]
    public void Time_reference_words_are_strong_cues()
    {
        var cues = MemoryCueExtractor.Extract("上次说的那个压缩问题，继续");

        Assert.IsTrue(cues.Any(c => c.Kind == MemoryCueKind.TimeReference && c.IsStrong),
            "「上次」「继续」是强召回信号");
    }

    [TestMethod]
    public void Correction_is_a_strong_cue()
    {
        var cues = MemoryCueExtractor.Extract("不对，应该是 48 MiB 而不是 32 MiB");

        Assert.IsTrue(cues.Any(c => c.Kind == MemoryCueKind.Correction && c.IsStrong),
            "用户纠正时必须招回被推翻的旧结论，否则会再答错一次");
    }

    [TestMethod]
    public void Unfinished_marker_is_a_medium_cue()
    {
        var cues = MemoryCueExtractor.Extract("这个还没弄完，剩下的明天继续");

        var unfinished = cues.FirstOrDefault(c => c.Kind == MemoryCueKind.UnfinishedTask);
        Assert.IsNotNull(unfinished);
        Assert.IsFalse(unfinished!.IsStrong, "未完成状态是中等线索，不该单独触发全量召回");
    }

    [TestMethod]
    public void Quoted_name_is_an_entity_cue()
    {
        var cues = MemoryCueExtractor.Extract("「PuddingMemoryEngine」是哪个工程？");

        Assert.IsTrue(cues.Any(c => c.Kind == MemoryCueKind.Entity && c.Text.Contains("PuddingMemoryEngine")));
    }

    [TestMethod]
    public void Type_identifier_is_a_weak_entity_cue()
    {
        var cues = MemoryCueExtractor.Extract("看看 IAgentLoopHook 有没有实现");

        var entity = cues.FirstOrDefault(c => c.Kind == MemoryCueKind.Entity && c.Text == "IAgentLoopHook");
        Assert.IsNotNull(entity);
        Assert.IsTrue(entity!.Strength < 0.8, "裸标识符是弱线索");
    }

    [TestMethod]
    public void Plain_small_talk_produces_no_strong_cue()
    {
        var cues = MemoryCueExtractor.Extract("你好，今天天气不错");

        Assert.IsFalse(MemoryCueExtractor.ShouldRecall(cues),
            "普通闲聊不该触发召回 —— 否则预算会被无关记忆占满");
    }

    [TestMethod]
    public void Empty_or_whitespace_text_yields_nothing()
    {
        Assert.AreEqual(0, MemoryCueExtractor.Extract(null).Count);
        Assert.AreEqual(0, MemoryCueExtractor.Extract("   ").Count);
        Assert.IsFalse(MemoryCueExtractor.ShouldRecall([]));
    }

    [TestMethod]
    public void Duplicate_cues_are_deduplicated()
    {
        var cues = MemoryCueExtractor.Extract("上次说的问题，上次说的那个文件");

        var timeCues = cues.Where(c => c.Kind == MemoryCueKind.TimeReference && c.Text == "上次").ToList();
        Assert.AreEqual(1, timeCues.Count, "同一条线索不应重复出现");
    }

    [TestMethod]
    public void Cues_are_ordered_by_strength()
    {
        var cues = MemoryCueExtractor.Extract(@"不太对，看 D:\a\b.cs 里的 FooBarBaz");

        Assert.IsTrue(cues.Count >= 3);
        for (var i = 1; i < cues.Count; i++)
            Assert.IsTrue(cues[i - 1].Strength >= cues[i].Strength, "线索必须按强度降序，调用方才能按预算截断");
    }

    [TestMethod]
    public void Should_recall_only_when_a_strong_cue_exists()
    {
        Assert.IsTrue(MemoryCueExtractor.ShouldRecall(MemoryCueExtractor.Extract("继续上次那个")));
        Assert.IsFalse(MemoryCueExtractor.ShouldRecall(MemoryCueExtractor.Extract("这个还没弄完")));
    }
}
