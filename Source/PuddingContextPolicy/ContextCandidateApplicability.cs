namespace PuddingContextPolicy;

/// <summary>候选适用性判定的结论。</summary>
public enum CandidateApplicabilityVerdict
{
    /// <summary>源版本/跨度都未变：可以在提交临界区内继续校验与提交。</summary>
    Applicable,

    /// <summary>相关版本/代次已变化：丢弃候选，**不得**拼接新尾部或自动 rebase。</summary>
    StaleCandidate,

    /// <summary>保留后缀（中间保留段 + 受保护尾部）不再是当前历史的结尾：追加、插入或改写都会命中。</summary>
    RetainedContextChanged,

    /// <summary>被替换跨度在当前历史里已不完整（新增、删除或重排）：丢弃候选。</summary>
    SpanNotIntact,
}

/// <summary>冻结候选的身份描述（生成候选时固化，提交前用于严格比对）。</summary>
/// <param name="RetainedIdentities">
/// 冻结时**保留后缀**的完整身份序列（中间保留段 + 受保护尾部）。提交前要求它与当前历史
/// 逐位一致 —— 这样「尾部之前插入/删除消息」也会让旧候选失效，而不只是「尾部被追加」。
/// </param>
public sealed record FrozenCandidateDescriptor(
    string Fingerprint,
    long SourceGeneration,
    string SourceRevision,
    string RouteVersion,
    string ToolSpecVersion,
    string PolicyVersion,
    IReadOnlyList<string> RemovedIdentities,
    IReadOnlyList<string> RetainedIdentities);

/// <summary>提交时刻的当前状态描述。</summary>
public sealed record CurrentContextDescriptor(
    long Generation,
    string Revision,
    string RouteVersion,
    string ToolSpecVersion,
    string PolicyVersion,
    IReadOnlyList<string> MessageIdentities);

/// <summary>适用性判定结果。</summary>
public sealed record CandidateApplicabilityResult(
    CandidateApplicabilityVerdict Verdict,
    string Reason)
{
    /// <summary>是否允许进入提交临界区（**不等于**提交成功：还要过收益与事务）。</summary>
    public bool IsApplicable => Verdict == CandidateApplicabilityVerdict.Applicable;
}

/// <summary>
/// 严格适用校验（纯函数）。ADR-095 D3：初版采用**严格校验** —— 生成后任何相关历史/配置变化
/// 都丢弃候选并记录 <c>stale_candidate</c>，不做复杂尾部拼接或自动 rebase。
/// <para>
/// 判定顺序：代次/revision/路由/工具/策略版本 → 受保护尾部完整性 → 被替换跨度仍是**当前历史的前缀**。
/// 这样旧摘要永远不可能覆盖新消息。
/// </para>
/// </summary>
public static class ContextCandidateApplicability
{
    public static CandidateApplicabilityResult Validate(
        FrozenCandidateDescriptor frozen,
        CurrentContextDescriptor current)
    {
        ArgumentNullException.ThrowIfNull(frozen);
        ArgumentNullException.ThrowIfNull(current);

        if (frozen.SourceGeneration != current.Generation)
            return Stale("generation_changed");
        if (!string.Equals(frozen.SourceRevision, current.Revision, StringComparison.Ordinal))
            return Stale("history_revision_changed");
        if (!string.Equals(frozen.RouteVersion, current.RouteVersion, StringComparison.Ordinal))
            return Stale("route_changed");
        if (!string.Equals(frozen.ToolSpecVersion, current.ToolSpecVersion, StringComparison.Ordinal))
            return Stale("tool_spec_changed");
        if (!string.Equals(frozen.PolicyVersion, current.PolicyVersion, StringComparison.Ordinal))
            return Stale("policy_changed");

        // 被替换跨度必须仍作为当前历史里**连续、按序**的一段存在（未插入、未删除、未重排）。
        var spanStart = FindRun(frozen.RemovedIdentities, current.MessageIdentities);
        if (spanStart < 0)
            return new CandidateApplicabilityResult(
                CandidateApplicabilityVerdict.SpanNotIntact,
                "selected_span_not_intact");

        // 跨度之后的内容必须与冻结时的**保留后缀**逐位一致：一旦有新消息追加（用户抢占/新 Turn）、
        // 中间被插入或改写，旧候选立即失效 ⇒ 旧摘要不可能覆盖新消息。
        var afterSpan = current.MessageIdentities
            .Skip(spanStart + frozen.RemovedIdentities.Count)
            .ToArray();
        if (!SequenceEquals(afterSpan, frozen.RetainedIdentities))
            return new CandidateApplicabilityResult(
                CandidateApplicabilityVerdict.RetainedContextChanged,
                "retained_context_changed");

        return new CandidateApplicabilityResult(CandidateApplicabilityVerdict.Applicable, "applicable");
    }

    private static CandidateApplicabilityResult Stale(string reason) =>
        new(CandidateApplicabilityVerdict.StaleCandidate, reason);

    /// <summary>返回跨度在当前历史中连续出现的起始下标；不存在返回 -1。</summary>
    private static int FindRun(
        IReadOnlyList<string> span,
        IReadOnlyList<string> current)
    {
        if (span.Count == 0)
            return -1;

        for (var start = 0; start + span.Count <= current.Count; start++)
        {
            var matches = true;
            for (var offset = 0; offset < span.Count; offset++)
            {
                if (!string.Equals(span[offset], current[start + offset], StringComparison.Ordinal))
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
                return start;
        }

        return -1;
    }

    /// <summary>逐位相等（元素与顺序都必须一致）。</summary>
    private static bool SequenceEquals(
        IReadOnlyList<string> actual,
        IReadOnlyList<string> expected)
    {
        if (actual.Count != expected.Count)
            return false;

        for (var i = 0; i < expected.Count; i++)
        {
            if (!string.Equals(actual[i], expected[i], StringComparison.Ordinal))
                return false;
        }

        return true;
    }
}
