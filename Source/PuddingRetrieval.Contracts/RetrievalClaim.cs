namespace PuddingRetrieval.Contracts;

/// <summary>
/// 空结果的**否定类型**（ADR-089 §8.x）：把"我没查到"与"世上没有"分开。
/// <para>
/// 旧接口只有 <see cref="RetrievalEmptyReason"/>（为什么是 0），没有声明"我查了哪些面"。
/// 于是"词面没命中、语义面根本没接线"这种情形，机器无法与"全查过确实没有"区分 ——
/// 只能靠提示句解释，而提示句不是可判定的字段。
/// </para>
/// </summary>
public enum RetrievalClaimKind
{
    /// <summary>执行/覆盖不足以形成否定（索引不可用、能力不足、过滤性空、或 auto 零结果）。</summary>
    None = 0,

    /// <summary>未找到证据 —— <b>不等于</b>没有实现。</summary>
    NoCandidates = 1,

    /// <summary>在已声明的 scope/规则内穷尽且无匹配（限定范围结论，不是全局否定）。</summary>
    NoMatchInScope = 2,
}

/// <summary>
/// 空结果的机器可判定声明：否定类型 + 已查/未查面 + 形成确定性结论所需的 provider。
/// <para>
/// 三条 fail-closed 不变量（构造期检查，无法表示自相矛盾的声明）：
/// ① 已查面与未查面不得有交集（同一面不能既查过又没查）；
/// ② <see cref="RetrievalClaimKind.NoMatchInScope"/> 要求未查面为空 —— 还有面没查，
///    就不能说"该范围内穷尽"；
/// ③ 所需 provider 必须包含所有已查面（否则"执行过某面"与"判定需要它"脱节）。
/// </para>
/// </summary>
public sealed record RetrievalClaim
{
    /// <summary>词面面（符号名 / 声明文本）。当前端口恒执行。</summary>
    public const string SurfaceLexical = "lexical";

    /// <summary>结构关系面（调用/引用/继承等边）。仅在锚点关系查询时执行。</summary>
    public const string SurfaceRelations = "relations";

    /// <summary>语义（向量）面。当前<b>未接入</b>：<c>IEmbeddingProvider</c> 在 Runtime/CodeIndex 零引用。</summary>
    public const string SurfaceVector = "vector";

    private readonly string[] _searched;
    private readonly string[] _unsearched;
    private readonly string[] _required;

    private RetrievalClaim(
        RetrievalClaimKind kind,
        IReadOnlyList<string> searchedSurfaces,
        IReadOnlyList<string> unsearchedSurfaces,
        IReadOnlyList<string> requiredProviders)
    {
        _searched = Normalize(searchedSurfaces, nameof(searchedSurfaces));
        _unsearched = Normalize(unsearchedSurfaces, nameof(unsearchedSurfaces), allowEmpty: true);
        _required = Normalize(requiredProviders, nameof(requiredProviders));

        if (_searched.Length == 0)
        {
            throw new RetrievalContractViolationException(
                "已查面不得为空 —— 一个面都没查过，那不是空结果，是没执行。");
        }

        foreach (var surface in _searched)
        {
            if (Array.IndexOf(_unsearched, surface) >= 0)
            {
                throw new RetrievalContractViolationException(
                    $"面 '{surface}' 不能同时出现在已查与未查里 —— 空结果声明必须自洽。");
            }
        }

        if (kind == RetrievalClaimKind.NoMatchInScope && _unsearched.Length > 0)
        {
            throw new RetrievalContractViolationException(
                "还有未查面（" + string.Join(" / ", _unsearched) + "）时不得声明 NoMatchInScope —— "
                + "限定范围结论的前提是该范围内的面已穷尽。");
        }

        foreach (var surface in _searched)
        {
            if (Array.IndexOf(_required, surface) < 0)
            {
                throw new RetrievalContractViolationException(
                    $"已查面 '{surface}' 必须出现在所需 provider 里 —— 否则\"查过\"与\"判定依赖它\"脱节。");
            }
        }

        Kind = kind;
    }

    /// <summary>否定类型。</summary>
    public RetrievalClaimKind Kind { get; }

    /// <summary>本端口实际执行过的面。</summary>
    public IReadOnlyList<string> SearchedSurfaces => _searched;

    /// <summary>已知存在但**未**执行的面 —— 它们的存在就是"不能下否定"的理由。</summary>
    public IReadOnlyList<string> UnsearchedSurfaces => _unsearched;

    /// <summary>形成确定性结论所需的 provider（auto 面恒含 vector）。</summary>
    public IReadOnlyList<string> RequiredProviders => _required;

    /// <summary>
    /// 按 (请求, 空原因) 推导声明 —— 唯一实现，不得在调用方各写一份。
    /// <para>
    /// 规则（ADR-089 §8.x）：<br/>
    /// ① 非 <see cref="RetrievalEmptyReason.GenuinelyAbsent"/> ⇒ <see cref="RetrievalClaimKind.None"/>
    ///    （索引不可用 / 能力不足 / 过滤性空，全都不是"没有"）；<br/>
    /// ② <c>GenuinelyAbsent</c> 且请求是 auto ⇒ <see cref="RetrievalClaimKind.None"/>
    ///    （<b>所有 auto 零结果都禁止否定</b>）；<br/>
    /// ③ <c>GenuinelyAbsent</c> + 锚点关系面 ⇒ <see cref="RetrievalClaimKind.NoMatchInScope"/>
    ///    —— 这是"精确零匹配"的正对照：关系边来自索引，不依赖向量，可以说限定范围结论；<br/>
    /// ④ 其余（显式非 auto、无锚点）⇒ <see cref="RetrievalClaimKind.NoCandidates"/>。
    /// </para>
    /// </summary>
    public static RetrievalClaim ClassifyForEmpty(RetrievalRequest request, RetrievalEmptyReason? reason)
    {
        ArgumentNullException.ThrowIfNull(request);

        var anchoredRelations = request.HasAnchor;

        // 关系面：边来自索引，不需要向量，因此未查面为空 —— 这是能下限定范围结论的前提。
        var searched = anchoredRelations
            ? [SurfaceLexical, SurfaceRelations]
            : new[] { SurfaceLexical };

        var required = anchoredRelations
            ? new[] { SurfaceLexical, SurfaceRelations }
            : new[] { SurfaceLexical, SurfaceVector };

        var unsearched = anchoredRelations ? Array.Empty<string>() : new[] { SurfaceVector };

        var kind = reason switch
        {
            RetrievalEmptyReason.GenuinelyAbsent when anchoredRelations => RetrievalClaimKind.NoMatchInScope,
            RetrievalEmptyReason.GenuinelyAbsent when request.IsAuto => RetrievalClaimKind.None,
            RetrievalEmptyReason.GenuinelyAbsent => RetrievalClaimKind.NoCandidates,
            _ => RetrievalClaimKind.None,
        };

        return new RetrievalClaim(kind, searched, unsearched, required);
    }

    private static string[] Normalize(
        IReadOnlyList<string>? values,
        string parameterName,
        bool allowEmpty = false)
    {
        if (values is null || values.Count == 0)
        {
            return allowEmpty
                ? []
                : throw new RetrievalContractViolationException($"'{parameterName}' 不得为空。");
        }

        var set = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var raw in values)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                throw new RetrievalContractViolationException($"'{parameterName}' 的条目不得为空。");
            }

            set.Add(raw.Trim());
        }

        return [.. set];
    }
}
