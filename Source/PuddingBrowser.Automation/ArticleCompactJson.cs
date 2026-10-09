using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace PuddingBrowser.Automation;

/// <summary>
/// 正文的 **compact_json** 表示（设计方案 §151：<c>mode</c> 决定读什么，<c>format</c> 决定表示成
/// markdown 还是 compact_json）。
///
/// 与 <see cref="ArticleMarkdown"/> 共用同一套语义（块边界分页、覆盖 100%、超预算仍合法输出），
/// 区别只在**形状**：markdown 给人看，compact_json 给程序读（保留块类型/层级/链接地址/表格行列）。
///
/// 一条硬性要求：**分页截断后输出仍必须是合法 JSON** —— 因此这里先算要放哪几块，
/// 再一次性序列化，括号永远成对；而不是边写边截（那会产出半截 JSON，调用方根本解析不了）。
/// </summary>
public static class ArticleCompactJson
{
    /// <summary>正文的单次输出预算（与 markdown 一致：每页 4,096 字节，§6.1）。</summary>
    public const int DefaultMaxBytes = 4_096;

    /// <summary>
    /// 组装 compact_json。<paramref name="cursor"/> 是**块索引**（与 markdown 表示同一套游标语义，
    /// 因此两种表示可以互相续读）。
    /// </summary>
    public static BrowserReadingResult Compose(
        string? blocks,
        int maxBytes = DefaultMaxBytes,
        int cursor = 0)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 64);
        ArgumentOutOfRangeException.ThrowIfNegative(cursor);

        // 按 **JSON 自己真实的字节数**分页：块成本 = 序列化后的字节（含引号与转义），
        // 分隔符是逗号，包装（{"blocks":[…],"truncated":…,"nextCursor":…}）也计入预算。
        // 用最坏情况的包装长度做估计，再在序列化后做一次**精确复核**（见下），
        // 这样既能提前分页，又保证"不超预算"这个承诺是真的。
        var parsed = ArticleMarkdown.Parse(blocks);
        if (parsed.Blocks.Count == 0)
        {
            return new BrowserReadingResult(
                null, 0, 0, 0, parsed.Skipped > 0,
                parsed.Skipped > 0 ? BrowserReadingTruncation.IncompleteScan : BrowserReadingTruncation.None, null)
            {
                SkippedBlocks = parsed.Skipped,
            };
        }

        var wrapper = WrapperBytes(parsed.Blocks.Count, cursor);
        var page = ArticlePaging.Page(
            parsed.Blocks,
            cursor,
            maxBytes,
            block => BlockBytes(block),
            ArticlePaging.JsonSeparatorBytes,
            wrapper);

        var from = page.From;
        var count = page.Count;
        var selected = parsed.Blocks.Skip(from).Take(count).ToArray();
        var truncated = !page.ConsumedAll;
        var json = Serialize(selected, truncated, truncated ? from + count : null);

        // 精确复核：包装里的 nextCursor 位数是分页之后才知道的，可能比估计更长。
        // 超出就**丢掉最后一块**再试（块边界不变，游标语义不变），直到装下或只剩一块。
        while (Encoding.UTF8.GetByteCount(json) > maxBytes && selected.Length > 1)
        {
            selected = selected[..^1];
            truncated = true;
            json = Serialize(selected, truncated, from + selected.Length);
        }

        var bytes = Encoding.UTF8.GetByteCount(json);
        var overBudget = bytes > maxBytes;   // 只剩一块仍超预算 ⇒ 整块给出并如实标注
        var nextCursor = truncated ? from + selected.Length : (int?)null;
        var reason = parsed.Skipped > 0 && !truncated && !overBudget
            ? BrowserReadingTruncation.IncompleteScan
            : !truncated && !overBudget
                ? BrowserReadingTruncation.None
                : BrowserReadingTruncation.OutputBudget;

        return new BrowserReadingResult(
            json,
            parsed.Blocks.Count,
            selected.Length,
            bytes,
            truncated || overBudget || parsed.Skipped > 0,
            reason,
            nextCursor)
        {
            SkippedBlocks = parsed.Skipped,
        };
    }

    /// <summary>包装的字节数估计：用最坏情况的游标位数（10 位）与固定字段。</summary>
    private static int WrapperBytes(int blockCount, int cursor)
    {
        // {"blocks":[],"truncated":true,"nextCursor":4294967295}
        var skeleton = blockCount == 0 && cursor == 0
            ? """{"blocks":[],"truncated":false,"nextCursor":null}"""
            : """{"blocks":[],"truncated":true,"nextCursor":4294967295}""";
        return Encoding.UTF8.GetByteCount(skeleton);
    }

    /// <summary>单块的 JSON 字节数（用同一套序列化器算，**不另写一份估算**，避免两处漂移）。</summary>
    private static int BlockBytes(ArticleBlock block) =>
        Encoding.UTF8.GetByteCount(SerializeBlock(block));

    private static string SerializeBlock(ArticleBlock block)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, CompactWriterOptions))
        {
            WriteBlock(writer, block);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// 紧凑表示的 JSON 写入选项。
    ///
    /// **不转义非 ASCII**（`UnsafeRelaxedJsonEscaping`）是刻意的：默认转义会把每个汉字写成
    /// `\uXXXX`（6 字节，是原文 3 字节的两倍），实测让"10 条中文标题 + 1536 字节预算"**装不下**
    /// （见 changelog 的 G5 实测）。JSON 允许直接输出 UTF-8，因此不转义既合法又省一半预算。
    /// 名字里的 "Unsafe" 针对的是**嵌入 HTML/JS** 的场景，本组件输出的是纯 JSON 数据，不拼 HTML。
    /// </summary>
    private static readonly JsonWriterOptions CompactWriterOptions = new()
    {
        Indented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    private static string Serialize(IReadOnlyList<ArticleBlock> blocks, bool truncated, int? nextCursor)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, CompactWriterOptions))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("blocks");
            writer.WriteStartArray();

            foreach (var block in blocks)
            {
                WriteBlock(writer, block);
            }

            writer.WriteEndArray();

            // 截断与游标**始终**写在同一个对象里：调用方不必靠"解析失败"猜测是否被截断。
            writer.WriteBoolean("truncated", truncated);
            if (nextCursor is { } cursor)
            {
                writer.WriteNumber("nextCursor", cursor);
            }
            else
            {
                writer.WriteNull("nextCursor");
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>单块写入（序列化与成本估算共用同一段代码 ⇒ 两者不可能漂移）。</summary>
    private static void WriteBlock(Utf8JsonWriter writer, ArticleBlock block)
    {
        writer.WriteStartObject();
        writer.WriteString("k", block.Kind);
        if (block.Text is { } text)
        {
            writer.WriteString("t", text);
        }

        if (block.Kind == "li")
        {
            writer.WriteNumber("d", Math.Max(1, block.Depth));
        }

        if (block.Href is { } href)
        {
            writer.WriteString("h", href);
        }

        if (block.Rows is { Count: > 0 } rows)
        {
            writer.WritePropertyName("r");
            writer.WriteStartArray();
            foreach (var row in rows)
            {
                writer.WriteStartArray();
                foreach (var cell in row)
                {
                    writer.WriteStringValue(cell);
                }

                writer.WriteEndArray();
            }

            writer.WriteEndArray();
        }

        writer.WriteEndObject();
    }
}
