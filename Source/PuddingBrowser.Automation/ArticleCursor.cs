using Pudding.Contracts.Desktop;

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

    /// <summary>
    /// 编码：kind + 索引 + **页面/版本身份**。
    /// 只带索引的游标有一个真实缺口：换页面或文档被改过之后，同一个索引会指向**另一份内容**，
    /// 调用方却以为在续读同一篇文章（不报错，只是内容接不上）。带上身份就能明确拒绝。
    /// </summary>
    public static string Encode(string kind, int index, string pageIdentity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentException.ThrowIfNullOrWhiteSpace(pageIdentity);
        return $"{kind}:{index}@{pageIdentity}";
    }

    /// <summary>
    /// 页面/版本身份（<c>p{页面标识短哈希}-v{版本}</c>）。
    /// 页面标识只取短哈希：游标要对调用方保持不透明，同时**不能**把完整页面 id 当成游标的一部分到处传。
    /// </summary>
    public static string PageIdentityOf(string pageId, long pageVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pageId);
        ArgumentOutOfRangeException.ThrowIfNegative(pageVersion);
        return $"p{StableHash.Short(pageId)}-v{pageVersion}";
    }

    /// <summary>解析：只接受 <c>{已知kind}:{非负整数}@{身份}</c>，其余一律拒绝。</summary>
    public static bool TryParse(string? cursor, out string kind, out int index, out string pageIdentity)
    {
        kind = string.Empty;
        index = 0;
        pageIdentity = string.Empty;

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

        // 身份部分必须形如 p{8 位十六进制}-v{非负整数}（严格：不接受空白/变体/缺字段）。
        var at = parts[1].IndexOf('@', StringComparison.Ordinal);
        if (at <= 0 || at == parts[1].Length - 1)
        {
            return false;
        }

        var identity = parts[1][(at + 1)..];
        var identityParts = identity.Split('-');
        if (identityParts.Length != 2
            || identityParts[0].Length != 9 || identityParts[0][0] != 'p'
            || !identityParts[0][1..].All(Uri.IsHexDigit)
            || identityParts[1].Length < 2 || identityParts[1][0] != 'v')
        {
            return false;
        }

        if (!long.TryParse(identityParts[1].AsSpan(1), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var version) || version < 0)
        {
            return false;
        }

        // 严格解析：游标是我们自己生成的机器令牌，因此不接受空白/符号/千分位等变体
        // （宽容解析会让畸形令牌悄悄变成"合法游标"，那正是要防的静默跳段）。
        if (!int.TryParse(parts[1].AsSpan(0, at), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed) || parsed < 0)
        {
            return false;
        }

        kind = parts[0];
        index = parsed;
        pageIdentity = identity;
        return true;
    }

    /// <summary>游标带的是不是**这一页这一版**（不符就必须拒绝，而不是照着索引往下读）。</summary>
    public static bool MatchesPage(string cursorPageIdentity, string pageId, long pageVersion) =>
        string.Equals(cursorPageIdentity, PageIdentityOf(pageId, pageVersion), StringComparison.Ordinal);

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
