using PuddingRuntime.Services.AgentLoop;

namespace PuddingRuntimeTests.Services.AgentLoop;

/// <summary>
/// Run 异常检测器：目标是「发现原地打转，而不是数步数」。
/// <para>
/// 两个方向都必须测：只测「能命中」会退化成过敏（长任务里偶发重复就被暂停，工作白做）；
/// 只测「不误判」会退化成永远不拦（复读机因为缓存命中率高，跑得比正常任务还便宜）。
/// 所以对照组（判据 1）和复位用例（判据 4）与命中用例同等重要。
/// </para>
/// </summary>
[TestClass]
public sealed class RunAnomalyDetectorTests
{
    private const long TimeBase = 1_700_000_000_000;

    /// <summary>窗口容量默认 20，与设计稿一致；测试只用默认阈值，不要覆写 Notice/Pause 档位，
    /// 否则把 PauseWindows 改小这种变异就取不到红。</summary>
    private static RunAnomalyObservation Said(string text, int index, int stepMs = 1000)
        => new(RunAnomalyObservationKind.Assistant, text, null, TimeBase + (long)index * stepMs);

    private static RunAnomalyObservation Did(string tool, string args, int index, int stepMs = 1000)
        => new(RunAnomalyObservationKind.Tool, tool, args, TimeBase + (long)index * stepMs);

    /// <summary>把同一段文本灌进一台新检测器，返回第三次（命中点）的判定，用来比对归一化 hash。</summary>
    private static RunAnomalyVerdict FeedRepeated(string text, int times = 3)
    {
        var detector = new RunAnomalyDetector();
        var last = default(RunAnomalyVerdict);
        for (var i = 1; i <= times; i++)
            last = detector.Observe(Said(text, i));

        return last;
    }

    [TestMethod]
    public void Thirty_distinct_observations_never_trip_the_detector()
    {
        var detector = new RunAnomalyDetector();

        for (var i = 0; i < 30; i++)
        {
            var observation = i % 2 == 0
                ? Said($"第 {i} 步：读取 sample-{i}.txt 并整理格式", i)
                : Did($"tool-{i}", $"{{\"path\":\"D:\\\\dir-{i}\\\\file-{i}.cs\"}}", i);

            var verdict = detector.Observe(observation);

            Assert.AreEqual(
                RunAnomalyAction.None,
                verdict.Action,
                $"第 {i + 1} 条内容互不相同的正常观测不得被判定为异常");
        }
    }

    [TestMethod]
    public void Same_assistant_text_third_time_is_notice_but_not_pause()
    {
        var detector = new RunAnomalyDetector();

        Assert.AreEqual(RunAnomalyAction.None, detector.Observe(Said("正在读取文件 A", 1)).Action);
        Assert.AreEqual(RunAnomalyAction.None, detector.Observe(Said("正在读取文件 A", 2)).Action);

        var third = detector.Observe(Said("正在读取文件 A", 3));

        Assert.AreEqual(RunAnomalyAction.Notice, third.Action, "第三次重复应在该条上立刻出 Notice");
        Assert.AreNotEqual(RunAnomalyAction.Pause, third.Action, "第一窗口只留痕，不得直接暂停");
        Assert.AreEqual(RunAnomalySignal.RepeatedAssistantOutput, third.Signal);
        Assert.AreEqual(3, third.HitCount);
        Assert.AreEqual(1, third.ConsecutiveHitWindows);
        Assert.IsNotNull(third.SampleHash);
    }

    [TestMethod]
    public void Second_consecutive_hit_window_escalates_to_pause()
    {
        var detector = new RunAnomalyDetector();
        var verdicts = new List<RunAnomalyVerdict>();

        // 窗口 1（1..20）：第 1..3 条重复，其余互不相同。
        for (var i = 1; i <= 20; i++)
            verdicts.Add(detector.Observe(Said(i <= 3 ? "重复的收尾文本" : $"窗口一第 {i} 条互不相同的内容", i)));

        // 窗口 2（21..40）：第 23..25 条重复同一段话，前两条保持干净，
        // 顺便验证窗口 1 的重复不会因为滑动窗口残留而把窗口 2 误判成命中。
        for (var i = 21; i <= 40; i++)
            verdicts.Add(detector.Observe(Said(i is >= 23 and <= 25 ? "重复的收尾文本" : $"窗口二第 {i} 条互不相同的内容", i)));

        Assert.AreEqual(RunAnomalyAction.Notice, verdicts[2].Action, "窗口 1 命中只出 Notice");
        Assert.AreEqual(1, verdicts[19].ConsecutiveHitWindows);
        Assert.AreEqual(RunAnomalyAction.None, verdicts[20].Action, "窗口 2 起始两条仍属正常");

        var pauseVerdict = verdicts[24];
        Assert.AreEqual(RunAnomalyAction.Pause, pauseVerdict.Action, "连续第二窗口命中即暂停");
        Assert.AreEqual(RunAnomalySignal.RepeatedAssistantOutput, pauseVerdict.Signal);
        Assert.IsGreaterThanOrEqualTo(2, pauseVerdict.ConsecutiveHitWindows, "连续命中窗口数必须累加到 2");
    }

