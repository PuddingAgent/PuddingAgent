namespace PuddingCodeIndex.Contracts.Retrieval;

/// <summary>特异度（ADR-089 §2.2 C）：查询问得准不准。</summary>
public enum QuerySpecificity
{
    /// <summary>低：命中总量显著超预算且高度分散 / 词法占多数 ⇒ 查询过泛。</summary>
    Low = 0,

    /// <summary>中：量偏大但不分散也不词法主导 ⇒ 可能只是天然多。</summary>
    Medium = 1,

    /// <summary>高：无过载迹象。</summary>
    High = 2,
}

/// <summary>
/// 过载原因（ADR-089 §2.2 B 的判定输入，逐条可测，不许拍脑袋）：
/// `TotalCount / PageSize` 倍率、分散度（hits/files）、`Lexical` 占比、跨顶层目录数、命中层分布。
/// </summary>
public enum OverloadReasonKind
{
    /// <summary>命中总量超过预算倍率阈值。</summary>
    VolumeOverBudget = 0,

    /// <summary>高度分散：几乎每个命中一个文件 ⇒ 词法泛匹配特征。</summary>
    HighDispersion = 1,

    /// <summary>词法证据占多数 ⇒ 没有语义定位。</summary>
    LexicalDominance = 2,

    /// <summary>跨太多顶层目录。</summary>
    CrossDirectorySpread = 3,

    /// <summary>命中层过于分散（声明/文本/注释混杂）。</summary>
    KindScatter = 4,
}

/// <summary>
/// 查询诊断（ADR-089 §2.2 C）：<b>在同一次调用内</b>把"查询偏泛"这件事说清楚。
/// <para>
/// 用户第六轮裁定原文："如果返回的命中超级多，那么也代表这次查询质量不高" ——
/// 但 §2.2 B 明确要求区分"正当的多"（符号明确、关系单一）与"可疑的多"，
/// 否则会对 <c>References</c> 这类天然多命中误报。
/// </para>
/// <para>
/// 不变量：<see cref="QuerySpecificity.Low"/> 必须至少给出一条 <see cref="OverloadReasons"/>
/// （不许"说它差但不说为什么"）；<see cref="QuerySpecificity.High"/> 必须**没有**原因
/// （有原因就不叫高特异度）。
/// </para>
/// </summary>
public sealed record QueryDiagnostics
{
    private QueryDiagnostics(QuerySpecificity specificity, OverloadReasonKind[] reasons)
    {
        Specificity = specificity;
        OverloadReasons = reasons;
    }

    /// <summary>无过载迹象（高特异度）。</summary>
    public static QueryDiagnostics NotOverloaded { get; } = new(QuerySpecificity.High, []);

    /// <summary>特异度。</summary>
    public QuerySpecificity Specificity { get; }

    /// <summary>过载原因（Low 时非空）。</summary>
    public IReadOnlyList<OverloadReasonKind> OverloadReasons { get; }

    /// <summary>是否判为过载（= 特异度 Low）。</summary>
    public bool IsOverloaded => Specificity == QuerySpecificity.Low;

    /// <summary>构造诊断（校验 Low 必须有原因、High 必须无原因）。</summary>
    public static QueryDiagnostics Create(QuerySpecificity specificity, IEnumerable<OverloadReasonKind>? reasons = null)
    {
        var distinct = reasons is null
            ? Array.Empty<OverloadReasonKind>()
            : new SortedSet<OverloadReasonKind>(reasons).ToArray();

        if (specificity == QuerySpecificity.Low && distinct.Length == 0)
            throw new ArgumentException(
                "判为 Low（查询过泛）时必须给出至少一条过载原因 —— 否则 Agent 只知道\"差\"却不知道该收窄什么。",
                nameof(reasons));

        if (specificity == QuerySpecificity.High && distinct.Length > 0)
            throw new ArgumentException(
                "High（高特异度）不得携带过载原因 —— 有原因就不叫高特异度。",
                nameof(reasons));

        return new QueryDiagnostics(specificity, distinct);
    }
}

/// <summary>
/// 过载判定的输入事实（§2.2 B 的五类输入），全部是<b>聚合量</b>而非命中正文：
/// 判定必须可测、可复现，因此输入被定死成这些数字。
/// <para>
/// <b>重要</b>：请用 <b>去重后</b>的命中构造（§8.10：跨 scope 去重优先于过载判定）。
/// </para>
/// </summary>
public sealed record RetrievalOverloadFacts
{
    private RetrievalOverloadFacts(
        int totalCount,
        int pageSize,
        int distinctFileCount,
        int lexicalHitCount,
        int semanticHitCount,
        int distinctTopLevelDirectoryCount,
        int distinctHitKindCount,
        RetrievalIntent intent,
        int distinctRelationKindCount,
        int distinctSymbolCount)
    {
        TotalCount = totalCount;
        PageSize = pageSize;
        DistinctFileCount = distinctFileCount;
        LexicalHitCount = lexicalHitCount;
        SemanticHitCount = semanticHitCount;
        DistinctTopLevelDirectoryCount = distinctTopLevelDirectoryCount;
        DistinctHitKindCount = distinctHitKindCount;
        Intent = intent;
        DistinctRelationKindCount = distinctRelationKindCount;
        DistinctSymbolCount = distinctSymbolCount;
    }

