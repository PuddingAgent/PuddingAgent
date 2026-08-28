using PuddingCode.Models;
using PuddingCode.Platform;

namespace PuddingRuntime.Services;

/// <summary>
/// Warm-prefix checkpoint planning for long Agent loops.
/// The auxiliary summary request replays the exact outbound conversation and tool header,
/// then appends one fixed user instruction. Only after a valid, shrinking summary returns
/// may the caller atomically replace the selected old head with one checkpoint message.
/// </summary>
internal static class WarmPrefixCompaction
{
    internal const string CheckpointPreamble =
        "This is an automatically generated checkpoint condensing an earlier span of the conversation " +
        "to free up context. Treat the captured context as established background and build on it " +
        "without restating it. Continue the task directly from the messages that follow, without " +
        "acknowledging this checkpoint.";

    internal const string SummaryInstruction = """
        You are now acting as a compaction engine for this AI coding assistant. Condense the conversation ABOVE into a structured checkpoint that lets the same model resume the work with no loss of essential context.

        Output EXACTLY the Markdown structure below. Keep every section in order, use terse bullets, and write "(none)" for an empty section.

        ## Primary Request and Intent
        ## Key Technical Concepts
        ## Files and Code
        ## Errors and Fixes
        ## Pending Jobs
        ## Current Work
        ## Next Step
        ## Critical Context

        Preserve exact file paths, commands, error strings, identifiers, numeric values, function signatures, user corrections, and safety constraints. Merge any prior <compacted-summary> instead of copying it verbatim. Do not continue the task, call tools, mention this request, or output anything outside the checkpoint Markdown.
        """;

    internal static bool TryCreatePlan(
        ContextUsageSnapshotStore usageStore,
        string sessionId,
        IReadOnlyList<ChatMessage> durableHistory,
        IReadOnlyList<ChatMessage> exactOutboundHistory,
        IReadOnlyList<LlmToolDefinition>? tools,
        LlmConfig? config,
        double triggerRatio,
        double targetRatio,
        out WarmPrefixCompactionPlan? plan)
    {
        ArgumentNullException.ThrowIfNull(usageStore);
        ArgumentNullException.ThrowIfNull(durableHistory);
        ArgumentNullException.ThrowIfNull(exactOutboundHistory);

        plan = null;
        if (durableHistory.Count != exactOutboundHistory.Count)
            return false;

        var selection = LlmRequestBudgetGuard.PrepareSoftCompaction(
            usageStore,
            sessionId,
            exactOutboundHistory,
            tools,
            config,
            triggerRatio,
            targetRatio);
        if (!selection.Compacted || selection.RemovedMessageCount <= 0)
            return false;

        var leadingSystemCount = durableHistory.TakeWhile(message => message.Role == ChatRole.System).Count();
        if (leadingSystemCount == 0
            || leadingSystemCount + selection.RemovedMessageCount >= durableHistory.Count)
        {
            return false;
        }

        var removed = durableHistory
            .Skip(leadingSystemCount)
            .Take(selection.RemovedMessageCount)
            .ToArray();
        var retained = durableHistory
            .Take(leadingSystemCount)
            .Concat(durableHistory.Skip(leadingSystemCount + selection.RemovedMessageCount))
            .ToArray();
        if (removed.Length == 0 || retained.Count(message => message.Role != ChatRole.System) == 0)
            return false;

        var summaryRequest = exactOutboundHistory
            .Append(new ChatMessage(ChatRole.User, SummaryInstruction))
            .ToArray();
        var removedTokens = removed.Sum(message =>
            ContextUsageSnapshotStore.CountTokens(message.Content ?? string.Empty));

        plan = new WarmPrefixCompactionPlan(
            summaryRequest,
            retained,
            selection.Snapshot,
            selection.EffectiveInputLimit,
            selection.InitialUsedTokens,
            selection.InitialMessageCount,
            selection.RemovedMessageCount,
            removedTokens,
            leadingSystemCount);
        return true;
    }

    internal static IReadOnlyList<ChatMessage>? TryCreateCheckpoint(
        WarmPrefixCompactionPlan plan,
        string? rawSummary)
    {
        if (string.IsNullOrWhiteSpace(rawSummary))
            return null;

        var summary = rawSummary.Trim();
        var summaryTokens = ContextUsageSnapshotStore.CountTokens(summary);
        if (summaryTokens <= 0 || summaryTokens >= plan.RemovedTokenEstimate)
            return null;

        var checkpoint = new ChatMessage(
            ChatRole.User,
            $"{CheckpointPreamble}\n\n<compacted-summary>\n{summary}\n</compacted-summary>");
        var result = plan.RetainedMessages.ToList();
        result.Insert(plan.LeadingSystemMessageCount, checkpoint);
        return result;
    }
}

internal sealed record WarmPrefixCompactionPlan(
    IReadOnlyList<ChatMessage> SummaryRequestMessages,
    IReadOnlyList<ChatMessage> RetainedMessages,
    ContextUsageSnapshot EstimatedRetainedSnapshot,
    int EffectiveInputLimit,
    int InitialUsedTokens,
    int InitialMessageCount,
    int RemovedMessageCount,
    int RemovedTokenEstimate,
    int LeadingSystemMessageCount);

internal sealed record WarmPrefixCompactionOutcome(
    bool Compacted,
    WarmPrefixCompactionPlan? Plan,
    double TriggerRatio,
    double TargetRatio,
    DateTimeOffset? StartedAtUtc)
{
    internal static WarmPrefixCompactionOutcome NotNeeded { get; } =
        new(false, null, 0, 0, null);
}
