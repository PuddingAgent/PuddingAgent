namespace PuddingRuntime.Services.AgentLoop;

/// <summary>
/// Centralizes the compatibility heuristic that turns a nominally successful
/// execution into a failure when its final reply only explains a tool failure.
/// Structured, contract-complete delegated reports are authoritative and must
/// not be reclassified because their evidence happens to contain words such as
/// <c>Failed</c> or <c>timed out</c>.
/// </summary>
internal static class AgentExecutionOutcomePolicy
{
    internal static bool ShouldDowngradeSuccessfulExecution(
        bool currentlySuccessful,
        int toolFailureCount,
        string? finalReply,
        string? expectedOutputContract)
    {
        if (!currentlySuccessful || toolFailureCount <= 0)
            return false;

        if (CanonicalWorkReport.IsRequiredBy(expectedOutputContract)
            && CanonicalWorkReport.TryValidate(finalReply, out _))
        {
            return false;
        }

        return LooksLikeFailureReply(finalReply);
    }

    internal static bool LooksLikeFailureReply(string? reply)
    {
        if (string.IsNullOrWhiteSpace(reply))
            return false;

        return reply.Contains("执行失败", StringComparison.OrdinalIgnoreCase)
            || reply.Contains("FAILED", StringComparison.OrdinalIgnoreCase)
            || reply.Contains("failed", StringComparison.OrdinalIgnoreCase)
            || reply.Contains("Command timed out", StringComparison.OrdinalIgnoreCase)
            || reply.Contains("timed out", StringComparison.OrdinalIgnoreCase);
    }
}
