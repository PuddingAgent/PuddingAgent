using System.Text;
using System.Text.Json;

namespace PuddingBrowser.Automation;

/// <summary>
/// 正文 markdown 组装（设计方案 §6.1 的 <c>mode=text</c>：标题/列表/链接/表格/代码块结构）。
///
/// **分工**：DOM 遍历与"正文在哪"（article/main/语义地标优先，再普通块回退）由驱动脚本完成，
/// 它只输出**语义块**；这里把块组装成 markdown。这样做的理由和本仓库的组件交付顺序一致 ——
/// 能在进程内纯函数验证的部分（结构、预算、分页边界）必须真的被验证，而不是只能靠"看起来对"。
///
/// 三条硬性承诺（对应 G6 验收）：
/// ① **不丢段、不重复**：每个块要么被渲染、要么被如实计入 <see cref="SkippedBlocks"/>；
///    分页永远落在**块边界**上，续读不会重复也不会跳过（不做块内切分）。
/// ② **结构保真**：标题层级、列表缩进、表格行列、代码块空白、链接目的地址都保留。
/// ③ **输出始终是合法 markdown**：非法/未知输入不抛出，按最保守方式降级并计数。
///
/// 块格式：**一行一个 JSON 对象**（驱动侧用 <c>JSON.stringify</c> 生成，转义天然正确）：
/// <code>
/// {"k":"h2","t":"Heading"}
/// {"k":"p","t":"Paragraph"}
/// {"k":"li","t":"Item","d":1}
/// {"k":"pre","t":"code\nline"}
/// {"k":"quote","t":"Quoted"}
/// {"k":"a","t":"Link text","h":"https://example.test/"}
/// {"k":"hr"}
/// {"k":"table","r":[["h1","h2"],["c1","c2"]]}
/// </code>
/// </summary>
public static class ArticleMarkdown
{
    /// <summary>正文的单次输出预算（设计方案 §6.1：每页 4,096 字节）。</summary>
    public const int DefaultMaxBytes = 4_096;

    /// <summary>块数上限（防御：驱动异常时也不让组装变成无界工作）。</summary>
    public const int MaxBlocks = 20_000;

