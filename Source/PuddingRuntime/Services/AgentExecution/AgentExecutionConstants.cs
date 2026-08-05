namespace PuddingRuntime.Services;

/// <summary>
/// Shared constants for the agent execution pipeline.
/// Single source of truth for wire-format strings that are produced by execution
/// and consumed by downstream stages (compaction noise filter, persistence, etc.).
/// </summary>
public static class AgentExecutionConstants
{
    /// <summary>
    /// Placeholder reply returned when an inbound message is detected as a duplicate
    /// (same message_id re-dispatched after Ack loss / retry).
    /// Produced by AgentExecutionService (buffered + streaming paths) and consumed by
    /// ContextCompactionService.IsNoise — both sides MUST reference this constant so a
    /// format change can never silently disable the noise filter.
    /// </summary>
    public const string DuplicateMessagePlaceholder = "(duplicate message — already processed)";

    /// <summary>
    /// Legacy hyphen variant of <see cref="DuplicateMessagePlaceholder"/> that may still
    /// exist in persisted history from before the em-dash canonical form. Retained for
    /// noise-filter tolerance only; never emit new messages with this form.
    /// </summary>
    public const string DuplicateMessagePlaceholderLegacyHyphen = "(duplicate message - already processed)";
}
