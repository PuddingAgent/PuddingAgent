using PuddingCodeIndex.Contracts;

namespace PuddingCodeIndex.Services;

/// <summary>
/// 新旧符号集的**语义差异**（D3，2026-10-02）：哪些符号消失、哪些签名/种类/容器变了。
/// <para>
/// 只有列出来的符号才可能让**别的文件**失效（类型签名变化会让引用方需要重新绑定），
/// 因此依赖扩展必须建立在这份差异上，而不是「文件被改了」这种粗判据。
/// </para>
/// <para>
/// 行号变化不算语义变化：整段代码下移不会让任何引用方失效。
/// </para>
/// </summary>
public static class CodeFileSemanticDiff
{
    /// <summary>
    /// 计算需要让依赖方重新绑定的符号 id（消失的 ∪ 签名/种类/名称/容器变化的）。
    /// </summary>
    /// <param name="previous">该文件上一次已提交的符号（首次索引时为空）。</param>
    /// <param name="current">本次提取到的符号。</param>
    /// <returns>稳定排序、去重的符号 id。</returns>
    public static IReadOnlyList<string> ChangedSymbolIds(
        IReadOnlyList<CodeSymbolRecord>? previous,
        IReadOnlyList<CodeSymbolRecord>? current)
    {
        var previousById = new Dictionary<string, CodeSymbolRecord>(StringComparer.Ordinal);

        foreach (var symbol in previous ?? [])
        {
            if (symbol is not null && !string.IsNullOrWhiteSpace(symbol.SymbolId))
                previousById[symbol.SymbolId] = symbol;
        }

        if (previousById.Count == 0)
        {
            // 首次索引：调用方应从空基线处理这些符号；这里不把「全新文件」当成「符号变化」，
            // 因为没有任何已存在的依赖方可能失效（图里还没有它们）。
            return [];
        }

        var changed = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var symbol in current ?? [])
        {
            if (symbol is null || string.IsNullOrWhiteSpace(symbol.SymbolId))
                continue;

            if (!previousById.TryGetValue(symbol.SymbolId, out var before))
            {
                // 新符号：没有旧依赖方需要失效（新增不会打断已有绑定）。
                continue;
            }

            if (!HasSameSemantics(before, symbol))
                changed.Add(symbol.SymbolId);

            previousById.Remove(symbol.SymbolId);
        }

        // 剩下的就是消失的符号：依赖方必须重新绑定。
        foreach (var symbolId in previousById.Keys)
            changed.Add(symbolId);

        return changed.ToArray();
    }

    /// <summary>两个符号在**语义上**是否等价（行号不算）。</summary>
    private static bool HasSameSemantics(CodeSymbolRecord left, CodeSymbolRecord right) =>
        string.Equals(left.Name, right.Name, StringComparison.Ordinal)
        && left.Kind == right.Kind
        && string.Equals(left.Signature, right.Signature, StringComparison.Ordinal)
        && string.Equals(left.Container, right.Container, StringComparison.Ordinal);
}
