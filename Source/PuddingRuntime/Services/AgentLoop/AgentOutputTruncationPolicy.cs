namespace PuddingRuntime.Services.AgentLoop;

/// <summary>
/// Prevents a provider output-limit stop from being projected as a successful
/// agent turn. One short recovery round is allowed so a reasoning-heavy model
/// can take an immediate tool action without replaying its discarded reasoning.
/// </summary>
internal static class AgentOutputTruncationPolicy
{
    internal const int MaxRecoveryAttempts = 1;

    internal static bool IsTruncated(string? finishReason)
        => finishReason is "length" or "incomplete";

    internal static bool ShouldRetry(int recoveryAttempts, int round, int maxRounds)
        => recoveryAttempts < MaxRecoveryAttempts && round < maxRounds - 1;

    internal static string RecoveryPrompt(bool hadDisplayableContent)
        => hadDisplayableContent
            ? "[SYSTEM] The previous response hit the output limit and is incomplete. "
              + "Do not repeat the analysis. Invoke the single best next tool now, or return only the concise final result if no tool is required."
            : "[SYSTEM] The previous response exhausted the output limit in internal reasoning without taking action. "
              + "Do not continue or restate that reasoning. Invoke the single best next tool now; if no tool is required, return a concise final result now.";
}
