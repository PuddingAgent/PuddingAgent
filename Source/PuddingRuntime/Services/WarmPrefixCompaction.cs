using System.Security.Cryptography;
using System.Text;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingContextPolicy;

namespace PuddingRuntime.Services;

/// <summary>
/// Warm-prefix checkpoint planning for long Agent loops.
/// The plan freezes one exact replacement span; the caller summarizes only that span in bounded parts. Only a valid, shrinking summary within token and text budgets
/// may atomically replace the selected old head with one checkpoint message.
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
        long? workUnitInputCapacity = null,
        /// <summary>
        /// 冻结候选时读到的**持久会话代际**（`Session.CompactionGeneration`）。默认 0 仅为兼容既有调用；
        /// 生产路径必须传入真实值，否则「仅代次变化」这一路 CAS 抓不到（durable compaction 与
        /// warm-prefix 交错时会用旧候选覆盖新事实）。
        /// </summary>
        int sourceGeneration = 0,
        /// <summary>Provider 身份（Runtime 的 LlmConfig 不携带，由调用方给出）：参与路由 CAS。</summary>
        string? providerId = null,
        int maxRequestTextBytes = 0,
        double textTriggerRatio = 1.0,
        bool enforceTarget = false)
    {
        ArgumentNullException.ThrowIfNull(usageStore);
        ArgumentNullException.ThrowIfNull(durableHistory);
        ArgumentNullException.ThrowIfNull(exactOutboundHistory);

        plan = null;
        if (durableHistory.Count != exactOutboundHistory.Count)
            return false;

        var initial = usageStore.MeasureLlmRequest(sessionId, exactOutboundHistory, tools, config?.ModelId);
        var limit = LlmRequestBudgetGuard.ResolveEffectiveInputLimit(config, workUnitInputCapacity: workUnitInputCapacity);
        var initialBytes = LlmRequestTextBudget.Measure(exactOutboundHistory, tools);
        if (initial.UsedTokens < limit * Math.Clamp(triggerRatio, 0.05, 1)
            && (maxRequestTextBytes <= 0 || initialBytes < maxRequestTextBytes * textTriggerRatio)) return false;
        var shapes = exactOutboundHistory.Select(message => new ContextMessageShape(
            ComputeMessageIdentity(message), Enum.Parse<ContextMessageRole>(message.Role.ToString()),
            usageStore.MeasureLlmRequest(sessionId, [message], null, config?.ModelId).UsedTokens,
            LlmRequestTextBudget.MeasureMessage(message), message.ToolCalls?.Select(call => call.Id).ToArray(),
            message.ToolCallId)).ToArray();
        var wrapper = CreateCheckpointMessage("");
        var selection = ContextCandidateSelector.Select(shapes,
            Math.Max(0, initial.UsedTokens - shapes.Sum(m => m.TokenEstimate)),
            ContextCapacityArithmetic.Resolve(new ContextCapacityInputs(limit)),
            new CandidateSelectionOptions(TriggerRatio: triggerRatio, TargetRatio: targetRatio,
                TextTriggerRatio: textTriggerRatio, EnforceTarget: enforceTarget),
            ResolveRouteVersion(config, providerId), ResolveToolSpecVersion(exactOutboundHistory, tools),
            sourceGeneration, ComputeRevisionHash(durableHistory),
            new ContextRequestBudget(LlmRequestTextBudget.Measure([], tools), maxRequestTextBytes,
                usageStore.MeasureLlmRequest(sessionId, [wrapper], null, config?.ModelId).UsedTokens,
                LlmRequestTextBudget.MeasureMessage(wrapper)));
        if (!selection.Created) return false;

        var replacementStart = selection.RemovedFromIndex;
        var removed = durableHistory.Skip(replacementStart).Take(selection.RemovedCount).ToArray();
        var retained = durableHistory.Take(replacementStart)
            .Concat(durableHistory.Skip(replacementStart + selection.RemovedCount)).ToArray();
        var retainedOutbound = exactOutboundHistory.Take(replacementStart)
            .Concat(exactOutboundHistory.Skip(replacementStart + selection.RemovedCount)).ToArray();
        var retainedSnapshot = usageStore.MeasureLlmRequest(sessionId, retainedOutbound, tools, config?.ModelId);
        var allowance = selection.SummaryAllowance! with
        {
            Tokens = (int)(selection.SummaryAllowance!.Tokens / Math.Max(1.0, initial.PromptCalibrationRatio)),
        };
        var removedTokens = usageStore.MeasureLlmRequest(sessionId,
            exactOutboundHistory.Skip(replacementStart).Take(selection.RemovedCount).ToArray(), null, config?.ModelId).RawEstimatedTokens;

        plan = new WarmPrefixCompactionPlan(
            retained,
            retainedSnapshot,
            initial,
            limit,
            initial.UsedTokens,
            durableHistory.Count,
            selection.RemovedCount,
            removedTokens,
            replacementStart,
            // F2：冻结候选的版本与身份，提交前做严格校验（旧候选不得覆盖新消息）。
            removed.Select(ComputeMessageIdentity).ToArray(),
            // CAS 的保留身份是「被替换跨度**之后**的后缀」，不含跨度之前的保留前缀（前导 system）。
            retained.Skip(replacementStart).Select(ComputeMessageIdentity).ToArray(),
            ComputeRevisionHash(durableHistory),
            ResolveRouteVersion(config, providerId),
            ResolveToolSpecVersion(exactOutboundHistory, tools),
            targetRatio,
            // 提交时同时校验持久代次，不能让旧候选覆盖新 checkpoint。
            SourceGeneration: sourceGeneration,
            InitialTextBytes: initialBytes,
            MaxRequestTextBytes: maxRequestTextBytes,
            SummaryAllowance: allowance);
        return true;
    }

    internal static string SummaryBudgetInstruction(ContextSummaryAllowance allowance) =>
        $"\nWrite a complete concise checkpoint. Aim for at most {Math.Max(1, allowance.Tokens / 2)} tokens; " +
        $"the hard content budget is {allowance.Tokens} tokens and {allowance.TextBytes} serialized UTF-8 bytes. " +
        "Prioritize unresolved intent, corrections, decisions, evidence locations and next actions. Do not pad sections.";

    private static ChatMessage CreateCheckpointMessage(string summary) => ChatMessage.ContextSummary(
        $"{CheckpointPreamble}\n\n{ContextSummaryMarkers.WarmPrefixCheckpoint}\n{summary}\n</compacted-summary>");

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

        var checkpoint = CreateCheckpointMessage(summary);
        var result = plan.RetainedMessages.ToList();
        result.Insert(plan.ReplacementStart, checkpoint);
        return result;
    }

    /// <summary>
    /// 被替换跨度（warm-prefix 候选）在 outbound 历史中的切片：本地兜底摘要**只覆盖它**，
    /// 不得把保留尾部/受保护消息也概括进替代摘要（与 <see cref="VerifyApplication"/> 的切分同源）。
    /// </summary>
    internal static IReadOnlyList<ChatMessage> SelectRemovedMessages(
        IReadOnlyList<ChatMessage> durableHistory,
        WarmPrefixCompactionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(durableHistory);
        ArgumentNullException.ThrowIfNull(plan);
        return durableHistory
            .Skip(plan.ReplacementStart)
            .Take(plan.RemovedMessageCount)
            .ToArray();
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
        string? rawSummary,
        /// <summary>
        /// **提交时**从真实状态取得的当前上下文描述符（generation/route/tool 版本、revision 基准）。
        /// 为 null ⇒ 一律拒绝（不能拿 plan 里的冻结值自比较，那会让 CAS 变成同义反复：R4 §5.1）。
        /// </summary>
        CurrentContextValues? currentValues = null,
        IReadOnlyList<ChatMessage>? exactOutboundHistory = null)
    {
        ArgumentNullException.ThrowIfNull(usageStore);
        ArgumentNullException.ThrowIfNull(currentDurableHistory);
        ArgumentNullException.ThrowIfNull(plan);

        // ⓪ 必须先有「提交时的真实当前值」，否则无法做真实 CAS。
        if (currentValues is null)
        {
            return WarmPrefixApplyVerification.Rejected(
                WarmPrefixCompactionReasons.CurrentContextUnavailable,
                reclaimTokens: 0,
                meetsTarget: false);
        }

        // ① 摘要必须真实缩小被替换跨度，否则无收益。
        var checkpoint = TryCreateCheckpoint(plan, rawSummary);
        if (checkpoint is null)
        {
            return WarmPrefixApplyVerification.Rejected(
                WarmPrefixCompactionReasons.SummaryNoGain,
                reclaimTokens: 0,
                meetsTarget: false);
        }

        // ② 严格 CAS：冻结候选 vs **提交时真实当前值**（generation/route/tool/revision/跨度身份）。
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
            RetainedIdentities: plan.RetainedIdentities,
            RemovedFromIndex: plan.ReplacementStart);
        var current = new CurrentContextDescriptor(
            Generation: currentValues.Generation,
            Revision: currentRevision,
            RouteVersion: currentValues.RouteVersion,
            ToolSpecVersion: currentValues.ToolSpecVersion,
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
        var measuredCheckpoint = exactOutboundHistory is null ? checkpoint :
            exactOutboundHistory.Take(plan.ReplacementStart).Append(checkpoint[plan.ReplacementStart])
                .Concat(exactOutboundHistory.Skip(plan.ReplacementStart + plan.RemovedMessageCount)).ToArray();
        var afterSnapshot = usageStore.MeasureLlmRequest(
            sessionId,
            measuredCheckpoint,
            tools,
            config?.ModelId);

        var afterTextBytes = LlmRequestTextBudget.Measure(measuredCheckpoint, tools);
        var summaryTextBytes = LlmRequestTextBudget.MeasureSummary(rawSummary?.Trim() ?? "");

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
                afterSnapshot, afterTextBytes);
        }

        // 真实计量优先于算式：算出来能过、实测仍越界 ⇒ 一律拒绝提交。
        if (afterSnapshot.UsedTokens > plan.EffectiveInputLimit)
        {
            return WarmPrefixApplyVerification.Rejected(
                "after_summary_exceeds_hard_limit",
                reclaim,
                meetsTarget: false,
                afterSnapshot, afterTextBytes);
        }

        if (plan.MaxRequestTextBytes > 0 && afterTextBytes > plan.MaxRequestTextBytes)
            return WarmPrefixApplyVerification.Rejected("after_summary_exceeds_text_budget", reclaim, false, afterSnapshot, afterTextBytes);
        if (plan.SummaryAllowance is { } allowance &&
            (summaryTokens > allowance.Tokens || summaryTextBytes > allowance.TextBytes))
            return WarmPrefixApplyVerification.Rejected("summary_exceeds_planned_allowance", reclaim, false, afterSnapshot, afterTextBytes);

        var target = (int)Math.Floor(plan.EffectiveInputLimit * Math.Clamp(plan.TargetRatio, 0.05, 1.0));
        // F2 持久化：把「被替换跨度」的**对象**带出去，供调用方解析持久 MessageId。
        // CAS 已保证跨度与保留后缀原样存在，因此按冻结时的布局切分是安全的
        // （retained prefix → removed span → retained suffix，见 TryCreatePlan）。
        var removedMessages = currentDurableHistory
            .Skip(plan.ReplacementStart)
            .Take(plan.RemovedMessageCount)
            .ToArray();
        return new WarmPrefixApplyVerification(
            CanApply: true,
            Reason: "applied",
            AppliedMessages: checkpoint,
            AfterSnapshot: afterSnapshot,
            ReclaimTokens: reclaim,
            MeetsTarget: afterSnapshot.UsedTokens <= target,
            RemovedMessages: removedMessages,
            AfterTextBytes: afterTextBytes);
    }

    /// <summary>Only an intact frozen prefix can adopt appended messages. Final verification still runs under the commit lease.</summary>
    internal static WarmPrefixCompactionPlan? RefreshAppendOnlyPlan(
        ContextUsageSnapshotStore store, string sessionId, IReadOnlyList<ChatMessage> frozenHistory,
        IReadOnlyList<ChatMessage> currentHistory, IReadOnlyList<ChatMessage> currentOutbound,
        IReadOnlyList<LlmToolDefinition>? tools, LlmConfig? config, WarmPrefixCompactionPlan plan,
        CurrentContextValues current, out string reason)
    {
        var revision = ComputeRevisionHash(currentHistory);
        var verdict = PrefixCompactionPolicy.ValidateAppendOnly(
            new("", plan.SourceGeneration, plan.SourceRevisionHash, plan.RouteVersion, plan.ToolSpecVersion,
                ContextPolicyVersion.Current, plan.RemovedIdentities, plan.RetainedIdentities, plan.ReplacementStart),
            frozenHistory.Select(ComputeMessageIdentity).ToArray(),
            new(current.Generation, revision, current.RouteVersion, current.ToolSpecVersion,
                ContextPolicyVersion.Current, currentHistory.Select(ComputeMessageIdentity).ToArray()));
        reason = verdict.Reason;
        if (!verdict.IsApplicable || currentHistory.Count != currentOutbound.Count) return null;
        var retained = currentHistory.Take(plan.ReplacementStart)
            .Concat(currentHistory.Skip(plan.ReplacementStart + plan.RemovedMessageCount)).ToArray();
        var retainedOutbound = currentOutbound.Take(plan.ReplacementStart)
            .Concat(currentOutbound.Skip(plan.ReplacementStart + plan.RemovedMessageCount)).ToArray();
        var initial = store.MeasureLlmRequest(sessionId, currentOutbound, tools, config?.ModelId);
        return plan with
        {
            RetainedMessages = retained,
            RetainedIdentities = retained.Skip(plan.ReplacementStart).Select(ComputeMessageIdentity).ToArray(),
            SourceRevisionHash = revision, InitialMessageCount = currentHistory.Count,
            InitialSnapshot = initial, InitialUsedTokens = initial.UsedTokens,
            InitialTextBytes = LlmRequestTextBudget.Measure(currentOutbound, tools),
            EstimatedRetainedSnapshot = store.MeasureLlmRequest(sessionId, retainedOutbound, tools, config?.ModelId),
        };
    }

    /// <summary>
    /// 消息身份（CAS 与 revision 的原子）：必须覆盖**全部语义载荷**，否则「内容变了但身份没变」
    /// 会让陈旧候选仍然通过校验（R4 §5.3）。当前覆盖：角色、正文、思维链、工具回执身份、
    /// 工具调用（id/名称/**参数 JSON**）、有序内容部件（图片等 typed parts）。
    /// </summary>
    internal static string ComputeMessageIdentity(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var builder = new StringBuilder();
        builder.Append(message.Role).Append('\u001f')
            .Append(message.IsContextSummary).Append('\u001f')
            .Append(message.Content ?? string.Empty).Append('\u001f')
            .Append(message.ReasoningContent ?? string.Empty).Append('\u001f')
            .Append(message.ToolCallId ?? string.Empty).Append('\u001f')
            .Append(message.ToolName ?? string.Empty);
        if (message.ToolCalls is { Count: > 0 })
        {
            builder.Append('\u001f');
            foreach (var call in message.ToolCalls)
            {
                // 参数是语义载荷：不参与身份会让「同一 tool 换参数」被判成未变。
                builder.Append(call.Id).Append(':').Append(call.Name).Append(':')
                    .Append(call.ArgumentsJson ?? string.Empty).Append('\u001e');
            }
        }

        if (message.ContentParts is { Count: > 0 })
        {
            builder.Append('\u001f');
            foreach (var part in message.ContentParts)
            {
                builder.Append(part.Type).Append(':');
                if (part is LlmTextPart text)
                    builder.Append(text.Text);
                else
                    // 非文本部件（图片等）用其规范 JSON 作为载荷，避免只凭类型判别。
                    builder.Append(System.Text.Json.JsonSerializer.Serialize(part));
                builder.Append('\u001e');
            }
        }

        if (message.ContinuationState is not null)
            builder.Append('\u001f').Append(System.Text.Json.JsonSerializer.Serialize(message.ContinuationState));

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

    /// <summary>
    /// 路由版本（参与 CAS）：**不能只看 ModelId** —— 同名模型换 Provider/Endpoint/协议或容量
    /// 都会改变实际请求语义（R4 §5.2）。这里用安全身份（不含密钥）拼出稳定 token：
    /// provider / endpoint 主机 / 模型 / 协议 / 容量三元组。
    /// </summary>
    internal static string ResolveRouteVersion(
        LlmConfig? config,
        string? providerId = null,
        string? protocol = null)
    {
        var model = string.IsNullOrWhiteSpace(config?.ModelId) ? "unknown-model" : config!.ModelId!.Trim();
        // Provider 由调用方显式给出（Runtime 的 LlmConfig 不携带 ProviderId）；Protocol 取 config。
        // 同名模型换 Provider 必须让旧候选失效。
        var providerSource = providerId;
        var protocolSource = protocol ?? config?.Protocol;
        var provider = string.IsNullOrWhiteSpace(providerSource) ? "unknown-provider" : providerSource!.Trim();
        var endpoint = ResolveEndpointIdentity(config?.Endpoint);
        var proto = string.IsNullOrWhiteSpace(protocolSource) ? "unknown-protocol" : protocolSource!.Trim();

        return string.Join(
            '\u001f',
            provider,
            endpoint,
            model,
            proto,
            config?.MaxContextTokens?.ToString() ?? "-",
            config?.MaxInputTokens?.ToString() ?? "-",
            config?.MaxOutputTokens?.ToString() ?? "-");
    }

    /// <summary>只取 endpoint 的**主机**（去掉路径与查询，避免把密钥/参数写进身份）。</summary>
    private static string ResolveEndpointIdentity(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            return "unknown-endpoint";

        return Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            ? uri.Host
            : endpoint.Trim();
    }

    internal static string ResolveToolSpecVersion(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<LlmToolDefinition>? tools)
    {
        if (tools is not { Count: > 0 })
            return "no-tools";

        return PrefixCacheSnapshotBuilder.Build(messages, tools).ToolSpecHash ?? "unknown-tools";
    }
}

