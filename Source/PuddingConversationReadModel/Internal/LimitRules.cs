namespace PuddingConversationReadModel;

/// <summary>
/// <see cref="ConversationReadModelLimits"/> 的归一化与一致性校验规则（只有一个配置来源，Plan §3.3）。
/// </summary>
internal static class LimitRules
{
    internal static int NormalizeOpenMessageLimit(ConversationReadModelLimits limits, int? requested)
    {
        var maximum = Math.Max(1, limits.MaxOpenMessageLimit);
        var value = requested ?? limits.OpenMessageLimit;
        return Math.Clamp(value, 1, maximum);
    }

    internal static int NormalizeDetailItemLimit(ConversationReadModelLimits limits, int? requested)
    {
        var maximum = Math.Max(1, limits.MaxDetailPageItems);
        var value = requested ?? limits.DetailPageItemLimit;
        return Math.Clamp(value, 1, maximum);
    }

    internal static IReadOnlyList<string> Validate(ConversationReadModelLimits limits)
    {
        var violations = new List<string>();

        if (limits.MaxOpenMessageLimit <= 0)
        {
            violations.Add($"MaxOpenMessageLimit must be positive (was {limits.MaxOpenMessageLimit}).");
        }
        else if (limits.OpenMessageLimit <= 0 || limits.OpenMessageLimit > limits.MaxOpenMessageLimit)
        {
            violations.Add(
                $"OpenMessageLimit must be within [1, MaxOpenMessageLimit={limits.MaxOpenMessageLimit}] (was {limits.OpenMessageLimit}).");
        }

        RequirePositive(violations, limits.OpenPageMaxBytes, "OpenPageMaxBytes");
        RequirePositive(violations, limits.SingleMessagePreviewBytes, "SingleMessagePreviewBytes");
        RequirePositive(violations, limits.ActivePreviewMaxBytes, "ActivePreviewMaxBytes");
        RequirePositive(violations, limits.GiantEventPreviewBytes, "GiantEventPreviewBytes");

        if (limits.MaxDetailPageItems <= 0)
        {
            violations.Add($"MaxDetailPageItems must be positive (was {limits.MaxDetailPageItems}).");
        }
        else if (limits.DetailPageItemLimit <= 0 || limits.DetailPageItemLimit > limits.MaxDetailPageItems)
        {
            violations.Add(
                $"DetailPageItemLimit must be within [1, MaxDetailPageItems={limits.MaxDetailPageItems}] (was {limits.DetailPageItemLimit}).");
        }

        RequirePositive(violations, limits.DetailPageMaxBytes, "DetailPageMaxBytes");
        RequirePositive(violations, limits.DetailPageCandidateScanLimit, "DetailPageCandidateScanLimit");
        RequirePositive(violations, limits.HistoryPageCandidateScanLimit, "HistoryPageCandidateScanLimit");
        RequirePositive(violations, limits.SubAgentSummaryLimit, "SubAgentSummaryLimit");
        RequirePositive(violations, limits.SubAgentSummaryMaxBytes, "SubAgentSummaryMaxBytes");
        RequirePositive(violations, limits.SseCatchUpMaxEvents, "SseCatchUpMaxEvents");
        RequirePositive(violations, limits.SseCatchUpMaxBytes, "SseCatchUpMaxBytes");
        RequirePositive(violations, limits.CacheSessionLimit, "CacheSessionLimit");
        RequirePositive(violations, limits.CacheMaxBytes, "CacheMaxBytes");
        RequirePositive(violations, limits.BackfillBatchSize, "BackfillBatchSize");

        RequireNonNegative(violations, limits.SseCatchUpMaxDuration, "SseCatchUpMaxDuration");
        RequireNonNegative(violations, limits.BackfillBatchWorkBudget, "BackfillBatchWorkBudget");
        RequireNonNegative(violations, limits.BackfillBatchDelay, "BackfillBatchDelay");
        RequireNonNegative(violations, limits.BackfillReadyGracePeriod, "BackfillReadyGracePeriod");

        return violations;
    }

    private static void RequirePositive(List<string> violations, long value, string name)
    {
        if (value <= 0)
            violations.Add($"{name} must be positive (was {value}).");
    }

    private static void RequireNonNegative(List<string> violations, TimeSpan value, string name)
    {
        if (value < TimeSpan.Zero)
            violations.Add($"{name} must be non-negative (was {value}).");
    }
}
