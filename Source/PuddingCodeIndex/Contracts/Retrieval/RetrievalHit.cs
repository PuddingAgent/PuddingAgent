using PuddingCodeIndex.Contracts;

namespace PuddingCodeIndex.Contracts.Retrieval;

/// <summary>
/// 一条命中（ADR-089 §2.1 第 2 条 / §5.3）：<b>每个命中都自描述</b> ——
/// 命中层 + 符号种类 + 语言原始种类 + 关系 + 证据 + 置信度 + 分数 + <b>人类可读的排序原因</b>。
/// <para>
/// "每个命中自带 why"是"不让 Agent 打转"的关键：Agent 不必靠"再查一次"来验证排序是否可信。
/// 因此 <c>why</c> 是**必填**（空 reasons 会被构造期拒绝），分数必须是有限值
/// （NaN 会让比较器退化，直接破坏 §2.1 第 5 条的确定性）。
/// </para>
/// <para>
/// <see cref="ScopeIds"/> 承载"同一符号在多个 scope 下各命中一次"这一实测事实（§2.4）：
/// 去重后多 scope 体现为**佐证元数据**（<see cref="ScopeCorroborationWeight"/>），而不是重复项。
/// </para>
/// </summary>
public sealed record RetrievalHit
{
    /// <summary>构造一条命中。所有"决定性"字段必填，避免空洞命中。</summary>
    public RetrievalHit(
        SymbolIdentity identity,
        string symbolName,
        CodeSymbolKind symbolKind,
        RetrievalHitKind hitKind,
        RetrievalEvidence evidence,
        RetrievalConfidence confidence,
        double score,
        string why,
        RetrievalMatchTarget matchTargets,
        RetrievalRelationKind relation = RetrievalRelationKind.Unknown,
        string? languageRawKind = null,
        string? container = null,
        IEnumerable<string>? scopeIds = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(evidence);

        if (string.IsNullOrWhiteSpace(symbolName))
            throw new ArgumentException("命中必须有符号名（文本命中给出被匹配的片段）。", nameof(symbolName));

        if (string.IsNullOrWhiteSpace(why))
            throw new ArgumentException("命中必须给出可解释的排序原因（why）—— 空原因会被 Agent 当成\"排序神秘\"。", nameof(why));

        if (!double.IsFinite(score))
            throw new ArgumentOutOfRangeException(nameof(score), score, "分数必须是有限值（NaN/Infinity 会破坏确定性排序）。");

        if (matchTargets == RetrievalMatchTarget.None)
            throw new ArgumentException("命中的匹配域不得为 None（没匹配任何域就不该是命中）。", nameof(matchTargets));

        Scopes = ScopeIdSet.Of(scopeIds);
        Identity = identity;
        SymbolName = symbolName.Trim();
        SymbolKind = symbolKind;
        HitKind = hitKind;
        Evidence = evidence;
        Confidence = confidence;
        Score = score;
        Why = why.Trim();
        MatchTargets = matchTargets;
        Relation = relation;
        LanguageRawKind = string.IsNullOrWhiteSpace(languageRawKind) ? null : languageRawKind;
        Container = string.IsNullOrWhiteSpace(container) ? null : container;
    }

    /// <summary>跨 scope 去重键（规范化 symbol_id + 文件 + 行）。</summary>
    public SymbolIdentity Identity { get; }

    /// <summary>符号名（文本命中时为被匹配的片段）。</summary>
    public string SymbolName { get; }

    /// <summary>通用符号种类（复用既有 <c>CodeSymbolKind</c>）。</summary>
    public CodeSymbolKind SymbolKind { get; }

    /// <summary>命中层（声明 / 实现 / 引用 / 调用 / 注释 / 正文）。</summary>
    public RetrievalHitKind HitKind { get; }

    /// <summary>证据位置（文件 + 行）。</summary>
    public RetrievalEvidence Evidence { get; }

    /// <summary>可信度（语义 / 词法 / 未知）。</summary>
    public RetrievalConfidence Confidence { get; }

    /// <summary>排序分数（引擎给出；层序与 tie-break 由比较器负责）。</summary>
    public double Score { get; }

    /// <summary>可解释的排序原因。</summary>
    public string Why { get; }

    /// <summary>查询在哪些面上匹配到了它（§2.3 匹配域；用于"只关注类名称"的可裁决性）。</summary>
    public RetrievalMatchTarget MatchTargets { get; }

    /// <summary>与查询符号的关系（无关系 = <see cref="RetrievalRelationKind.Unknown"/>）。</summary>
    public RetrievalRelationKind Relation { get; }

    /// <summary>语言原始种类字符串（设计 §4：语言专有 kind 不灌进通用枚举）。</summary>
    public string? LanguageRawKind { get; }

    /// <summary>归属类型（容器）。</summary>
    public string? Container { get; }

    /// <summary>命中来源 scope（已去重、确定性排序）。空 = 未盖章。</summary>
    public ScopeIdSet Scopes { get; }

    /// <summary>命中来源 scope 标识（<see cref="Scopes"/> 的只读视图）。</summary>
    public IReadOnlyList<string> ScopeIds => Scopes.Ids;

    /// <summary>是否在符号名面上命中（用户说的"只关注类名称"）。</summary>
    public bool IsSymbolNameMatch => (MatchTargets & RetrievalMatchTarget.SymbolName) != RetrievalMatchTarget.None;

    /// <summary>语言标识（由证据路径推导）。</summary>
    public string Language => RetrievalPathFacts.LanguageOf(Evidence.NormalizedFilePath);

    /// <summary>
    /// 多 scope 佐证加权（ADR-089 §2.4）：1 个 scope = 1.0，每多一个 +0.25，上限 2.0。
    /// 确定性、可解释；用于把"结构性重复"变成"独立佐证"而不是重复项。
    /// </summary>
    public double ScopeCorroborationWeight => Math.Min(2.0, 1.0 + (0.25 * (Math.Max(1, Scopes.Count) - 1)));

    /// <summary>排序首键：分数 × 佐证权重。</summary>
    public double EffectiveRank => Score * ScopeCorroborationWeight;

    /// <summary>估算字节数（见 <see cref="RetrievalBudget"/> 的估算口径）。</summary>
    public int EstimatedBytes => RetrievalBudget.EstimatePayloadBytes(
        Evidence.FilePath,
        SymbolName,
        Why,
        Evidence.Snippet,
        LanguageRawKind,
        Container);

    /// <summary>估算 token 数（token 预算是优先预算）。</summary>
    public int EstimatedTokens => RetrievalBudget.EstimateTokens(EstimatedBytes);

    /// <summary>是否词法级证据。</summary>
    public bool IsLexical => Confidence == RetrievalConfidence.Lexical;

    /// <summary>是否语义级证据。</summary>
    public bool IsSemantic => Confidence == RetrievalConfidence.Semantic;

    /// <summary>换一组 scope 盖章（去重合并时用；其余字段逐字保留）。</summary>
    public RetrievalHit WithScopes(IEnumerable<string> scopeIds) => new(
        Identity,
        SymbolName,
        SymbolKind,
        HitKind,
        Evidence,
        Confidence,
        Score,
        Why,
        MatchTargets,
        Relation,
        LanguageRawKind,
        Container,
        scopeIds);
}
