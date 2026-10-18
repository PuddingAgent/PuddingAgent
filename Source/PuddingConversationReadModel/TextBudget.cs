using System.Text;

namespace PuddingConversationReadModel;

/// <summary>
/// UTF-8 有界截断规则（Plan §5.4）。组件内所有预览截断都必须经过这里，不能在多个调用点各写一套。
/// <para>截断只按 UTF-8 字节数计算，不得切断代理对/码点；预算 &lt;= 0 时返回空字符串。</para>
/// </summary>
internal static class TextBudget
{
    /// <summary>UTF-8 编码字节数。</summary>
    internal static int Utf8Length(string text)
        => Encoding.UTF8.GetByteCount(text);

    /// <summary>取不超过 <paramref name="maxBytes"/> 的最长 UTF-8 前缀。</summary>
    internal static string TakeUtf8Prefix(string text, int maxBytes)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (maxBytes <= 0 || text.Length == 0)
            return string.Empty;

        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length <= maxBytes)
            return text;

        // bytes[maxBytes] 是第一个未包含的字节；若它是续字节（10xxxxxx），说明预算落在码点中间，
        // 必须回退到该码点的首字节，保证不切断 UTF-8 序列/代理对。
        var length = maxBytes;
        while (length > 0 && (bytes[length] & 0xC0) == 0x80)
            length--;

        return Encoding.UTF8.GetString(bytes, 0, length);
    }

    /// <summary>取不超过 <paramref name="maxBytes"/> 的最长 UTF-8 后缀（用于活动正文尾部预览）。</summary>
    internal static string TakeUtf8Tail(string text, int maxBytes)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (maxBytes <= 0 || text.Length == 0)
            return string.Empty;

        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length <= maxBytes)
            return text;

        // 从尾部取预算，再向后越过续字节，直到落在码点首字节上。
        var start = bytes.Length - maxBytes;
        while (start < bytes.Length && (bytes[start] & 0xC0) == 0x80)
            start++;

        return Encoding.UTF8.GetString(bytes, start, bytes.Length - start);
    }
}
