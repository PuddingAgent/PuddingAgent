using System.Text;
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

        // 复用 ArticleMarkdown 的"整块、成对"分页：先把 markdown 表示算出来只是为了拿到
        // **同一份**块解析与分页决策（两种表示的页边界必须一致，否则续读会在表示之间错位）。
        var reference = ArticleMarkdown.Compose(blocks, maxBytes, cursor);
        var parsed = ArticleMarkdown.Parse(blocks);

        if (parsed.Blocks.Count == 0)
        {
            return new BrowserReadingResult(null, 0, 0, 0, reference.SkippedBlocks > 0,
                reference.SkippedBlocks > 0 ? BrowserReadingTruncation.IncompleteScan : BrowserReadingTruncation.None, null)
            {
                SkippedBlocks = reference.SkippedBlocks,
            };
        }

        var from = Math.Min(cursor, parsed.Blocks.Count);
        var taken = Math.Max(0, reference.ReturnedLines);
        var selected = parsed.Blocks.Skip(from).Take(taken).ToArray();

        var json = Serialize(selected, truncated: reference.Truncated, nextCursor: reference.NextCursor);

        // 序列化后的**真实**字节数与行数（不沿用 markdown 的估算：JSON 有转义与结构开销）。
        var bytes = Encoding.UTF8.GetByteCount(json);
        var overBudget = bytes > maxBytes;

        return new BrowserReadingResult(
            json,
            parsed.Blocks.Count,
            selected.Length,
            bytes,
            reference.Truncated || overBudget,
            reference.Truncation == BrowserReadingTruncation.None && overBudget
                ? BrowserReadingTruncation.OutputBudget
                : reference.Truncation,
            reference.NextCursor)
        {
            SkippedBlocks = reference.SkippedBlocks,
        };
    }

    private static string Serialize(IReadOnlyList<ArticleBlock> blocks, bool truncated, int? nextCursor)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("blocks");
            writer.WriteStartArray();

            foreach (var block in blocks)
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
}
