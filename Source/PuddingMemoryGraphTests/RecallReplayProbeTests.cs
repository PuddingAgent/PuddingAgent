using System.Text;
using System.Text.Json;
using PuddingMemoryGraph;

namespace PuddingMemoryGraphTests;

/// <summary>
/// 回放评估探针：拿**真实历史会话**跑线索提取，量化「会话能不能记起来」的第一环。
/// <para>
/// 为什么要单独做这个探针而不是普通单测：单测只能证明「规则按写的那样工作」，
/// 证明不了「在真实语料上会不会每次都触发、或永远不触发」。后者只能靠真实数据。
/// </para>
/// <para>
/// 门控：只有设置了 <c>PUDDING_REPLAY_TURNS</c>（JSONL 路径）才会真正跑，
/// 否则 <c>Assert.Inconclusive</c> 跳过 —— 与仓库既有的 gated probe 写法一致。
/// </para>
/// </summary>
[TestClass]
public sealed class RecallReplayProbeTests
{
    [TestMethod]
    public void Replay_real_user_turns_to_measure_cue_coverage()
    {
        var turnsPath = Environment.GetEnvironmentVariable("PUDDING_REPLAY_TURNS");
        if (string.IsNullOrWhiteSpace(turnsPath) || !File.Exists(turnsPath))
            Assert.Inconclusive("未设置 PUDDING_REPLAY_TURNS，跳过回放评估。");

        var outPath = Environment.GetEnvironmentVariable("PUDDING_REPLAY_OUT")
                      ?? Path.Combine(Path.GetTempPath(), "recall-replay-report.md");

        var total = 0;
        var withCue = 0;
        var strong = 0;
        var kindHits = new Dictionary<MemoryCueKind, int>();
        var cueTexts = new Dictionary<string, int>(StringComparer.Ordinal);
        var strongCueTexts = new Dictionary<string, int>(StringComparer.Ordinal);
        long totalChars = 0;

        // 按 turn 长度分桶：用来区分「触发太松」和「样本本身就偏任务型」。
        var buckets = new (string Name, int Min, int Max)[]
        {
            ("<50 字符", 0, 49),
            ("50~200", 50, 199),
            ("200~1000", 200, 999),
            ("≥1000", 1000, int.MaxValue),
        };
        var bucketTotal = new Dictionary<string, int>(StringComparer.Ordinal);
        var bucketStrong = new Dictionary<string, int>(StringComparer.Ordinal);

        // 短对话里「没触发」的样本：漏召回只能靠人看，工具负责把它们捞出来。
        var untriggeredShort = new List<string>();

        foreach (var line in File.ReadLines(turnsPath, Encoding.UTF8))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            string? text;
            try
            {
                using var doc = JsonDocument.Parse(line);
                text = doc.RootElement.TryGetProperty("text", out var t) ? t.GetString() : null;
            }
            catch (JsonException)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(text))
                continue;

            total++;
            totalChars += text.Length;

            var cues = MemoryCueExtractor.Extract(text);
            if (cues.Count > 0)
                withCue++;
            var willRecall = MemoryCueExtractor.ShouldRecall(cues);
            if (willRecall)
                strong++;

            var len = text.Length;

            if (!willRecall && len is > 2 and <= 200 && untriggeredShort.Count < 40)
                untriggeredShort.Add(text.Replace("\n", " ").Trim());
            foreach (var b in buckets)
            {
                if (len < b.Min || len > b.Max)
                    continue;
                bucketTotal[b.Name] = bucketTotal.GetValueOrDefault(b.Name) + 1;
                if (willRecall)
                    bucketStrong[b.Name] = bucketStrong.GetValueOrDefault(b.Name) + 1;
                break;
            }

            foreach (var kind in cues.Select(c => c.Kind).Distinct())
                kindHits[kind] = kindHits.GetValueOrDefault(kind) + 1;

            foreach (var cue in cues)
            {
                cueTexts[cue.Text] = cueTexts.GetValueOrDefault(cue.Text) + 1;
                if (cue.IsStrong)
                    strongCueTexts[cue.Text] = strongCueTexts.GetValueOrDefault(cue.Text) + 1;
            }
        }

        Assert.IsTrue(total > 0, "回放文件里没有可分析的 user turn");

        var sb = new StringBuilder();
        sb.AppendLine("# 召回线索回放报告");
        sb.AppendLine();
        sb.AppendLine($"样本：{total} 条真实 user turn，平均 {totalChars / Math.Max(1, total)} 字符");
        sb.AppendLine();
        sb.AppendLine("| 指标 | 数量 | 占比 |");
        sb.AppendLine("|---|---:|---:|");
        sb.AppendLine($"| 至少提取到 1 条线索 | {withCue} | {Pct(withCue, total)} |");
        sb.AppendLine($"| **强线索（会触发召回）** | {strong} | {Pct(strong, total)} |");
        sb.AppendLine($"| 完全无线索 | {total - withCue} | {Pct(total - withCue, total)} |");
        sb.AppendLine();
        sb.AppendLine("## 按 turn 长度分桶（判断是「触发太松」还是「样本偏任务型」）");
        sb.AppendLine();
        sb.AppendLine("| 长度 | turn 数 | 占比 | 会触发召回 | 触发率 |");
        sb.AppendLine("|---|---:|---:|---:|---:|");
        foreach (var b in buckets)
        {
            var totalB = bucketTotal.GetValueOrDefault(b.Name);
            var strongB = bucketStrong.GetValueOrDefault(b.Name);
            sb.AppendLine($"| {b.Name} | {totalB} | {Pct(totalB, total)} | {strongB} | {Pct(strongB, totalB)} |");
        }

        sb.AppendLine();
        sb.AppendLine("## 按线索种类（有多少 turn 命中该类）");
        sb.AppendLine();
        sb.AppendLine("| 种类 | turn 数 | 占比 |");
        sb.AppendLine("|---|---:|---:|");
        foreach (var kind in Enum.GetValues<MemoryCueKind>())
        {
            var n = kindHits.GetValueOrDefault(kind);
            sb.AppendLine($"| {kind} | {n} | {Pct(n, total)} |");
        }

        sb.AppendLine();
        sb.AppendLine("## 最高频强线索（Top 15）");
        sb.AppendLine();
        foreach (var pair in strongCueTexts.OrderByDescending(p => p.Value).Take(15))
            sb.AppendLine($"- `{Trim(pair.Key)}` × {pair.Value}");

        sb.AppendLine();
        sb.AppendLine("## 短对话未触发样本（供人工判「漏召回」）");
        sb.AppendLine();
        sb.AppendLine("> 判断标准：这条是否**预设了累积状态**？若是，说明规则漏了。");
        sb.AppendLine();
        foreach (var s in untriggeredShort)
            sb.AppendLine($"- {Trim(s)}");

        File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(false));

        Console.WriteLine(sb.ToString());
        Console.WriteLine($"[REPLAY] report={outPath}");

        // 只做「数据存在且被解析」的断言；比例本身是观测值，不是通过/失败条件。
        Assert.IsTrue(withCue > 0, "真实语料上一条线索都提不出来，说明提取规则完全失效");
    }

    private static string Pct(int n, int total) =>
        total == 0 ? "-" : $"{100.0 * n / total:F1}%";

    private static string Trim(string s) =>
        s.Length <= 40 ? s : s[..40] + "…";
}
