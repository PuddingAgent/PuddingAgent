using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Runtime;

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
        out WarmPrefixCompactionPlan? plan,
        long? workUnitInputCapacity = null)
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
            targetRatio,
            workUnitInputCapacity: workUnitInputCapacity);
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
            selection.InitialSnapshot,
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
            $"{CheckpointPreamble}\n\n{ContextSummaryMarkers.WarmPrefixCheckpoint}\n{summary}\n</compacted-summary>");
        var result = plan.RetainedMessages.ToList();
        result.Insert(plan.LeadingSystemMessageCount, checkpoint);
        return result;
    }
}

internal sealed record WarmPrefixCompactionPlan(
    IReadOnlyList<ChatMessage> SummaryRequestMessages,
    IReadOnlyList<ChatMessage> RetainedMessages,
    ContextUsageSnapshot EstimatedRetainedSnapshot,
    /// <summary>
    /// 压缩前（即将发出的摘要请求）的完整测量。调用方必须把它发布为 session 的
    /// <c>currentPreparedRequest</c>：候选测量是只计量的，不会自动发布，
    /// 否则请求级归因会落到「移除后」的形状上（诊断 2026-10-07 §5.1）。
    /// </summary>
    ContextUsageSnapshot InitialSnapshot,
    int EffectiveInputLimit,
    int InitialUsedTokens,
    int InitialMessageCount,
    int RemovedMessageCount,
    int RemovedTokenEstimate,
    int LeadingSystemMessageCount)
{
    /// <summary>
    /// 本次请求是否**真的**越过硬输入边界（估算输入 &gt; 有效输入上限，两侧同源：模型窗口 − 实际输出预算 − 安全余量，
    /// 并取 Provider 输入上限与 WorkUnit 容量的较小值）。
    /// <para>
    /// false ⇒ 候选只来自**软阈值**（输入仍能安全容纳）：允许把摘要生成推迟到轮次安全点/终态之后，
    /// 不得为了软维护阻塞主请求首块（ADR-095 D2）。
    /// true ⇒ 必须同步保护：不做同步压缩，出站只能靠请求副本硬裁剪，会丢失会话级记忆连续性。
    /// </para>
    /// </summary>
    internal bool RequiresSynchronousProtection => InitialUsedTokens > EffectiveInputLimit;

    /// <summary>相对硬边界的余量（负数 = 已越界）。用于日志/生命周期事件归因，不参与准入判断。</summary>
    internal int HardHeadroomTokens => EffectiveInputLimit - InitialUsedTokens;
}

/// <summary>
/// warm-prefix 压缩一次尝试的处置结果（ADR-095：成功/跳过/延期/失败必须显式区分）。
/// </summary>
internal enum WarmPrefixCompactionDisposition
{
    /// <summary>未形成候选（软阈值未达 / 无可裁剪单元）：什么都没发生。</summary>
    NotNeeded,

    /// <summary>
    /// 形成候选但请求仍安全：摘要生成被移出发送关键路径，本次未调用摘要模型、未改历史。
    /// 这是**延期**不是失败，也不是"已压缩"。
    /// </summary>
    DeferredSoft,

    /// <summary>硬输入边界保护成功，历史已被 checkpoint 替换。</summary>
    Applied,

    /// <summary>摘要请求失败/被拒绝（含返回工具调用），历史保持原样。</summary>
    Failed,

    /// <summary>摘要未缩小选中跨度（无收益），历史保持原样。</summary>
    NoGain,
}

/// <summary>warm-prefix 压缩的稳定原因码（日志与生命周期事件共用，便于聚合指标）。</summary>
internal static class WarmPrefixCompactionReasons
{
    /// <summary>候选仅来自软阈值且请求仍能安全容纳 ⇒ 可选维护延期到终态之后。</summary>
    internal const string SoftDeferred = "soft_threshold_request_still_safe";

    /// <summary>估算输入已越过有效输入上限 ⇒ 必须同步压缩保护。</summary>
    internal const string HardProtection = "hard_input_budget_protection";

    /// <summary>摘要请求失败或被拒绝。</summary>
    internal const string SummaryRejected = "summary_rejected";

    /// <summary>摘要未比被替换跨度更小（无收益）。</summary>
    internal const string SummaryNoGain = "summary_no_gain";
}

internal sealed record WarmPrefixCompactionOutcome(
    bool Compacted,
    WarmPrefixCompactionPlan? Plan,
    double TriggerRatio,
    double TargetRatio,
    DateTimeOffset? StartedAtUtc,
    WarmPrefixCompactionDisposition Disposition = WarmPrefixCompactionDisposition.NotNeeded,
    long? DurationMs = null,
    string? SkipReason = null)
{
    internal static WarmPrefixCompactionOutcome NotNeeded { get; } =
        new(false, null, 0, 0, null, WarmPrefixCompactionDisposition.NotNeeded);

    /// <summary>是否为「软维护延期」：调用方据此计入推迟计数，且不得当作压缩完成。</summary>
    internal bool DeferredSoft => Disposition == WarmPrefixCompactionDisposition.DeferredSoft;
}
