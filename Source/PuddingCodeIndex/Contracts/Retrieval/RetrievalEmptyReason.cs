namespace PuddingCodeIndex.Contracts.Retrieval;

/// <summary>
/// 空结果的**原因**（ADR-089 §2.1 第 3 条 + §8.9）：这是"空洞否定"的对立面。
/// <para>
/// 用户第五轮裁定的动机（"避免让 Agent 打转"）在这里落成硬合同：引擎返回 0 条时，
/// <b>必须</b>能区分"真的没有"和"我问错了/我没能力问"。没有原因的 0 条
/// 在结构上不可表示（见 <see cref="RetrievalResult"/> 的构造校验）。
/// </para>
/// </summary>
public enum RetrievalEmptyReason
{
    /// <summary>该 scope 尚未建立索引。</summary>
    NotIndexed = 0,

    /// <summary>索引已损坏（需重建）。</summary>
    IndexCorrupted = 1,

    /// <summary>该语言不支持此能力（必须附替代方案，§5.2）。</summary>
    LanguageUnsupported = 2,

    /// <summary>该语言不支持此意图（必须附替代方案）。</summary>
    IntentUnsupported = 3,

    /// <summary>
    /// 过滤性空（§8.9）：因过滤条件导致 0 结果 —— <b>不是</b>真空，
    /// 必须说明"放宽哪个面会得到结果"。
    /// </summary>
    FilteredOut = 4,

    /// <summary>过滤面干净、索引可用、能力支持，且确实不存在。</summary>
    GenuinelyAbsent = 5,
}

/// <summary>索引可用性（<see cref="RetrievalEmptyReasonPolicy"/> 的输入事实之一）。</summary>
public enum RetrievalIndexAvailability
{
    /// <summary>可用。</summary>
    Available = 0,

    /// <summary>未建立。</summary>
    NotIndexed = 1,

    /// <summary>已损坏。</summary>
    Corrupted = 2,
}

/// <summary>
/// 空结果原因的裁决表（ADR-089 §2.1 第 3 条 + §8.9），成文且可逐条取红。
/// <para>
/// 优先级（高 → 低）：<br/>
/// ① 索引不可用（未建立 / 损坏）—— 这是关于世界的事实，此时"过滤"还没参与过判断；<br/>
/// ② 语言能力不足（<see cref="RetrievalCapabilityLevel.None"/>）；<br/>
/// ③ 意图不被支持；<br/>
/// ④ <b>有过滤面且 0 命中 ⇒ <see cref="RetrievalEmptyReason.FilteredOut"/></b>（§8.9 硬约束）；<br/>
/// ⑤ 否则 ⇒ <see cref="RetrievalEmptyReason.GenuinelyAbsent"/>。
/// </para>
/// <para>
/// ④ 是这张表的核心：把它报成 ⑤ 会让 Agent 以为"真的没有"，然后换查询 ⇒ 打转。
/// </para>
/// </summary>
public static class RetrievalEmptyReasonPolicy
{
    /// <summary>按成文优先级裁决 0 命中时的原因。</summary>
    public static RetrievalEmptyReason ClassifyForZeroHits(
        RetrievalFilter? filter,
        RetrievalIndexAvailability indexAvailability = RetrievalIndexAvailability.Available,
        RetrievalCapabilityLevel capabilityLevel = RetrievalCapabilityLevel.Semantic,
        bool intentSupported = true)
    {
        switch (indexAvailability)
        {
            case RetrievalIndexAvailability.NotIndexed:
                return RetrievalEmptyReason.NotIndexed;

            case RetrievalIndexAvailability.Corrupted:
                return RetrievalEmptyReason.IndexCorrupted;

            default:
                break;
        }

        if (capabilityLevel == RetrievalCapabilityLevel.None)
            return RetrievalEmptyReason.LanguageUnsupported;

        if (!intentSupported)
            return RetrievalEmptyReason.IntentUnsupported;

        if (filter?.HasAnyConstraint == true)
            return RetrievalEmptyReason.FilteredOut;

        return RetrievalEmptyReason.GenuinelyAbsent;
    }

    /// <summary>
    /// 过滤性空时"该放宽哪个面"：取生效面中**优选顺序最靠前**的那一个
    /// （匹配域 → 符号种类 → 命中层 → 关系 → 置信度 → 扩展名 → 目录）。
    /// <para>没有生效面 ⇒ 抛异常（不存在可放宽的面，不许凭空编一个）。</para>
    /// </summary>
    public static RetrievalFacet SuggestRelaxationFacet(RetrievalFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var facets = filter.ConstrainedFacets();
        return facets.Count == 0
            ? throw new RetrievalContractViolationException(
                "过滤器没有任何生效面，因此不存在可放宽的面 —— 此时 0 命中不可能是过滤性空（ADR-089 §8.9）。")
            : facets[0];
    }
}
