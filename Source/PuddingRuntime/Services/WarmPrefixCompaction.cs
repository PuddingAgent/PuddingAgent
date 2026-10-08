using System.Security.Cryptography;
using System.Text;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingContextPolicy;

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
            leadingSystemCount,
            // F2：冻结候选的版本与身份，提交前做严格校验（旧候选不得覆盖新消息）。
            removed.Select(ComputeMessageIdentity).ToArray(),
            // CAS 的保留身份是「被替换跨度**之后**的后缀」，不含跨度之前的保留前缀（前导 system）。
            retained.Skip(leadingSystemCount).Select(ComputeMessageIdentity).ToArray(),
            ComputeRevisionHash(durableHistory),
            ResolveRouteVersion(config),
            ResolveToolSpecVersion(exactOutboundHistory, tools),
            targetRatio,
            // 持久代次要等 checkpoint/generation 落库后才能提供；当前恒为 0，
            // 因此「仅代次变化」这一路今天抓不到（见 changelog「未完成」）。
            SourceGeneration: 0);
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

    /// <summary>
    /// 提交前的**唯一应用校验**（F2）：先做成 checkpoint、再对当前历史做严格版本/身份 CAS，
    /// 然后用**真实摘要**重新完整计量整请求，最后过收益与硬上限准入。
    /// <para>
    /// 关键纪律：计划阶段的 <see cref="WarmPrefixCompactionPlan.EstimatedRetainedSnapshot"/> 是
    /// 「移除后、未加摘要」的估算，**不得**当作提交后的值；本方法返回的
    /// <see cref="WarmPrefixApplyVerification.AfterSnapshot"/> 才是重算后的真实 after。
    /// </para>
    /// </summary>
    internal static WarmPrefixApplyVerification VerifyApplication(
        ContextUsageSnapshotStore usageStore,
        string sessionId,
        IReadOnlyList<ChatMessage> currentDurableHistory,
        IReadOnlyList<LlmToolDefinition>? tools,
        LlmConfig? config,
        WarmPrefixCompactionPlan plan,
        string? rawSummary)
    {
        ArgumentNullException.ThrowIfNull(usageStore);
        ArgumentNullException.ThrowIfNull(currentDurableHistory);
        ArgumentNullException.ThrowIfNull(plan);

        // ① 摘要必须真实缩小被替换跨度，否则无收益。
        var checkpoint = TryCreateCheckpoint(plan, rawSummary);
        if (checkpoint is null)
        {
            return WarmPrefixApplyVerification.Rejected(
                WarmPrefixCompactionReasons.SummaryNoGain,
                reclaimTokens: 0,
                meetsTarget: false);
        }

        // ② 严格 CAS：源 revision 必须未变，且被替换跨度与保留后缀仍在当前历史中原样存在。
        //    任一不符 ⇒ stale_candidate，历史保持原样（不拼接新尾部、不自动 rebase）。
        var currentRevision = ComputeRevisionHash(currentDurableHistory);
        var frozen = new FrozenCandidateDescriptor(
            Fingerprint: currentRevision,
            SourceGeneration: plan.SourceGeneration,
            SourceRevision: plan.SourceRevisionHash,
            RouteVersion: plan.RouteVersion,
            ToolSpecVersion: plan.ToolSpecVersion,
            PolicyVersion: ContextPolicyVersion.Current,
            RemovedIdentities: plan.RemovedIdentities,
            RetainedIdentities: plan.RetainedIdentities);
        var current = new CurrentContextDescriptor(
            Generation: plan.SourceGeneration,
            Revision: currentRevision,
            RouteVersion: plan.RouteVersion,
            ToolSpecVersion: plan.ToolSpecVersion,
            PolicyVersion: ContextPolicyVersion.Current,
            MessageIdentities: currentDurableHistory.Select(ComputeMessageIdentity).ToArray());
        var applicability = ContextCandidateApplicability.Validate(frozen, current);
        if (!applicability.IsApplicable)
        {
            return WarmPrefixApplyVerification.Rejected(
                applicability.Reason,
                reclaimTokens: 0,
                meetsTarget: false);
        }

        // ③ 加入摘要后**重新完整计量**（只计量、不发布）：这才是提交后的真实值。
        var afterSnapshot = usageStore.MeasureLlmRequest(
            sessionId,
            checkpoint,
            tools,
            config?.ModelId);

        // ④ 收益与硬上限准入：跨度与摘要都用**校准后**口径，避免与 InitialUsedTokens 混量纲。
        var calibratedRemovedSpan = Math.Max(
            0,
            plan.InitialUsedTokens - plan.EstimatedRetainedSnapshot.UsedTokens);
        var summaryTokens = ContextUsageSnapshotStore.CountTokens(rawSummary?.Trim() ?? string.Empty);
        var calibratedSummary = (int)Math.Min(
            int.MaxValue,
            Math.Ceiling(summaryTokens * Math.Max(1.0, plan.InitialSnapshot.PromptCalibrationRatio)));
        var admission = ContextNetGainAdmission.Evaluate(new NetGainInputs(
            InitialUsedTokens: plan.InitialUsedTokens,
            RemovedTokens: calibratedRemovedSpan,
            SummaryTokens: calibratedSummary,
            EffectiveInputTokens: plan.EffectiveInputLimit,
            TargetRatio: plan.TargetRatio));
        var reclaim = Math.Max(0, plan.InitialUsedTokens - afterSnapshot.UsedTokens);
        if (!admission.CanCommit)
        {
            return WarmPrefixApplyVerification.Rejected(
                admission.Reason,
                reclaim,
                meetsTarget: false,
                afterSnapshot);
        }

        // 真实计量优先于算式：算出来能过、实测仍越界 ⇒ 一律拒绝提交。
        if (afterSnapshot.UsedTokens > plan.EffectiveInputLimit)
        {
            return WarmPrefixApplyVerification.Rejected(
                "after_summary_exceeds_hard_limit",
                reclaim,
                meetsTarget: false,
                afterSnapshot);
        }

        var target = (int)Math.Floor(plan.EffectiveInputLimit * Math.Clamp(plan.TargetRatio, 0.05, 1.0));
        return new WarmPrefixApplyVerification(
            CanApply: true,
            Reason: "applied",
            AppliedMessages: checkpoint,
            AfterSnapshot: afterSnapshot,
            ReclaimTokens: reclaim,
            MeetsTarget: afterSnapshot.UsedTokens <= target);
    }

    /// <summary>稳定的消息身份（内容 + 角色 + 工具配对元数据），用于严格 CAS 与指纹；不展开密钥。</summary>
    internal static string ComputeMessageIdentity(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var builder = new StringBuilder();
        builder.Append(message.Role).Append('\u001f')
            .Append(message.Content ?? string.Empty).Append('\u001f')
            .Append(message.ToolCallId ?? string.Empty).Append('\u001f')
            .Append(message.ToolName ?? string.Empty);
        if (message.ToolCalls is { Count: > 0 })
        {
            builder.Append('\u001f');
            foreach (var call in message.ToolCalls)
                builder.Append(call.Id).Append(':').Append(call.Name).Append('\u001e');
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>整段历史的 revision（身份序列的 SHA-256）：CAS 的主判据。</summary>
    internal static string ComputeRevisionHash(IReadOnlyList<ChatMessage> history)
    {
        ArgumentNullException.ThrowIfNull(history);

        var builder = new StringBuilder();
        foreach (var message in history)
            builder.Append(ComputeMessageIdentity(message)).Append('\n');
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexStringLower(hash);
    }

    private static string ResolveRouteVersion(LlmConfig? config)
        => string.IsNullOrWhiteSpace(config?.ModelId) ? "unknown-route" : config!.ModelId!;

    private static string ResolveToolSpecVersion(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<LlmToolDefinition>? tools)
    {
        if (tools is not { Count: > 0 })
            return "no-tools";

        return PrefixCacheSnapshotBuilder.Build(messages, tools).ToolSpecHash ?? "unknown-tools";
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
    int LeadingSystemMessageCount,
    /// <summary>被替换跨度的消息身份（冻结时固化，提交前 CAS 比对）。</summary>
    IReadOnlyList<string> RemovedIdentities,
    /// <summary>保留后缀的消息身份（含受保护尾部；冻结时固化，提交前逐位比对）。</summary>
    IReadOnlyList<string> RetainedIdentities,
    /// <summary>冻结时整段历史的 revision（身份序列 SHA-256）：CAS 主判据。</summary>
    string SourceRevisionHash,
    /// <summary>路由/模型版本（参与 CAS，路由切换即作废旧候选）。</summary>
    string RouteVersion,
    /// <summary>工具定义版本（参与 CAS，工具集合变化即作废旧候选）。</summary>
    string ToolSpecVersion,
    /// <summary>压实目标比例（收益准入用）。</summary>
    double TargetRatio,
    /// <summary>
    /// 源上下文代次。**当前恒为 0**：持久 checkpoint/generation 尚未落地，
    /// 因此「仅代次变化」这一路 CAS 今天抓不到（见 changelog「未完成」）。
    /// </summary>
    long SourceGeneration = 0)
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
/// 提交前校验结果（F2 唯一应用边界）。只有 <see cref="CanApply"/> 才允许替换内存历史；
/// <see cref="AfterSnapshot"/> 是**加入真实摘要后重新计量**的值，不是计划估算。
/// </summary>
internal sealed record WarmPrefixApplyVerification(
    bool CanApply,
    string Reason,
    IReadOnlyList<ChatMessage>? AppliedMessages,
    ContextUsageSnapshot? AfterSnapshot,
    int ReclaimTokens,
    bool MeetsTarget)
{
    /// <summary>拒绝提交：历史保持原样，原因码进入日志与生命周期事件。</summary>
    internal static WarmPrefixApplyVerification Rejected(
        string reason,
        int reclaimTokens,
        bool meetsTarget,
        ContextUsageSnapshot? afterSnapshot = null)
        => new(false, reason, null, afterSnapshot, reclaimTokens, meetsTarget);
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

    /// <summary>
    /// 冻结候选与当前历史/版本不再匹配（revision、跨度、保留后缀、路由或工具变化）：
    /// 丢弃候选、历史保持原样。这**不是**「压缩完成」，也不是收益性失败。
    /// </summary>
    StaleCandidate,

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