internal sealed record WarmPrefixCompactionPlan(
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
    int ReplacementStart,
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
    /// 源上下文持久代次，提交前参与 CAS 校验。
    /// </summary>
    long SourceGeneration = 0,
    long InitialTextBytes = 0,
    int MaxRequestTextBytes = 0,
    ContextSummaryAllowance? SummaryAllowance = null)
{
    /// <summary>
    /// 本次请求是否**真的**越过硬输入边界（估算输入 &gt; 有效输入上限，两侧同源：模型窗口 − 实际输出预算 − 安全余量，
    /// 并取 Provider 输入上限与 WorkUnit 容量的较小值），或越过独立的文本上传预算。
    /// <para>
    /// false ⇒ 候选只来自**软阈值**（输入仍能安全容纳）：允许把摘要生成推迟到轮次安全点/终态之后，
    /// 不得为了软维护阻塞主请求首块（ADR-095 D2）。
    /// true ⇒ 必须同步保护：不做同步压缩，出站只能靠请求副本硬裁剪，会丢失会话级记忆连续性。
    /// </para>
    /// </summary>
    internal bool RequiresSynchronousProtection => InitialUsedTokens > EffectiveInputLimit || RequiresTextProtection;
    internal bool RequiresTextProtection => MaxRequestTextBytes > 0 && InitialTextBytes > MaxRequestTextBytes;

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
    bool MeetsTarget,
    /// <summary>
    /// 被替换跨度的**消息对象**（仅 <see cref="CanApply"/> 为 true 时有值）。
    /// 调用方据此解析持久 `MessageId` 去做覆盖标注；解析不全即必须拒绝提交，保留原始历史。
    /// </summary>
    IReadOnlyList<ChatMessage>? RemovedMessages = null,
    long? AfterTextBytes = null)
{
    /// <summary>拒绝提交：历史保持原样，原因码进入日志与生命周期事件。</summary>
    internal static WarmPrefixApplyVerification Rejected(
        string reason,
        int reclaimTokens,
        bool meetsTarget,
        ContextUsageSnapshot? afterSnapshot = null,
        long? afterTextBytes = null)
        => new(false, reason, null, afterSnapshot, reclaimTokens, meetsTarget, AfterTextBytes: afterTextBytes);
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

    /// <summary>
    /// provider 摘要失败后改用**本地抽取式**摘要：只用于日志/摘要请求归因，不是生命周期失败原因；
    /// 降级后仍走同一校验 + 持久提交边界，成功时发 completed 而不是 failed。
    /// </summary>
    internal const string SummaryLocalFallback = "provider_summary_failed_local_fallback";

    /// <summary>摘要未比被替换跨度更小（无收益）。</summary>
    internal const string SummaryNoGain = "summary_no_gain";

    /// <summary>
    /// 调用方没能提供**提交时的真实当前值**（generation/route/tool 版本）⇒ 拒绝应用。
    /// 不得退回「拿 plan 里的冻结值自比较」：那会让 CAS 永远通过（R4）。
    /// </summary>
    internal const string CurrentContextUnavailable = "current_context_unavailable";
}

