using System.Text;

namespace PuddingMemoryEngine.Infrastructure.Text;

/// <summary>
/// 文本切块器——按句子边界切分长文本，相邻块带重叠，纯函数、确定性实现。
/// 供会话级语义检索（WP-L2b）将消息切成可嵌入的块。
/// </summary>
public static class TextChunker
{
    private static readonly char[] SentenceEnders = ['。', '！', '？', '!', '?', '\n'];

    /// <summary>
    /// 将文本切成不超过 <paramref name="maxChars"/> 的块。
    /// <list type="bullet">
    /// <item>null / 空白文本返回空列表。</item>
    /// <item>长度不超过 <paramref name="maxChars"/> 返回单块。</item>
    /// <item>长文本优先按句子边界（。！？!?\n）切分，单句超长才硬切。</item>
    /// <item>相邻块重叠约 <paramref name="overlapChars"/> 字符：取前块尾部句子并入下块开头，保持句子完整。</item>
    /// </list>
    /// </summary>
    public static IReadOnlyList<string> Chunk(string? text, int maxChars = 1024, int overlapChars = 128)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        if (maxChars <= 0)
            maxChars = 1024;
        if (overlapChars < 0)
            overlapChars = 0;

        if (text.Length <= maxChars)
            return [text];

        var sentences = SplitSentences(text);
        var chunks = new List<string>();
        // 每块由哪些句子组成（用于计算相邻块重叠种子；硬切块不参与，保持句子完整性）。
        var chunkSentences = new List<List<string>>();

        var i = 0;
        while (i < sentences.Count)
        {
            var buffer = new StringBuilder();
            var currentSentences = new List<string>();
            var addedNew = false;

            // ── 重叠种子：前块尾部句子并入本块开头（保持句子完整）──
            if (chunkSentences.Count > 0)
            {
                var prev = chunkSentences[^1];
                var seed = new List<string>();
                var seedLen = 0;
                for (var k = prev.Count - 1; k >= 0; k--)
                {
                    if (seedLen + prev[k].Length > overlapChars)
                        break;
                    seed.Insert(0, prev[k]);
                    seedLen += prev[k].Length;
                }
                // 单句超 overlapChars 时仍取最后一句作为种子（不超 maxChars），尽量保留重叠。
                if (seed.Count == 0 && prev.Count > 0 && prev[^1].Length <= maxChars)
                {
                    seed.Add(prev[^1]);
                }
                foreach (var s in seed)
                {
                    buffer.Append(s);
                    currentSentences.Add(s);
                }
            }

            // ── 贪婪累积句子 ──
            while (i < sentences.Count)
            {
                var s = sentences[i];

                // 单句超长：硬切（本句无法放进任何块）
                if (s.Length > maxChars)
                {
                    if (buffer.Length > 0)
                    {
                        chunks.Add(buffer.ToString());
                        chunkSentences.Add(currentSentences);
                        buffer = new StringBuilder();
                        currentSentences = new List<string>();
                        addedNew = false;
                    }

                    var remaining = s;
                    while (remaining.Length > maxChars)
                    {
                        chunks.Add(remaining[..maxChars]);
                        remaining = remaining[maxChars..];
                    }
                    if (remaining.Length > 0)
                        sentences[i] = remaining;
                    else
                        i++;
                    break;
                }

                if (buffer.Length + s.Length > maxChars)
                {
                    // 缓冲区只有重叠种子（尚无新内容）时丢弃种子重试，保证进度推进。
                    if (!addedNew && buffer.Length > 0)
                    {
                        buffer.Clear();
                        currentSentences.Clear();
                        continue;
                    }
                    break;
                }

                buffer.Append(s);
                currentSentences.Add(s);
                addedNew = true;
                i++;

                if (buffer.Length >= maxChars)
                    break;
            }

            if (buffer.Length > 0)
            {
                chunks.Add(buffer.ToString());
                chunkSentences.Add(currentSentences);
            }
        }

        return chunks;
    }

    /// <summary>按句末标点/换行切句，标点附在句尾；连续标点合并为一个句子结尾。</summary>
    private static List<string> SplitSentences(string text)
    {
        var sentences = new List<string>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (Array.IndexOf(SentenceEnders, text[i]) < 0)
                continue;

            var end = i;
            while (end < text.Length && Array.IndexOf(SentenceEnders, text[end]) >= 0)
                end++;
            sentences.Add(text[start..end]);
            start = end;
            i = end - 1;
        }

        if (start < text.Length)
            sentences.Add(text[start..]);

        return sentences;
    }
}
