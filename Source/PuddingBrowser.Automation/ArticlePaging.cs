namespace PuddingBrowser.Automation;

/// <summary>
/// 正文表示的**块边界分页引擎**（设计方案 §6.1 的"每页预算 + 游标续读"）。
///
/// 为什么要把分页单独抽出来：markdown 与 compact_json 是同一份内容的两种**形状**，
/// 但它们每个块占的字节数不同（JSON 有结构、转义与包装开销）。如果让两者共用"按 markdown 字节"
/// 的分页决策，compact_json 就可能超出调用方请求的预算——那正好违反 §151 的"合法输出"承诺。
/// 因此分页接受一个**成本函数**：各表示按自己真实会输出的字节数分页。
///
/// 三条不变式（两种表示都适用）：
/// ① 分页只落在**块边界**（绝不切块；切块会让续读重复或丢半段）；
/// ② 只要有块被选中，输出就**不会超过预算** —— 除非"单独第一块就超预算"，
///    那种情况整块给出并如实标注 <see cref="PageResult.OverBudget"/>；
/// ③ 同样的输入给出同样的分页（确定性），因此游标可以在两种表示之间互换。
/// </summary>
public static class ArticlePaging
{
    /// <summary>分页结果：选中的块区间、真实内容字节数与"单块超预算"事实。</summary>
    public readonly record struct PageResult(
        int From,
        int Count,
        int ContentBytes,
        bool OverBudget,
        bool ConsumedAll);

    /// <summary>空内容时包装本身的字节数（例如 <c>{"blocks":[],…}</c>），必须计入预算。</summary>
    public const int DefaultWrapperBytes = 0;

    /// <summary>块之间的分隔字节数（markdown 是空行 2 字节，JSON 是逗号 1 字节）。</summary>
    public const int MarkdownSeparatorBytes = 2;
    public const int JsonSeparatorBytes = 1;

    /// <summary>
    /// 按预算选块。<paramref name="blockCost"/> 返回**该块单独输出时的字节数**
    /// （不含分隔符与包装；两者由本方法加上）。
    /// </summary>
    public static PageResult Page(
        IReadOnlyList<ArticleBlock> blocks,
        int cursor,
        int maxBytes,
        Func<ArticleBlock, int> blockCost,
        int separatorBytes = MarkdownSeparatorBytes,
        int wrapperBytes = DefaultWrapperBytes)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        ArgumentNullException.ThrowIfNull(blockCost);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(cursor);

        var from = Math.Min(cursor, blocks.Count);
        var count = 0;
        var content = 0;
        var index = from;

        for (; index < blocks.Count; index++)
        {
            var cost = blockCost(blocks[index]) + (count == 0 ? 0 : separatorBytes);
            var projected = wrapperBytes + content + cost;

            // 装不下就停在**块边界**；例外：第一块本身超预算 ⇒ 整块给出（由 OverBudget 如实标注）。
            if (count > 0 && projected > maxBytes)
            {
                break;
            }

            content += cost;
            count++;
        }

        var consumedAll = index >= blocks.Count;
        var overBudget = wrapperBytes + content > maxBytes;

        return new PageResult(from, count, content, overBudget, consumedAll);
    }
}