/// <summary>
/// **提交时**从真实状态取得的当前上下文值（供 warm-prefix 做真实 CAS）。
/// 与 <see cref="WarmPrefixCompactionPlan"/> 里的冻结值必须分别来源，否则校验是同义反复。
/// </summary>
internal sealed record CurrentContextValues(
    long Generation,
    string RouteVersion,
    string ToolSpecVersion);

internal sealed record WarmPrefixCompactionOutcome(
    bool Compacted,
    WarmPrefixCompactionPlan? Plan,
    double TriggerRatio,
    double TargetRatio,
    DateTimeOffset? StartedAtUtc,
    WarmPrefixCompactionDisposition Disposition = WarmPrefixCompactionDisposition.NotNeeded,
    long? DurationMs = null,
    string? SkipReason = null,
    /// <summary>
    /// 提交成功时**加入真实摘要后重新计量**的 after；未提交（延期/失败/无收益/陈旧）为 null。
    /// 调用方一律用本字段报告 after，**不得**改用 <c>Plan.EstimatedRetainedSnapshot</c>（那是未含摘要的计划估算）。
    /// </summary>
    int? AppliedAfterUsedTokens = null,
    IReadOnlyDictionary<string, string>? DiagnosticEvidence = null)
{
    internal static WarmPrefixCompactionOutcome NotNeeded { get; } =
        new(false, null, 0, 0, null, WarmPrefixCompactionDisposition.NotNeeded);

    /// <summary>是否为「软维护延期」：调用方据此计入推迟计数，且不得当作压缩完成。</summary>
    internal bool DeferredSoft => Disposition == WarmPrefixCompactionDisposition.DeferredSoft;
}
