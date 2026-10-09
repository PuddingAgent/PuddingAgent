using System.Text.RegularExpressions;

namespace Pudding.DesktopService;

/// <summary>
/// **从修改日志里提取"内部证明不了的结论"**（交付清单的 <c>knownUnverified</c> 字段来源）。
///
/// 为什么要自动化：我在每轮 changelog 里都如实写了"真机未验证/CDP 行为需外部 smoke"之类的边界，
/// 但交接时**靠人回忆**必然漏。把日志当**事实来源**提取出来，清单就不可能悄悄漏掉这些项 ——
/// 而 <see cref="DeliveryManifest"/> 明确要求该字段非 null，"漏写"就不再是可能的失败模式。
///
/// 提取规则刻意保守：只认少数几个**明确表示未验证**的说法，宁可少提取（人工补），
/// 也不要把"已验证"的句子误当成未验证项。
/// </summary>
public static partial class KnownUnverifiedExtractor
{
    /// <summary>单次提取的条目上限（清单是给人读的，不是日志转储）。</summary>
    public const int MaxItems = 20;

    /// <summary>每条的最大长度（过长就截断，避免把整段日志搬进清单）。</summary>
    public const int MaxItemChars = 200;

    /// <summary>表示"未验证"的说法（保守词表：只认明确的否定式验证声明）。</summary>
    private static readonly string[] Markers =
    [
        "真机未验证",
        "未经真机验证",
        "未做真机验证",
        "未在真机",
        "无法在真机",
        "需外部 smoke",
        "需要外部 smoke",
        "未经外部",
        "无法自证",
        "环境限制",
    ];

    /// <summary>
    /// 一次提取的结果：**展示给清单的有界列表** + **实际找到的条数**。
    ///
    /// 为什么必须带 <see cref="TotalDistinct"/>：列表有 20 条上限（清单是给人读的），
    /// 但"只有 20 条"与"有 43 条、只列了 20 条"是**完全不同的事实**。
    /// 不带总数时，清单会把截断当成全部 —— 那正是"绝不报告假事实"要防的事。
    /// </summary>
    public sealed record KnownUnverifiedExtraction(IReadOnlyList<string> Items, int TotalDistinct);

    /// <summary>
    /// 从若干段 markdown 文本里提取未验证项（去重、保序、有界）。
    /// **永不返回 null**（返回空列表表示"确实没有"）—— 交付清单要求显式给出该字段。
    /// </summary>
    public static IReadOnlyList<string> Extract(IEnumerable<string?> documents) =>
        ExtractDetailed(documents).Items;

    /// <summary>同上，但**同时**给出实际找到的条数（用于如实标注"被截断"）。</summary>
    public static KnownUnverifiedExtraction ExtractDetailed(IEnumerable<string?> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);

        var items = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var totalDistinct = 0;

        foreach (var document in documents)
        {
            if (string.IsNullOrWhiteSpace(document))
            {
                continue;
            }

            // 跳过 YAML front matter：它的 description/tags 里也会出现"未验证"字样，
            // 但那是**文档摘要**不是边界条目（实测会把摘要混进交接清单）。
            var inFrontMatter = false;
            var frontMatterClosed = false;

            foreach (var rawLine in document.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
            {
                var trimmedLine = rawLine.Trim();

                if (!frontMatterClosed && trimmedLine == "---")
                {
                    if (!inFrontMatter)
                    {
                        inFrontMatter = true;
                        continue;
                    }

                    inFrontMatter = false;
                    frontMatterClosed = true;
                    continue;
                }

                if (inFrontMatter)
                {
                    continue;
                }

                var line = Clean(rawLine);
                if (line.Length == 0 || !Markers.Any(marker => line.Contains(marker, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var item = line.Length > MaxItemChars ? line[..MaxItemChars] + "…" : line;

                // 同一条边界在不同小节里重复出现时只报一次（清单不是日志）。
                if (seen.Add(item))
                {
                    totalDistinct++;
                    if (items.Count < MaxItems)
                    {
                        items.Add(item);
                    }
                }
            }
        }

        return new KnownUnverifiedExtraction(items, totalDistinct);
    }

    /// <summary>去掉 markdown 装饰（标题/项目符号/粗体/行内代码），压成一行。</summary>
    private static string Clean(string rawLine)
    {
        var line = rawLine.Trim();
        if (line.Length == 0)
        {
            return string.Empty;
        }

        line = LinePrefix().Replace(line, string.Empty);
        line = line.Replace("**", string.Empty, StringComparison.Ordinal)
            .Replace("`", string.Empty, StringComparison.Ordinal)
            .Replace(" | ", " ", StringComparison.Ordinal);

        return CollapseWhitespace().Replace(line, " ").Trim(' ', '|', '-', ':', '·');
    }

    [GeneratedRegex(@"^\s*(?:[-*+>]|\d+[.)]|#+)\s*")]
    private static partial Regex LinePrefix();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex CollapseWhitespace();
}
