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

    /// <summary>
    /// 召回触发是**组合判定**，不是"有强线索就触发"。
    /// <para>
    /// 语义依据（3000 条真实语料实测）：
    /// · 「继续」出现 46.9%，但那是"继续干活"的推进语义 ⇒ 只给 0.5，不触发；
    /// · 「还没弄完」是真的未完成 ⇒ 0.6，触发；
    /// · 路径/实体单独出现 ⇒ 不触发（它们只说明"当前在操作什么"，不说明"要想起过去"）。
    /// </para>
    /// </summary>
    [TestMethod]
    public void Should_recall_requires_a_memory_referencing_cue()
    {
        // 只推进、不忆旧：不触发
        Assert.IsFalse(MemoryCueExtractor.ShouldRecall(MemoryCueExtractor.Extract("继续")));

        // 真未完成语义：触发
        Assert.IsTrue(MemoryCueExtractor.ShouldRecall(MemoryCueExtractor.Extract("这个还没弄完")));

        // 时间引用：触发
        Assert.IsTrue(MemoryCueExtractor.ShouldRecall(MemoryCueExtractor.Extract("继续上次那个")));

        // 只有路径：不触发（实测这类占 42.9%，若触发则召回退化为无筛选）
        Assert.IsFalse(MemoryCueExtractor.ShouldRecall(
            MemoryCueExtractor.Extract(@"打开 D:\CodeProject\PuddingAgent\README.md")));
    }

    [TestMethod]
    public void Path_does_not_trigger_recall_on_its_own()
    {
        var cues = MemoryCueExtractor.Extract(@"看 D:\Data\databases\pudding_memory.db");

        Assert.IsTrue(cues.Any(c => c.Kind == MemoryCueKind.Path), "路径本身仍应被识别为线索");
        Assert.IsFalse(MemoryCueExtractor.ShouldRecall(cues), "但路径单独不触发召回");
    }

    [TestMethod]
    public void Escape_sequence_is_not_a_path()
    {
        // 真实语料里 n:\n / a:\n1. / S:\nnone 这类转义串被误判为路径上百次。
        var cues = MemoryCueExtractor.Extract(@"a:\n1. S:\nnone Y:\n");

        Assert.IsFalse(cues.Any(c => c.Kind == MemoryCueKind.Path), "转义序列不是路径");
    }
}
