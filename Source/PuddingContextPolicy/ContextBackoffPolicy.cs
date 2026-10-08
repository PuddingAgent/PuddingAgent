namespace PuddingContextPolicy;

/// <summary>候选上一次维护尝试的终态记录（只保留去重需要的字段）。</summary>
public sealed record CandidateMaintenanceRecord(
    string Fingerprint,
    long Generation,
    string PolicyVersion,
    string Outcome,
    DateTimeOffset ObservedAtUtc);

/// <summary>退避判定的结果。</summary>
public sealed record BackoffDecision(bool ShouldAttempt, string Reason);

/// <summary>维护终态取值（与生命周期事件的 outcome 一致）。</summary>
public static class CandidateMaintenanceOutcomes
{
    public const string Applied = "applied";
    public const string NoGain = "no_gain";
    public const string Failed = "failed";
    public const string StaleCandidate = "stale_candidate";
    public const string DeferredSoft = "deferred_soft";
}

/// <summary>
/// 退避/去重判定（纯函数）。
/// <para>
/// 硬不变量：**退避绝不能屏蔽硬预算保护**（ADR-095 D1/D2）。因此
/// <c>hardProtectionRequired = true</c> 时一律返回可尝试，理由是
/// <c>hard_protection_overrides_backoff</c>；同理，同候选的“已应用”记录若出现在**同一代次**上，
/// 说明代次没有被推进（成功应用才推进 generation），此时也不得据此静默跳过。
/// </para>
/// </summary>
public static class ContextBackoffPolicy
{
    /// <summary>
    /// 判定是否应当为当前候选发起维护。
    /// </summary>
    /// <param name="lastRecord">该会话上一次维护终态；null = 无记录。</param>
    /// <param name="fingerprint">当前候选指纹。</param>
    /// <param name="generation">当前上下文代次。</param>
    /// <param name="policyVersion">当前策略版本。</param>
    /// <param name="now">当前时刻（注入，便于测试）。</param>
    /// <param name="minRetryInterval">同候选无收益/失败后的最小重试间隔。</param>
    /// <param name="hardProtectionRequired">当前请求是否已越过硬输入边界。</param>
    public static BackoffDecision Decide(
        CandidateMaintenanceRecord? lastRecord,
        string fingerprint,
        long generation,
        string policyVersion,
        DateTimeOffset now,
        TimeSpan minRetryInterval,
        bool hardProtectionRequired)
    {
        // 硬保护优先于任何退避：不得用冷却期跳过硬预算保护。
        if (hardProtectionRequired)
            return new BackoffDecision(true, "hard_protection_overrides_backoff");

        if (lastRecord is null)
            return new BackoffDecision(true, "no_previous_record");

        var samePolicy = string.Equals(lastRecord.PolicyVersion, policyVersion, StringComparison.Ordinal);
        var sameCandidate = samePolicy
            && string.Equals(lastRecord.Fingerprint, fingerprint, StringComparison.Ordinal)
            && lastRecord.Generation == generation;
        if (!sameCandidate)
            return new BackoffDecision(true, "candidate_changed");

        // 同候选、同代次上的“已应用”说明代次没被推进（成功应用应推进 generation）⇒ 记录不可信，不跳过。
        if (string.Equals(lastRecord.Outcome, CandidateMaintenanceOutcomes.Applied, StringComparison.Ordinal))
            return new BackoffDecision(true, "applied_record_without_generation_advance");

        // 未真正产生历史写入的终态才构成退避依据。
        var isSuppressible = string.Equals(lastRecord.Outcome, CandidateMaintenanceOutcomes.NoGain, StringComparison.Ordinal)
            || string.Equals(lastRecord.Outcome, CandidateMaintenanceOutcomes.Failed, StringComparison.Ordinal);
        if (!isSuppressible)
            return new BackoffDecision(true, "outcome_not_suppressible");

        var elapsed = now - lastRecord.ObservedAtUtc;
        if (elapsed < TimeSpan.Zero || elapsed >= minRetryInterval)
            return new BackoffDecision(true, "backoff_expired");

        return new BackoffDecision(false, "same_candidate_backoff");
    }
}
