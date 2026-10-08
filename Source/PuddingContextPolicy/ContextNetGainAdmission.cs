namespace PuddingContextPolicy;

/// <summary>净收益准入的结论。</summary>
public enum NetGainVerdict
{
    /// <summary>真实收缩、满足硬上限且通过收益准入：允许进入事务提交。</summary>
    Accepted,

    /// <summary>摘要为空/被拒绝/不缩小：无收益，历史保持原样。</summary>
    NoGain,

    /// <summary>加入摘要后整请求仍越过硬上限：**不得提交**（提交只会把越界写进历史）。</summary>
    ExceedsHardLimit,
}

/// <summary>
/// 净收益计算的输入。**必须带真实摘要长度**：计划阶段的
/// <see cref="CandidateSelectionResult.ProjectedUsedTokensExcludingSummary"/> 不能当提交后的值。
/// </summary>
/// <param name="InitialUsedTokens">本次请求压缩前的整请求估算。</param>
/// <param name="RemovedTokens">被替换跨度的 token 估算。</param>
/// <param name="SummaryTokens">**实际**摘要 token（Provider 返回后计量）。</param>
/// <param name="EffectiveInputTokens">有效输入上限（与出站硬门禁同源）。</param>
/// <param name="TargetRatio">压实目标比例。</param>
public sealed record NetGainInputs(
    int InitialUsedTokens,
    int RemovedTokens,
    int SummaryTokens,
    int EffectiveInputTokens,
    double TargetRatio = 0.50);

/// <summary>
/// 净收益判定结果。
/// </summary>
/// <param name="ProjectedAfterUsedTokens">
/// 加入**实际**摘要后重算的整请求估算。只有本字段才是「提交后的值」；
/// 计划估算（不含摘要）不得进入日志或事件充当 after。
/// </param>
/// <param name="MeetsTarget">是否达到压实目标比例。未达目标不拒绝提交，但必须如实记录。</param>
public sealed record NetGainDecision(
    NetGainVerdict Verdict,
    bool MeetsTarget,
    int ReclaimTokens,
    int ProjectedAfterUsedTokens,
    string Reason)
{
    /// <summary>是否允许提交。</summary>
    public bool CanCommit => Verdict == NetGainVerdict.Accepted;
}

/// <summary>
/// 收益准入（纯函数）。ADR-095 D3 / ADR-090：摘要加入后必须**重新完整计量**，
/// 验证真实收缩、满足硬上限与配置的回收准入；计划阶段未含摘要的估算不能当最终收益。
/// </summary>
public static class ContextNetGainAdmission
{
    public static NetGainDecision Evaluate(NetGainInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        var initial = Math.Max(0, inputs.InitialUsedTokens);
        var removed = Math.Max(0, inputs.RemovedTokens);
        var summary = Math.Max(0, inputs.SummaryTokens);
        var limit = Math.Max(1, inputs.EffectiveInputTokens);

        if (summary <= 0)
            return Reject(NetGainVerdict.NoGain, initial, 0, "summary_empty");
        if (summary >= removed)
            return Reject(NetGainVerdict.NoGain, initial, removed - summary, "summary_not_smaller_than_span");

        // 真实 after：必须用实际摘要长度重算，而不是计划的 retained 估算。
        var after = Math.Max(0, initial - removed + summary);
        var reclaim = removed - summary;
        if (after > limit)
        {
            return new NetGainDecision(
                NetGainVerdict.ExceedsHardLimit,
                MeetsTarget: false,
                ReclaimTokens: reclaim,
                ProjectedAfterUsedTokens: after,
                Reason: "after_summary_exceeds_hard_limit");
        }

        var ratio = inputs.TargetRatio is > 0 and <= 1 ? inputs.TargetRatio : 0.50;
        var meetsTarget = after <= (int)Math.Floor(limit * ratio);
        return new NetGainDecision(
            NetGainVerdict.Accepted,
            meetsTarget,
            reclaim,
            after,
            meetsTarget ? "accepted_meets_target" : "accepted_below_target");
    }

    private static NetGainDecision Reject(
        NetGainVerdict verdict,
        int initial,
        int reclaim,
        string reason) =>
        new(verdict, MeetsTarget: false, ReclaimTokens: reclaim,
            ProjectedAfterUsedTokens: initial, Reason: reason);
}