    [TestMethod]
    public void Clean_window_resets_consecutive_hit_count()
    {
        var detector = new RunAnomalyDetector();
        var verdicts = new List<RunAnomalyVerdict>();

        for (var i = 1; i <= 20; i++)
            verdicts.Add(detector.Observe(Said(i <= 3 ? "复位用例的重复文本" : $"第一窗口第 {i} 条互不相同的内容", i)));
        for (var i = 21; i <= 40; i++)
            verdicts.Add(detector.Observe(Said($"干净窗口第 {i} 条互不相同的内容", i)));
        for (var i = 41; i <= 60; i++)
            verdicts.Add(detector.Observe(Said(i <= 43 ? "复位用例的重复文本" : $"第三窗口第 {i} 条互不相同的内容", i)));

        Assert.AreEqual(1, verdicts[2].ConsecutiveHitWindows, "窗口 1 命中");
        Assert.AreEqual(0, verdicts[39].ConsecutiveHitWindows, "干净窗口结束必须把连续命中计数清零");
        Assert.AreEqual(RunAnomalyAction.None, verdicts[39].Action);

        var third = verdicts[42];
        Assert.AreEqual(RunAnomalyAction.Notice, third.Action, "计数清零后重新从第一窗口算起，只能 Notice");
        Assert.AreEqual(1, third.ConsecutiveHitWindows);
        Assert.IsTrue(verdicts.All(v => v.Action != RunAnomalyAction.Pause), "全程不得出现暂停：偶发重复不应被累加");
    }

    [TestMethod]
    public void Repeated_tool_action_with_same_arguments_is_notice()
    {
        var detector = new RunAnomalyDetector();

        Assert.AreEqual(RunAnomalyAction.None, detector.Observe(Did("read_file", "{\"path\":\"D:\\\\a.cs\"}", 1)).Action);
        Assert.AreEqual(RunAnomalyAction.None, detector.Observe(Did("read_file", "{\"path\":\"D:\\\\a.cs\"}", 2)).Action);

        var third = detector.Observe(Did("read_file", "{\"path\":\"D:\\\\a.cs\"}", 3));

        Assert.AreEqual(RunAnomalyAction.Notice, third.Action);
        Assert.AreEqual(RunAnomalySignal.RepeatedToolAction, third.Signal);
        Assert.AreEqual(3, third.HitCount);
    }

    [TestMethod]
    public void Frequency_spike_across_two_windows_pauses()
    {
        var detector = new RunAnomalyDetector();
        var verdicts = new List<RunAnomalyVerdict>();

        // 窗口 1/2：每 1000ms 一条 → 约 63 events/min，作为基线。
        for (var i = 1; i <= 40; i++)
            verdicts.Add(detector.Observe(Said($"第 {i} 条不同的观测内容", i, stepMs: 1000)));

        // 窗口 3：20 条压在 2983ms 内 → 约 402 events/min，超过基线 5 倍。
        for (var i = 41; i <= 60; i++)
        {
            verdicts.Add(detector.Observe(new RunAnomalyObservation(
                RunAnomalyObservationKind.Assistant,
                $"第 {i} 条不同的观测内容",
                null,
                TimeBase + 40_000 + (long)(i - 41) * 157)));
        }

        // 窗口 4：再压到 494ms 内 → 约 2429 events/min，相对新基线仍是尖峰（连续两窗）。
        for (var i = 61; i <= 80; i++)
        {
            verdicts.Add(detector.Observe(new RunAnomalyObservation(
                RunAnomalyObservationKind.Assistant,
                $"第 {i} 条不同的观测内容",
                null,
                TimeBase + 44_000 + (long)(i - 61) * 26)));
        }

        var firstSpike = verdicts[59];
        Assert.AreEqual(RunAnomalyAction.Notice, firstSpike.Action, "第一个尖峰窗口只出 Notice");
        Assert.AreEqual(RunAnomalySignal.FrequencySpike, firstSpike.Signal);

        var secondSpike = verdicts[79];
        Assert.AreEqual(RunAnomalyAction.Pause, secondSpike.Action, "连续第二个尖峰窗口才暂停");
        Assert.AreEqual(RunAnomalySignal.FrequencySpike, secondSpike.Signal);
        Assert.AreEqual(2, secondSpike.ConsecutiveHitWindows);
    }

