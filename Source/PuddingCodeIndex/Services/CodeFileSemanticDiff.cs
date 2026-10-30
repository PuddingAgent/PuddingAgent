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
/// <para>
/// 新增符号一般也不算 —— <b>除了新增重载</b>：它会改变既有调用点的重载解析结果，
/// 所以同容器、同名的既有重载会被列入变化（R1-03 门禁）。
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
        var currentIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var symbol in current ?? [])
        {
            if (symbol is null || string.IsNullOrWhiteSpace(symbol.SymbolId))
                continue;

            currentIds.Add(symbol.SymbolId);

            if (!previousById.TryGetValue(symbol.SymbolId, out var before))
            {
                // 新增符号一般不会打断已有绑定，**除了重载**：
                // 新增 Pick(string) 会让原本绑定到 Pick(object) 的调用改绑（重载解析变了），
                // 那些调用点必须重新绑定。因此把同容器同名的既有重载列为变化。
                // 没有同名旧重载时不产生任何变化 —— 否则退化成「一改就全量重绑」。
                if (IsOverloadable(symbol.Kind))
                {
                    foreach (var prior in previousById.Values)
                    {
                        if (prior.Kind == symbol.Kind
                            && string.Equals(prior.Name, symbol.Name, StringComparison.Ordinal)
                            && string.Equals(prior.Container, symbol.Container, StringComparison.Ordinal))
                        {
                            changed.Add(prior.SymbolId);
                        }
                    }
                }

                continue;
            }

            if (!HasSameSemantics(before, symbol))
                changed.Add(symbol.SymbolId);
        }

        // 剩下的（不在本次集合里的）就是消失的符号：依赖方必须重新绑定。
        // 注意“剩余”必须靠 currentIds 判断，不能靠边循环边 Remove —— 那会让新增重载
        // 看不到已被移除的同名旧重载，重载变化就被静默丢掉。
        foreach (var symbolId in previousById.Keys)
        {
            if (!currentIds.Contains(symbolId))
                changed.Add(symbolId);
        }

        return changed.ToArray();
    }

    /// <summary>可参与重载解析的成员种类：新增一个就会改变既有调用点的绑定。</summary>
    private static bool IsOverloadable(CodeSymbolKind kind) =>
        kind is CodeSymbolKind.Method or CodeSymbolKind.Constructor or CodeSymbolKind.Operator;

    /// <summary>两个符号在**语义上**是否等价（行号不算）。</summary>
    private static bool HasSameSemantics(CodeSymbolRecord left, CodeSymbolRecord right) =>
        string.Equals(left.Name, right.Name, StringComparison.Ordinal)
        && left.Kind == right.Kind
        && string.Equals(left.Signature, right.Signature, StringComparison.Ordinal)
        && string.Equals(left.Container, right.Container, StringComparison.Ordinal);
}