    /// <summary>
    /// 组装正文 markdown。<paramref name="cursor"/> 是**块索引**（续读从这一块开始）。
    /// </summary>
    public static BrowserReadingResult Compose(
        string? blocks,
        int maxBytes = DefaultMaxBytes,
        int cursor = 0)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 64);
        ArgumentOutOfRangeException.ThrowIfNegative(cursor);

        var parsed = ParseBlocks(blocks);
        var skipped = parsed.Skipped;

        if (parsed.Blocks.Count == 0)
        {
            return new BrowserReadingResult(
                null, 0, 0, 0, skipped > 0, skipped > 0 ? BrowserReadingTruncation.IncompleteScan : BrowserReadingTruncation.None, null)
            {
                SkippedBlocks = skipped,
            };
        }

        var from = Math.Min(cursor, parsed.Blocks.Count);
        var pieces = new List<string>();
        var bytes = 0;
        var index = from;

        for (; index < parsed.Blocks.Count; index++)
        {
            var piece = Render(parsed.Blocks[index]);
            var cost = Encoding.UTF8.GetByteCount(piece) + (pieces.Count == 0 ? 0 : 2); // 空行分隔

            // 块边界分页：装不下就停在这里，**不切块**（切块会造成续读重复或丢半段）。
            // 例外：第一块本身就超预算 ⇒ 整块给出（宁可超一点，也不静默截断正文）。
            if (pieces.Count > 0 && bytes + cost > maxBytes)
            {
                break;
            }

            pieces.Add(piece);
            bytes += cost;
        }

        var consumedAll = index >= parsed.Blocks.Count;

        // 单块就超预算时我们**整块给出**（不在块内切，避免续读重复/丢半段），
        // 但必须如实标注"这份输出超出了请求的预算"，否则调用方会以为它在预算内。
        var overBudget = bytes > maxBytes;
        var truncated = !consumedAll || overBudget || skipped > 0;
        var reason = skipped > 0 && consumedAll && !overBudget
            ? BrowserReadingTruncation.IncompleteScan
            : consumedAll && !overBudget
                ? BrowserReadingTruncation.None
                : BrowserReadingTruncation.OutputBudget;

        return new BrowserReadingResult(
            pieces.Count == 0 ? null : string.Join("\n\n", pieces),
            parsed.Blocks.Count,
            pieces.Count,
            bytes,
            truncated,
            reason,
            reason == BrowserReadingTruncation.OutputBudget ? index : null)
        {
            SkippedBlocks = skipped,
        };
    }

    /// <summary>把一个语义块渲染成 markdown（单独的公开入口，便于逐块钉住格式）。</summary>
    public static string Render(ArticleBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);

        return block.Kind switch
        {
            "h1" or "h2" or "h3" or "h4" or "h5" or "h6" => RenderHeading(block),
            "li" => RenderListItem(block),
            "pre" => RenderCode(block),
            "quote" => RenderQuote(block),
            "hr" => "---",
            "table" => RenderTable(block),
            "a" => RenderLink(block),
            // 未知块只要有文本就按段落降级（**不丢内容**比"格式好看"重要），空的才计入跳过。
            _ => block.Text is { Length: > 0 } text ? text : string.Empty,
        };
    }

    private static string RenderHeading(ArticleBlock block)
    {
        var level = block.Kind[1] - '0';
        var text = block.Text?.Trim() ?? string.Empty;
        return text.Length == 0 ? new string('#', level) : $"{new string('#', level)} {text}";
    }

    private static string RenderListItem(ArticleBlock block)
    {
        // 嵌套列表用缩进表达（d=1 顶层）；空项渲染成空列表项而不是消失。
        var depth = Math.Max(1, block.Depth);
        return $"{new string(' ', (depth - 1) * 2)}- {block.Text?.Trim() ?? string.Empty}".TrimEnd();
    }

    private static string RenderCode(ArticleBlock block)
    {
        // 代码块**保留空白与换行**（方案要求）；内容里已有的围栏用更长的围栏包住。
        var code = block.Text ?? string.Empty;
        var fence = code.Contains("```", StringComparison.Ordinal) ? "````" : "```";
        return $"{fence}\n{code.TrimEnd('\n')}\n{fence}";
    }

    private static string RenderQuote(ArticleBlock block)
    {
        var lines = (block.Text ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        return string.Join('\n', lines.Select(line => line.Length == 0 ? ">" : $"> {line}"));
    }

    private static string RenderTable(ArticleBlock block)
    {
        if (block.Rows is not { Count: > 0 } rows)
        {
            return block.Text ?? string.Empty;
        }

        var columns = rows.Max(row => row.Count);
        var builder = new StringBuilder();

        for (var index = 0; index < rows.Count; index++)
        {
            builder.Append('|');
            for (var column = 0; column < columns; column++)
            {
                var cell = column < rows[index].Count ? rows[index][column] : string.Empty;
                builder.Append(' ').Append(EscapeCell(cell)).Append(" |");
            }

            builder.Append('\n');

            // 表头分隔行：只有第一行之后插入，且没有数据行时也补一行（否则不是合法表格）。
            if (index == 0)
            {
                builder.Append('|');
                for (var column = 0; column < columns; column++)
                {
                    builder.Append(" --- |");
                }

                builder.Append('\n');
            }
        }

        return builder.ToString().TrimEnd('\n');
    }

    private static string RenderLink(ArticleBlock block)
    {
        var text = block.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(block.Href))
        {
            return text;
        }

        // 链接目的地址必须保留（方案要求）；文本为空时用地址本身当文本。
        return text.Length == 0 ? $"<{block.Href}>" : $"[{text}]({block.Href})";
    }

    private static string EscapeCell(string cell) =>
        cell.Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace('\n', ' ')
            .Replace("|", "\\|", StringComparison.Ordinal)
            .Trim();

    /// <summary>
    /// 解析语义块（供 markdown 与 compact_json 两种表示共用）。
    /// **两种表示必须用同一份解析与同一套分页决策**，否则续读会在表示之间错位。
    /// </summary>
    public static (IReadOnlyList<ArticleBlock> Blocks, int Skipped) Parse(string? blocks)
    {
        var parsed = ParseBlocks(blocks);
        return (parsed.Blocks, parsed.Skipped);
    }

    private static (List<ArticleBlock> Blocks, int Skipped) ParseBlocks(string? blocks)
    {
        var list = new List<ArticleBlock>();
        var skipped = 0;

        if (string.IsNullOrWhiteSpace(blocks))
        {
            return (list, skipped);
        }

        foreach (var line in blocks.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (list.Count >= MaxBlocks)
            {
                skipped++;
                continue;
            }

            if (!TryParseBlock(line, out var block))
            {
                // 非法行不抛异常（输出必须保持合法），但**必须计数**：这就是"丢了几段"的账。
                skipped++;
                continue;
            }

            // 无文本又无结构的块没有任何可渲染内容 ⇒ 计入跳过而不是渲染空串。
            // 例外：hr/table 靠结构、a 靠目的地址（文本为空的链接仍要保留地址）。
            if (string.IsNullOrEmpty(block.Text)
                && block.Kind is not ("hr" or "table" or "a"))
            {
                skipped++;
                continue;
            }

            list.Add(block);
        }

        return (list, skipped);
    }

    private static bool TryParseBlock(string line, out ArticleBlock block)
    {
        block = null!;

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("k", out var kind)
                || kind.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            string? text = root.TryGetProperty("t", out var textElement) && textElement.ValueKind == JsonValueKind.String
                ? textElement.GetString()
                : null;
            var depth = root.TryGetProperty("d", out var depthElement) && depthElement.ValueKind == JsonValueKind.Number
                ? depthElement.GetInt32()
                : 1;
            var href = root.TryGetProperty("h", out var hrefElement) && hrefElement.ValueKind == JsonValueKind.String
                ? hrefElement.GetString()
                : null;

            IReadOnlyList<IReadOnlyList<string>>? rows = null;
            if (root.TryGetProperty("r", out var rowsElement) && rowsElement.ValueKind == JsonValueKind.Array)
            {
                rows = rowsElement.EnumerateArray()
                    .Select(row => (IReadOnlyList<string>)row.EnumerateArray()
                        .Select(cell => cell.ValueKind == JsonValueKind.String ? cell.GetString() ?? string.Empty : cell.ToString())
                        .ToArray())
                    .ToArray();
            }

            block = new ArticleBlock(kind.GetString() ?? string.Empty, text, depth, href, rows);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

/// <summary>一个语义块（驱动输出的正文结构单元）。</summary>
public sealed record ArticleBlock(
    string Kind,
    string? Text = null,
    int Depth = 1,
    string? Href = null,
    IReadOnlyList<IReadOnlyList<string>>? Rows = null);