    [TestMethod]
    public void Texts_differing_only_by_timestamp_and_hex_share_the_same_hash()
    {
        var variantA = FeedRepeated("正在处理 id=abcdef0123456789 at 2026-11-21T13:25:36.130+08:00");
        var variantB = FeedRepeated("正在处理 id=0123456789abcdef at 2026-11-21 13:26:01");
        var variantC = FeedRepeated("正在处理 id=deadbeefcafe1234 at 2026-11-21T13:27:59.412+08:00");

        Assert.AreEqual(RunAnomalyAction.Notice, variantA.Action);
        Assert.AreEqual(variantA.SampleHash, variantB.SampleHash, "时区偏移写法不同也必须归一成同一 hash");
        Assert.AreEqual(variantA.SampleHash, variantC.SampleHash, "只换了 hex id 也必须归一成同一 hash");

        // 同一窗口里三条只在时间戳/hex 上有差异的观测，就是复读机的形态。
        var detector = new RunAnomalyDetector();
        detector.Observe(Said("正在处理 id=abcdef0123456789 at 2026-11-21T13:25:36.130+08:00", 1));
        detector.Observe(Said("正在处理 id=0123456789abcdef at 2026-11-21 13:26:01", 2));
        var third = detector.Observe(Said("正在处理 id=deadbeefcafe1234 at 2026-11-21T13:27:59.412+08:00", 3));

        Assert.AreEqual(RunAnomalyAction.Notice, third.Action, "归一化后必须能触发重复");
    }

    [TestMethod]
    public void Genuinely_different_texts_do_not_share_a_hash()
    {
        var first = FeedRepeated("全部文件已写入 temp 目录");
        var second = FeedRepeated("等待用户确认部署窗口");

        Assert.AreEqual(RunAnomalyAction.Notice, first.Action);
        Assert.AreEqual(RunAnomalyAction.Notice, second.Action);
        Assert.AreNotEqual(first.SampleHash, second.SampleHash, "内容确实不同就不能归一成同一 hash");

        var detector = new RunAnomalyDetector();
        Assert.AreEqual(RunAnomalyAction.None, detector.Observe(Said("全部文件已写入 temp 目录", 1)).Action);
        Assert.AreEqual(RunAnomalyAction.None, detector.Observe(Said("等待用户确认部署窗口", 2)).Action);
        Assert.AreEqual(RunAnomalyAction.None, detector.Observe(Said("全部文件已写入 temp 目录", 3)).Action);
        Assert.AreEqual(RunAnomalyAction.None, detector.Observe(Said("等待用户确认部署窗口", 4)).Action);
    }

    [TestMethod]
    public void Blank_content_is_never_counted_as_repetition()
    {
        var detector = new RunAnomalyDetector();

        for (var i = 1; i <= 5; i++)
        {
            var verdict = detector.Observe(Said("   \n\t  ", i));
            Assert.AreEqual(RunAnomalyAction.None, verdict.Action, "空内容不参与重复计数");
            Assert.AreEqual(RunAnomalySignal.None, verdict.Signal);
            Assert.IsNull(verdict.SampleHash);
        }
    }

    [TestMethod]
    public void Same_input_sequence_yields_identical_verdicts()
    {
        static List<RunAnomalyVerdict> Run()
        {
            var detector = new RunAnomalyDetector();
            var verdicts = new List<RunAnomalyVerdict>();

            for (var i = 1; i <= 20; i++)
                verdicts.Add(detector.Observe(Said(i <= 3 ? "同一段复读文本" : $"第一窗口第 {i} 条互不相同的内容", i)));
            for (var i = 21; i <= 40; i++)
                verdicts.Add(detector.Observe(i <= 24
                    ? Did("read_file", "{\"path\":\"D:\\\\b.cs\"}", i)
                    : Said($"第二窗口第 {i} 条互不相同的内容", i)));

            return verdicts;
        }

        var first = Run();
        var second = Run();

        Assert.HasCount(first.Count, second);
        for (var i = 0; i < first.Count; i++)
            Assert.AreEqual(first[i], second[i], $"第 {i + 1} 条判定必须逐位一致（hash 不得依赖进程随机种子）");
    }

    [TestMethod]
    public void Reset_clears_window_and_counters()
    {
        var detector = new RunAnomalyDetector();
        detector.Observe(Said("正在读取文件 A", 1));
        detector.Observe(Said("正在读取文件 A", 2));
        Assert.HasCount(2, detector.SnapshotWindow());

        detector.Reset();

        Assert.IsEmpty(detector.SnapshotWindow());
        var verdict = detector.Observe(Said("正在读取文件 A", 3));
        Assert.AreEqual(RunAnomalyAction.None, verdict.Action, "重置后重新计数，不得沿用历史");
        Assert.HasCount(1, detector.SnapshotWindow());
    }
}
