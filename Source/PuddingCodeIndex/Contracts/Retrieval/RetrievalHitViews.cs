using PuddingCodeIndex.Contracts;

namespace PuddingCodeIndex.Contracts.Retrieval;

/// <summary>
/// 符号视图（ADR-089 §5.4 的"双视图"之一）：<b>含归属与命中原因摘要</b>。
/// </summary>
public sealed record RetrievalSymbolView
{
    internal RetrievalSymbolView(
        SymbolIdentity identity,
        string displayName,
        CodeSymbolKind symbolKind,
        string? languageRawKind,
        RetrievalHitKind bestHitKind,
        int hitCount,
        string reason)
    {
        Identity = identity;
        DisplayName = displayName;
        SymbolKind = symbolKind;
        LanguageRawKind = languageRawKind;
        BestHitKind = bestHitKind;
        HitCount = hitCount;
        Reason = reason;
    }

    /// <summary>符号身份（去重键）。</summary>
    public SymbolIdentity Identity { get; }

    /// <summary>显示名。</summary>
    public string DisplayName { get; }

    /// <summary>通用符号种类。</summary>
    public CodeSymbolKind SymbolKind { get; }

    /// <summary>语言原始种类。</summary>
    public string? LanguageRawKind { get; }

    /// <summary>最佳命中层。</summary>
    public RetrievalHitKind BestHitKind { get; }

    /// <summary>该符号的命中条数。</summary>
    public int HitCount { get; }

    /// <summary>命中原因摘要（排序不再神秘）。</summary>
    public string Reason { get; }
}

/// <summary>
/// 文件视图（ADR-089 §5.4 的"双视图"之二）：<b>文件 + 命中原因摘要</b>。
/// 这样"找文件"和"找类"都能一次拿到。
/// </summary>
public sealed record RetrievalFileView
{
    internal RetrievalFileView(string filePath, int hitCount, RetrievalHitKind bestHitKind, string reason)
    {
        FilePath = filePath;
        HitCount = hitCount;
        BestHitKind = bestHitKind;
        Reason = reason;
    }

    /// <summary>文件路径（归一化）。</summary>
    public string FilePath { get; }

    /// <summary>该文件的命中条数。</summary>
    public int HitCount { get; }

    /// <summary>最佳命中层。</summary>
    public RetrievalHitKind BestHitKind { get; }

    /// <summary>命中原因摘要。</summary>
    public string Reason { get; }
}

/// <summary>
/// 双视图派生（ADR-089 §5.4）：从**已按全序排列**的命中派生 symbols / files 两个视图。
/// <para>
/// "最佳"= 输入顺序中该分组的首条（输入已按 intent 全序排列 ⇒ 首条即最相关）。
/// 派生是纯函数且与输入顺序一致，因此可复现。
/// </para>
/// </summary>
public static class RetrievalHitViews
{
    /// <summary>符号视图（按符号身份分组；<b>同一符号只出现一次</b>，见 §2.4）。</summary>
    public static IReadOnlyList<RetrievalSymbolView> Symbols(IReadOnlyList<RetrievalHit> hits)
    {
        ArgumentNullException.ThrowIfNull(hits);

        var order = new List<SymbolIdentity>();
        var groups = new Dictionary<SymbolIdentity, List<RetrievalHit>>();
        foreach (var hit in hits)
        {
            if (!groups.TryGetValue(hit.Identity, out var group))
            {
                groups[hit.Identity] = group = [];
                order.Add(hit.Identity);
            }

            group.Add(hit);
        }

        return
        [
            .. order.Select(identity =>
            {
                var group = groups[identity];
                var best = group[0];
                return new RetrievalSymbolView(
                    identity,
                    best.SymbolName,
                    best.SymbolKind,
                    best.LanguageRawKind,
                    best.HitKind,
                    group.Count,
                    Describe(best, group.Count));
            })
        ];
    }

    /// <summary>文件视图（按文件路径分组）。</summary>
    public static IReadOnlyList<RetrievalFileView> Files(IReadOnlyList<RetrievalHit> hits)
    {
        ArgumentNullException.ThrowIfNull(hits);

        var order = new List<string>();
        var groups = new Dictionary<string, List<RetrievalHit>>(StringComparer.Ordinal);
        foreach (var hit in hits)
        {
            var path = hit.Evidence.NormalizedFilePath;
            if (!groups.TryGetValue(path, out var group))
            {
                groups[path] = group = [];
                order.Add(path);
            }

            group.Add(hit);
        }

        return
        [
            .. order.Select(path =>
            {
                var group = groups[path];
                var best = group[0];
                return new RetrievalFileView(path, group.Count, best.HitKind, Describe(best, group.Count));
            })
        ];
    }

    private static string Describe(RetrievalHit best, int hitCount) =>
        hitCount == 1
            ? $"{best.HitKind}: {best.Why}"
            : $"{best.HitKind}: {best.Why}（共 {hitCount} 条命中）";
}