    /// <summary>去重后的真实命中总数。</summary>
    public int TotalCount { get; }

    /// <summary>本次返回的页大小。</summary>
    public int PageSize { get; }

    /// <summary>命中的不同文件数。</summary>
    public int DistinctFileCount { get; }

    /// <summary>词法级命中数。</summary>
    public int LexicalHitCount { get; }

    /// <summary>语义级命中数。</summary>
    public int SemanticHitCount { get; }

    /// <summary>跨多少个顶层目录。</summary>
    public int DistinctTopLevelDirectoryCount { get; }

    /// <summary>出现了多少种命中层。</summary>
    public int DistinctHitKindCount { get; }

    /// <summary>本次查询的意图（决定"正当的多"豁免）。</summary>
    public RetrievalIntent Intent { get; }

    /// <summary>出现了多少种关系类型。</summary>
    public int DistinctRelationKindCount { get; }

    /// <summary>命中了多少个不同符号。</summary>
    public int DistinctSymbolCount { get; }

    /// <summary>参与统计的命中条数（= 词法 + 语义）。</summary>
    public int HitCount => LexicalHitCount + SemanticHitCount;

    /// <summary>倍率：<see cref="TotalCount"/> / <see cref="PageSize"/>。</summary>
    public double OverloadRatio => (double)TotalCount / PageSize;

    /// <summary>分散度：命中 / 文件（≈1.0 表示几乎每个命中一个文件）。</summary>
    public double HitsPerFile => DistinctFileCount == 0 ? 0 : (double)HitCount / DistinctFileCount;

    /// <summary>词法占比。</summary>
    public double LexicalShare => HitCount == 0 ? 0 : (double)LexicalHitCount / HitCount;

    /// <summary>是否"天然命中很多且完全正当"（符号明确、关系单一）。</summary>
    public bool IsLegitimateBulk => RetrievalIntentPolicy.IsLegitimateBulkIntent(Intent)
        && DistinctRelationKindCount <= 1
        && DistinctSymbolCount <= 1;

    /// <summary>是否超过倍率阈值。</summary>
    public bool IsVolumeOverBudget => OverloadRatio >= RetrievalOverloadDiagnostics.VolumeRatioThreshold;

    /// <summary>是否高度分散。</summary>
    public bool IsHighlyDispersed => HitCount > 0
        && HitsPerFile <= RetrievalOverloadDiagnostics.DispersionThreshold;

    /// <summary>是否词法主导。</summary>
    public bool IsLexicalDominated => HitCount > 0
        && LexicalShare >= RetrievalOverloadDiagnostics.LexicalDominanceThreshold;

    /// <summary>直接给数字（引擎或测试都可用）。</summary>
    public static RetrievalOverloadFacts Create(
        int totalCount,
        int pageSize,
        int distinctFileCount,
        int lexicalHitCount,
        int semanticHitCount,
        int distinctTopLevelDirectoryCount,
        int distinctHitKindCount,
        RetrievalIntent intent,
        int distinctRelationKindCount,
        int distinctSymbolCount)
    {
        if (totalCount < 0)
            throw new ArgumentOutOfRangeException(nameof(totalCount), totalCount, "命中总数不得为负。");

        if (pageSize < 1)
            throw new ArgumentOutOfRangeException(nameof(pageSize), pageSize, "页大小必须 ≥ 1。");

        if (lexicalHitCount < 0 || semanticHitCount < 0)
            throw new ArgumentOutOfRangeException(nameof(lexicalHitCount), "命中计数不得为负。");

        if (lexicalHitCount + semanticHitCount > totalCount)
            throw new ArgumentException("参与统计的命中数不得超过真实总数（必须先按符号身份去重）。");

        if (distinctFileCount < 0)
            throw new ArgumentOutOfRangeException(nameof(distinctFileCount), distinctFileCount, "文件数不得为负。");

        if (lexicalHitCount + semanticHitCount == 0 && distinctFileCount != 0)
            throw new ArgumentException("没有命中却报告了文件数 —— 事实自相矛盾。");

        if (distinctFileCount > lexicalHitCount + semanticHitCount)
            throw new ArgumentException("文件数不得超过命中数 —— 事实自相矛盾。");

        return new RetrievalOverloadFacts(
            totalCount,
            pageSize,
            distinctFileCount,
            lexicalHitCount,
            semanticHitCount,
            distinctTopLevelDirectoryCount,
            distinctHitKindCount,
            intent,
            distinctRelationKindCount,
            distinctSymbolCount);
    }

