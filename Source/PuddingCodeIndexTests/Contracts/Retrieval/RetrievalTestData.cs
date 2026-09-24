using PuddingCodeIndex.Contracts;
using PuddingCodeIndex.Contracts.Retrieval;

namespace PuddingCodeIndexTests.Contracts.Retrieval;

/// <summary>
/// U4-2a 契约测试的共享构造器：让"命中/请求/落盘/建议"的构造短到能一眼看清测试意图。
/// <para>刻意只依赖 <c>PuddingCodeIndex</c>（组件边界，见 <c>ComponentBoundaryTests</c>）。</para>
/// </summary>
internal static class RetrievalTestData
{
    public const string IndexVersion = "index-v1";

    public const string DefaultScopeId = "scope-u4-2a";

    /// <summary>构造一条命中（默认是"符号名域命中的语义级声明"）。</summary>
    public static RetrievalHit Hit(
        string symbolId,
        string? filePath = null,
        int line = 10,
        RetrievalHitKind hitKind = RetrievalHitKind.Declaration,
        RetrievalConfidence confidence = RetrievalConfidence.Semantic,
        double score = 1.0,
        CodeSymbolKind symbolKind = CodeSymbolKind.Class,
        RetrievalRelationKind relation = RetrievalRelationKind.Unknown,
        RetrievalMatchTarget matchTargets = RetrievalMatchTarget.SymbolName,
        string? symbolName = null,
        string? why = null,
        IEnumerable<string>? scopes = null)
    {
        var path = filePath ?? $"Source/Proj/{symbolId}.cs";
        return new RetrievalHit(
            SymbolIdentity.Create(symbolId, path, line),
            symbolName ?? symbolId,
            symbolKind,
            hitKind,
            new RetrievalEvidence(path, line, snippet: "matched"),
            confidence,
            score,
            why ?? "symbol-name match",
            matchTargets,
            relation,
            languageRawKind: "class",
            container: null,
            scopeIds: scopes);
    }

    /// <summary>构造请求（缺省即 Auto + 默认预算）。</summary>
    public static RetrievalRequest Request(
        string query = "MessageDeliveryDispatcher",
        RetrievalIntent intent = RetrievalIntent.Auto,
        RetrievalFilter? filter = null,
        int pageSize = 10,
        int maxBytes = RetrievalBudget.DefaultMaxBytes,
        int maxTokens = RetrievalBudget.DefaultMaxTokens) =>
        new(query, DefaultScopeId, intent, filter, pageSize, cursor: null, maxBytes, maxTokens);

    /// <summary>构造落盘信息（路径落在 gitignore 覆盖的 <c>.pudding/</c> 下）。</summary>
    public static RetrievalOverflow Spill(
        int spilledCount,
        RetrievalBudgetKind triggeredBy = RetrievalBudgetKind.ItemCount) =>
        RetrievalOverflow.Create($".pudding/retrieval/{DefaultScopeId}-spill.jsonl", spilledCount, triggeredBy);

    /// <summary>恰好一条收窄建议（目录限制）。</summary>
    public static RetrievalNextStep NarrowStep(string? argument = "Source/PuddingRuntime") =>
        RetrievalNextStep.Narrow(
            RetrievalSuggestionKind.DirectoryLimit,
            "限定 scope=Source/PuddingRuntime 后再查",
            argument);

    /// <summary>小而稳定的文本命中（用于"量很大"的场景；估算体积刻意很小）。</summary>
    public static RetrievalHit TextHit(
        int index,
        string? filePath = null,
        IEnumerable<string>? scopes = null,
        RetrievalConfidence confidence = RetrievalConfidence.Lexical)
    {
        var path = filePath ?? $"S/F{index:D3}.cs";
        var line = 1 + (index % 7);
        return new RetrievalHit(
            SymbolIdentity.Create($"Sym.T{index:D3}", path, line),
            $"Sym{index:D3}",
            CodeSymbolKind.Class,
            RetrievalHitKind.TextHit,
            new RetrievalEvidence(path, line, snippet: null),
            confidence,
            1.0,
            "w",
            RetrievalMatchTarget.Body,
            RetrievalRelationKind.Unknown,
            languageRawKind: "csharp",
            container: null,
            scopeIds: scopes);
    }

    /// <summary>造一页 N 条小而稳定的命中（文件各不相同 ⇒ 分散度 ~1.0）。</summary>
    public static RetrievalHit[] Page(int count) =>
        [.. Enumerable.Range(0, count).Select(i => TextHit(i))];

    /// <summary>构造一个**合法的**部分返回（截断 + 落盘 + 游标 + 分布 + 一条收窄建议）。</summary>
    public static RetrievalResult Truncated(
        RetrievalRequest request,
        IReadOnlyList<RetrievalHit> page,
        int totalCount,
        QueryDiagnostics? diagnostics = null) =>
        RetrievalResult.Create(
            request,
            page,
            totalCount,
            IndexVersion,
            nextSteps: [NarrowStep()],
            overflow: Spill(totalCount),
            distribution: RetrievalDistribution.FromHits(page),
            diagnostics: diagnostics,
            nextCursor: "cursor-0001");
}
