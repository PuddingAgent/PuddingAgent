namespace PuddingBrowser.Automation;

/// <summary>
/// 阅读分页的**不透明游标**（设计方案 §6.1 的"游标续读"）。
///
/// 为什么要带**表示标识**：markdown 与 compact_json 的块成本不同 ⇒ 页边界不同。
/// 一个裸整数游标从一种表示拿到、却用到另一种表示上，会**静默跳过**若干块（不报错、也不重复）——
/// 这是最难发现的一类数据丢失。把表示编进游标后，这种误用可以被**明确拒绝**。
///
/// 形状：<c>{kind}:{index}</c>（如 <c>md:3</c> / <c>json:5</c> / <c>lines:50</c>）。
/// 对调用方仍是不透明的：只要求"原样回传上一次拿到的 nextCursor"。
/// 解析 fail closed：未知 kind、非数字、负数、多余字段一律拒绝（不猜、不回退成 0）。
/// </summary>
public static class ArticleCursor
{
    /// <summary>markdown 正文表示。</summary>
    public const string MarkdownKind = "md";

    /// <summary>compact_json 正文表示。</summary>
    public const string CompactJsonKind = "json";

    /// <summary>outline/interactive 的行分页表示。</summary>
    public const string LinesKind = "lines";

    /// <summary>编码：kind + 块/行索引。</summary>
    public static string Encode(string kind, int index)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        return $"{kind}:{index}";
    }

    /// <summary>解析：只接受 <c>{已知kind}:{非负整数}</c>，其余一律拒绝。</summary>
    public static bool TryParse(string? cursor, out string kind, out int index)
    {
        kind = string.Empty;
        index = 0;

        if (string.IsNullOrWhiteSpace(cursor))
        {
            return false;
        }

        var parts = cursor.Trim().Split(':');
        if (parts.Length != 2)
        {
            return false;
        }

        if (parts[0] is not (MarkdownKind or CompactJsonKind or LinesKind))
        {
            return false;
        }

        // 严格解析：游标是我们自己生成的机器令牌，因此不接受空白/符号/千分位等变体
        // （宽容解析会让畸形令牌悄悄变成"合法游标"，那正是要防的静默跳段）。
        if (!int.TryParse(parts[1], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed) || parsed < 0)
        {
            return false;
        }

        kind = parts[0];
        index = parsed;
        return true;
    }

    /// <summary>
    /// 表示标识 ↔ <c>format</c> 线名（两种正文表示）。<c>lines</c> 没有对应 format（返回 <c>null</c>）。
    /// </summary>
    public static string? KindOfFormat(string? format) => format?.Trim().ToLowerInvariant() switch
    {
        "markdown" => MarkdownKind,
        "compact_json" => CompactJsonKind,
        _ => null,
    };

    /// <summary>该表示标识对应的 <c>format</c> 线名（用于给调用方写"该用哪个 format"的提示）。</summary>
    public static string FormatOfKind(string kind) => kind switch
    {
        MarkdownKind => "markdown",
        CompactJsonKind => "compact_json",
        _ => kind,
    };
}