    /// <summary>从（去重后的）命中集合派生事实。</summary>
    public static RetrievalOverloadFacts FromHits(
        int totalCount,
        int pageSize,
        RetrievalIntent intent,
        IReadOnlyList<RetrievalHit> hits)
    {
        ArgumentNullException.ThrowIfNull(hits);

        var files = new SortedSet<string>(StringComparer.Ordinal);
        var directories = new SortedSet<string>(StringComparer.Ordinal);
        var hitKinds = new SortedSet<RetrievalHitKind>();
        var relations = new SortedSet<RetrievalRelationKind>();
        var identities = new SortedSet<string>(StringComparer.Ordinal);
        var lexical = 0;
        var semantic = 0;

        foreach (var hit in hits)
        {
            files.Add(hit.Evidence.NormalizedFilePath);
            directories.Add(RetrievalPathFacts.TopLevelDirectoryOf(hit.Evidence.NormalizedFilePath));
            hitKinds.Add(hit.HitKind);
            relations.Add(hit.Relation);
            identities.Add(hit.Identity.Key);

            if (hit.IsLexical)
                lexical++;
            else if (hit.IsSemantic)
                semantic++;
        }

        return Create(
            totalCount,
            pageSize,
            files.Count,
            lexical,
            semantic,
            directories.Count,
            hitKinds.Count,
            intent,
            relations.Count,
            identities.Count);
    }
}

/// <summary>
/// 过载诊断（ADR-089 §2.2 B / §8.8）：把"过载即信号"固化成**可测规则**，而不是拍脑袋。
/// <para>
/// 规则（成文，可逐条取红）：<br/>
/// ① 正当的多（<see cref="RetrievalOverloadFacts.IsLegitimateBulk"/>：<c>References/Callers/Callees/Impact</c>
///    + 关系单一 + 符号明确）⇒ <see cref="QuerySpecificity.High"/>，<b>无论总量多大都不得报 Low</b>；<br/>
/// ② 未超倍率阈值 ⇒ High；<br/>
/// ③ 超阈值且（高度分散 或 词法主导）⇒ <see cref="QuerySpecificity.Low"/> + 原因集合；<br/>
/// ④ 超阈值但不分散也不词法主导 ⇒ Medium（"可能只是天然多"）。
/// </para>
/// <para>
/// ⚠️ 本类只做聚合量判定，<b>不检索、不排序、不落盘</b>；真实引擎（U4-2c）负责提供事实。
/// </para>
/// </summary>
public static class RetrievalOverloadDiagnostics
{
    /// <summary>倍率阈值：命中总数 ≥ 20 × 页大小 视为量偏大。</summary>
    public const int VolumeRatioThreshold = 20;

    /// <summary>分散度阈值：命中/文件 ≤ 1.25 视为"几乎每个命中一个文件"。</summary>
    public const double DispersionThreshold = 1.25;

    /// <summary>词法占比阈值：≥ 0.5 视为词法主导。</summary>
    public const double LexicalDominanceThreshold = 0.5;

    /// <summary>跨顶层目录数阈值。</summary>
    public const int CrossDirectoryThreshold = 3;

    /// <summary>命中层种类数阈值。</summary>
    public const int KindScatterThreshold = 3;

    /// <summary>按成文规则给出诊断。</summary>
    public static QueryDiagnostics Evaluate(RetrievalOverloadFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        if (facts.IsLegitimateBulk || !facts.IsVolumeOverBudget)
            return QueryDiagnostics.NotOverloaded;

        var reasons = new List<OverloadReasonKind> { OverloadReasonKind.VolumeOverBudget };
        if (facts.IsHighlyDispersed)
            reasons.Add(OverloadReasonKind.HighDispersion);

        if (facts.IsLexicalDominated)
            reasons.Add(OverloadReasonKind.LexicalDominance);

        if (facts.DistinctTopLevelDirectoryCount >= CrossDirectoryThreshold)
            reasons.Add(OverloadReasonKind.CrossDirectorySpread);

        if (facts.DistinctHitKindCount >= KindScatterThreshold)
            reasons.Add(OverloadReasonKind.KindScatter);

        var overloaded = facts.IsHighlyDispersed || facts.IsLexicalDominated;
        return QueryDiagnostics.Create(
            overloaded ? QuerySpecificity.Low : QuerySpecificity.Medium,
            reasons);
    }
}
