using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using PuddingCode.Models;
using PuddingCode.Runtime;
using PuddingCode.Platform;

namespace PuddingRuntime.Services;

public sealed record LlmRequestBudgetResult(
    IReadOnlyList<ChatMessage> Messages,
    ContextUsageSnapshot Snapshot,
    int EffectiveInputLimit,
    int RemovedMessageCount);

/// <summary>软压缩结果：携带压缩前估算，供运行事件与日志归档。</summary>
public sealed record LlmSoftCompactionResult(
    IReadOnlyList<ChatMessage> Messages,
    ContextUsageSnapshot Snapshot,
    int EffectiveInputLimit,
    bool Compacted,
    int RemovedMessageCount,
    int InitialUsedTokens,
    int InitialMessageCount);

/// <summary>
/// 超限请求的「不可裁剪地板」分解（原始估算口径：不含 provider 校准比）。
/// 目的：把不可归因的硬悬崖变成可归因的失败——到底是 system 提示词、工具定义、
/// 受保护尾部，还是仍可裁剪的候选历史把请求撑爆的。
/// </summary>
public sealed record LlmInputBudgetBreakdown(
    int SystemMessageTokens,
    int ToolDefinitionTokens,
    int ProtectedTailTokens,
    int RemovableCandidateTokens);

public sealed class LlmInputBudgetExceededException : InvalidOperationException
{
    public LlmInputBudgetExceededException(int estimatedTokens, int effectiveInputLimit)
        : this(estimatedTokens, effectiveInputLimit, breakdown: null)
    {
    }

    public LlmInputBudgetExceededException(
        int estimatedTokens,
        int effectiveInputLimit,
        LlmInputBudgetBreakdown? breakdown)
        : base(BuildMessage(estimatedTokens, effectiveInputLimit, breakdown))
    {
        EstimatedTokens = estimatedTokens;
        EffectiveInputLimit = effectiveInputLimit;
        Breakdown = breakdown;
    }

    public int EstimatedTokens { get; }
    public int EffectiveInputLimit { get; }

    /// <summary>不可裁剪地板分解；null 表示调用方未提供分解（两参构造保持既有文案不变）。</summary>
    public LlmInputBudgetBreakdown? Breakdown { get; }

    private static string BuildMessage(
        int estimatedTokens,
        int effectiveInputLimit,
        LlmInputBudgetBreakdown? breakdown)
    {
        // 既有前缀保持不变（现有日志/告警文案依赖它），只在其后追加分解信息。
        var message = $"LLM request input is too large after history trimming: estimated={estimatedTokens}, limit={effectiveInputLimit}.";
        if (breakdown is null)
            return message;

        return message
            + " breakdown(rawEstimate):"
            + $" systemMessages={breakdown.SystemMessageTokens},"
            + $" toolDefinitions={breakdown.ToolDefinitionTokens},"
            + $" protectedTail={breakdown.ProtectedTailTokens},"
            + $" removableCandidates={breakdown.RemovableCandidateTokens}"
            + " (protectedTail = messages history trimming may never remove).";
    }
}

/// <summary>
/// Applies the provider/model input budget to the final message and tool payload
/// immediately before an LLM request is sent.
/// </summary>
public static partial class LlmRequestBudgetGuard
{
    public const int DefaultSafetyBufferTokens = 1_024;
    private const int ProtectedTailMessages = 8;

    [GeneratedRegex(@"Range\s+of\s+input\s+length\s+should\s+be\s+\[\s*1\s*,\s*(?<max>[0-9_]+)\s*\]", RegexOptions.IgnoreCase)]
    private static partial Regex InputLengthRangeRegex();

    public static int ResolveEffectiveInputLimit(
        LlmConfig? config,
        int safetyBufferTokens = DefaultSafetyBufferTokens,
        long? workUnitInputCapacity = null)
    {
        var providerLimit = config?.MaxInputTokens is > 0
            ? config.MaxInputTokens.Value
            : int.MaxValue;
        if (workUnitInputCapacity is > 0)
            providerLimit = Math.Min(providerLimit, (int)Math.Min(int.MaxValue, workUnitInputCapacity.Value));
        if (config?.MaxContextTokens is not > 0)
            return providerLimit;

        var contextWindow = config.MaxContextTokens.Value;
        var requestedOutput = Math.Max(0, config.MaxOutputTokens ?? 0);
        var safetyBuffer = Math.Max(0, safetyBufferTokens);
        var contextDerivedLimit = Math.Max(1, contextWindow - requestedOutput - safetyBuffer);
        return Math.Max(1, Math.Min(contextDerivedLimit, providerLimit));
    }

    /// <param name="evictOversizedToolPayloads">
    /// 超限时是否先做一次有界「载荷驱逐」（默认开启）：只截断 <see cref="ChatRole.Tool"/> 的超大载荷，
    /// 不动 System / User 消息、也不删除任何消息。关闭时行为回到旧的硬悬崖（直接抛错）。
    /// </param>
    /// <param name="logger">可选：驱逐真正发生时写一条 Warning 日志（条数 + 回收 token 估算）。</param>
    public static LlmRequestBudgetResult Prepare(
        ContextUsageSnapshotStore usageStore,
        string sessionId,
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<LlmToolDefinition>? tools,
        LlmConfig? config,
        int safetyBufferTokens = DefaultSafetyBufferTokens,
        long? workUnitInputCapacity = null,
        bool evictOversizedToolPayloads = true,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(usageStore);

        var working = messages.ToList();
        var initialCount = working.Count;
        var effectiveInputLimit = ResolveEffectiveInputLimit(config, safetyBufferTokens, workUnitInputCapacity);
        var snapshot = usageStore.CaptureLlmRequest(
            sessionId,
            working,
            tools,
            config?.ModelId);

        while (snapshot.UsedTokens > effectiveInputLimit && RemoveOldestConversationUnit(working))
        {
            working = LlmMessageSequenceNormalizer.Normalize(working).Messages.ToList();
            snapshot = usageStore.CaptureLlmRequest(
                sessionId,
                working,
                tools,
                config?.ModelId);
        }

        // 超限时不立刻抛错：先做一次（仅一次）有界「载荷驱逐」——
        // 把 Role=Tool 的超大载荷换成「头部摘要 + 截断标记」，给本次请求一次可挽救的机会。
        // 只动载荷、不动角色、不删消息，因此 RemovedMessageCount 语义不受影响。
        if (snapshot.UsedTokens > effectiveInputLimit && evictOversizedToolPayloads)
        {
            var evictedPayloadCount = EvictOversizedToolPayloads(working, sessionId);
            if (evictedPayloadCount > 0)
            {
                // 刻意不调用 LlmMessageSequenceNormalizer：载荷驱逐不增不删消息，
                // 规范化反而会把「没有配对 assistant tool-call 的 Tool 消息」当孤儿丢弃。
                var reMeasured = usageStore.CaptureLlmRequest(
                    sessionId,
                    working,
                    tools,
                    config?.ModelId);
                logger?.LogWarning(
                    "[LlmRequestBudgetGuard:PayloadEviction] session={SessionId} evictedToolPayloads={Evicted} reclaimedTokens={Reclaimed} estimatedBefore={EstimatedBefore} estimatedAfter={EstimatedAfter} limit={Limit}",
                    sessionId,
                    evictedPayloadCount,
                    Math.Max(0, snapshot.UsedTokens - reMeasured.UsedTokens),
                    snapshot.UsedTokens,
                    reMeasured.UsedTokens,
                    effectiveInputLimit);
                snapshot = reMeasured;
            }
        }

        // 驱逐后仍超限 ⇒ fail-closed 保持不变，但异常自带「不可裁剪地板」分解。
        if (snapshot.UsedTokens > effectiveInputLimit)
            throw new LlmInputBudgetExceededException(
                snapshot.UsedTokens,
                effectiveInputLimit,
                BuildBreakdown(working, sessionId, snapshot, config?.ModelId));

        return new LlmRequestBudgetResult(
            working,
            snapshot,
            effectiveInputLimit,
            Math.Max(0, initialCount - working.Count));
    }

    /// <summary>
    /// 轮内软压缩：估算输入达到 triggerRatio × 有效上限即按会话单元驱逐最旧历史，
    /// 压到 targetRatio × 有效上限为止。与 <see cref="Prepare"/> 的硬悬崖不同，
    /// 本方法从不抛异常——压缩不动（如只剩受保护尾部）时原样返回。
    /// 2026-08-22 能耗修复：此前子代理路径只有硬悬崖（约 61 万 tokens），
    /// 上下文被养满才一次性裁剪，每轮重放 30-60 万 tokens。
    /// </summary>
    public static LlmSoftCompactionResult PrepareSoftCompaction(
        ContextUsageSnapshotStore usageStore,
        string sessionId,
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<LlmToolDefinition>? tools,
        LlmConfig? config,
        double triggerRatio = ContextCompactionDefaults.TriggerRatio,
        double targetRatio = 0.5,
        int safetyBufferTokens = DefaultSafetyBufferTokens)
    {
        ArgumentNullException.ThrowIfNull(usageStore);

        var effectiveInputLimit = ResolveEffectiveInputLimit(config, safetyBufferTokens);
        var working = messages.ToList();
        var snapshot = usageStore.CaptureLlmRequest(
            sessionId,
            working,
            tools,
            config?.ModelId);
        var initialUsedTokens = snapshot.UsedTokens;

        var trigger = effectiveInputLimit * Math.Clamp(triggerRatio, 0.1, 1.0);
        if (snapshot.UsedTokens < trigger)
        {
            return new LlmSoftCompactionResult(
                working,
                snapshot,
                effectiveInputLimit,
                Compacted: false,
                RemovedMessageCount: 0,
                InitialUsedTokens: initialUsedTokens,
                InitialMessageCount: messages.Count);
        }

        var target = effectiveInputLimit * Math.Clamp(targetRatio, 0.05, Math.Clamp(triggerRatio, 0.05, 1.0));
        while (snapshot.UsedTokens > target && RemoveOldestConversationUnit(working))
        {
            working = LlmMessageSequenceNormalizer.Normalize(working).Messages.ToList();
            snapshot = usageStore.CaptureLlmRequest(
                sessionId,
                working,
                tools,
                config?.ModelId);
        }

        var removed = Math.Max(0, messages.Count - working.Count);
        return new LlmSoftCompactionResult(
            working,
            snapshot,
            effectiveInputLimit,
            Compacted: removed > 0,
            RemovedMessageCount: removed,
            InitialUsedTokens: initialUsedTokens,
            InitialMessageCount: messages.Count);
    }

    public static bool TryGetProviderMaxInputTokens(Exception exception, out int maxInputTokens)
        => TryGetProviderMaxInputTokens(exception.Message, out maxInputTokens);

    public static bool TryGetProviderMaxInputTokens(string? error, out int maxInputTokens)
    {
        maxInputTokens = 0;
        if (string.IsNullOrWhiteSpace(error))
            return false;

        var match = InputLengthRangeRegex().Match(error);
        return match.Success
            && int.TryParse(
                match.Groups["max"].Value.Replace("_", string.Empty, StringComparison.Ordinal),
                out maxInputTokens)
            && maxInputTokens > 0;
    }

    private static bool RemoveOldestConversationUnit(List<ChatMessage> messages)
    {
        var firstRemovable = messages.FindIndex(message => message.Role != ChatRole.System);
        if (firstRemovable < 0)
            return false;

        var protectedTailStart = ComputeProtectedTailStart(messages, firstRemovable);
        if (firstRemovable >= protectedTailStart)
            return false;

        var removeEnd = firstRemovable + 1;
        while (removeEnd < messages.Count
            && messages[removeEnd].Role is not (ChatRole.User or ChatRole.System))
            removeEnd++;

        // Remove whole conversation units only. A tail boundary inside a tool
        // exchange cannot license deleting its call while retaining its result.
        if (removeEnd > protectedTailStart)
            return false;

        messages.RemoveRange(firstRemovable, Math.Max(1, removeEnd - firstRemovable));
        return true;
    }

    /// <summary>
    /// 受保护尾部起点：最后 <see cref="ProtectedTailMessages"/> 条消息，以及当前轮最后一条 User 消息
    /// 及其之后的内容，都不得被裁剪。与 <see cref="RemoveOldestConversationUnit"/> 的边界判定同源，
    /// 也用于超限异常的「不可裁剪地板」分解。
    /// </summary>
    private static int ComputeProtectedTailStart(List<ChatMessage> messages, int firstRemovable)
    {
        var protectedTailStart = Math.Max(firstRemovable, messages.Count - ProtectedTailMessages);
        var currentUser = messages.FindLastIndex(message => message.Role == ChatRole.User);
        if (currentUser >= 0)
            protectedTailStart = Math.Min(protectedTailStart, currentUser);
        return protectedTailStart;
    }

    /// <summary>
    /// 有界「载荷驱逐」：只截断 <see cref="ChatRole.Tool"/> 消息中超过 <see cref="MaxVerbatimToolPayloadBytes"/> 的正文，
    /// 替换为「头部摘要 + 截断标记」。
    /// 硬约束：绝不改写 System 消息、绝不改写任何 User 消息（含当前轮最后一条 User）、绝不删除消息；
    /// 被驱逐的只是载荷正文，消息本身（含 ToolCallId/ToolName 配对标识）仍在原位。
    /// </summary>
    /// <returns>被驱逐载荷的消息条数（不是被移除的消息条数）。</returns>
    private static int EvictOversizedToolPayloads(List<ChatMessage> messages, string sessionId)
    {
        var maxBytes = MaxVerbatimToolPayloadBytes;
        if (maxBytes <= 0)
            return 0;

        var evicted = 0;
        for (var i = 0; i < messages.Count; i++)
        {
            var message = messages[i];
            if (message.Role != ChatRole.Tool)
                continue;

            var originalBytes = EstimateToolPayloadBytes(message);
            if (originalBytes <= maxBytes)
                continue;

            messages[i] = message with
            {
                Content = BuildPayloadTruncatedContent(message, sessionId, originalBytes, maxBytes),
            };
            evicted++;
        }

        return evicted;
    }

    /// <summary>
    /// 单条 Tool 消息的载荷尺寸（UTF-8 字节）：正文（tool_result/tool_output 的落点）+ 工具名/调用 id 元数据。
    /// </summary>
    private static int EstimateToolPayloadBytes(ChatMessage message)
        => Encoding.UTF8.GetByteCount(message.Content ?? string.Empty)
            + Encoding.UTF8.GetByteCount(message.ToolName ?? string.Empty)
            + Encoding.UTF8.GetByteCount(message.ToolCallId ?? string.Empty);

    /// <summary>
    /// 构造「头部摘要 + 截断标记」。文案与既有约定一致（见
    /// ContextCompactionService.BuildVerbatimTruncatedContent：标记注明原始大小，并提示完整内容
    /// 可在会话原始事件流 conversation_events 中查证）。那里的实现是 private 且作用于 MessageEntity，
    /// 本静态工具类无法复用，故此做最小本地等价实现。
    /// </summary>
    private static string BuildPayloadTruncatedContent(
        ChatMessage message,
        string sessionId,
        int originalBytes,
        int maxBytes)
    {
        var original = message.Content ?? string.Empty;
        var headChars = Math.Clamp(maxBytes / 4, 256, 4096);
        var head = original.Length <= headChars ? original : original[..headChars];
        var nl = Environment.NewLine;
        return head
            + $"{nl}{nl}--- [ContextCompaction 截断标记] 该消息原始内容过大（原始大小 {originalBytes} 字节），"
            + "未原样保留全文，完整内容仍可在会话原始事件流 conversation_events 中查证"
            + $"（session={sessionId}, tool={message.ToolName ?? "unknown"}, toolCallId={message.ToolCallId ?? "unknown"}）。---";
    }

    /// <summary>
    /// 计算超限请求的「不可裁剪地板」分解：system 消息 / 工具定义 / 受保护尾部 / 其余仍可裁剪候选。
    /// 工具定义 token 直接取最终快照的 <see cref="ContextUsageSnapshot.ToolDefinitionTokens"/>（快照已有该字段，
    /// 无需降级为「system/尾部/其余」三项）。
    /// 受保护尾部用一个**临时**快照 store 探针测量：CaptureLlmRequest 会把结果写回自身缓存，
    /// 用独立实例测量可避免污染真实会话的 outbound 快照。探针口径为未校准的原始估算。
    /// </summary>
    private static LlmInputBudgetBreakdown BuildBreakdown(
        List<ChatMessage> messages,
        string sessionId,
        ContextUsageSnapshot snapshot,
        string? modelId)
    {
        var protectedTailTokens = 0;
        var firstRemovable = messages.FindIndex(message => message.Role != ChatRole.System);
        if (firstRemovable >= 0)
        {
            var protectedTailStart = ComputeProtectedTailStart(messages, firstRemovable);
            var tail = messages
                .Skip(protectedTailStart)
                .Where(message => message.Role != ChatRole.System)
                .ToList();
            if (tail.Count > 0)
            {
                var probe = new ContextUsageSnapshotStore();
                protectedTailTokens = probe.CaptureLlmRequest(sessionId, tail, tools: null, modelId).UsedTokens;
            }
        }

        var systemTokens = snapshot.SystemMessageTokens;
        var toolDefinitionTokens = snapshot.ToolDefinitionTokens;
        var removableCandidateTokens = Math.Max(
            0,
            snapshot.RawEstimatedTokens - systemTokens - toolDefinitionTokens - protectedTailTokens);

        return new LlmInputBudgetBreakdown(
            systemTokens,
            toolDefinitionTokens,
            protectedTailTokens,
            removableCandidateTokens);
    }

    /// <summary>
    /// 单条 Tool 载荷的「原样保留」上限（字节）。直接引用仓内既有约定
    /// <see cref="ContextCompactionOptions.MaxVerbatimMessageBytes"/> 的默认值（16*1024），
    /// 保证与压缩侧 ApplyVerbatimSizeEviction 使用同一阈值口径；本静态工具类拿不到 Options 实例，
    /// 故在类型初始化时读取默认值（0/负数 ⇒ 禁用驱逐，与压缩侧语义一致）。
    /// </summary>
    private static readonly int MaxVerbatimToolPayloadBytes =
        new ContextCompactionOptions().MaxVerbatimMessageBytes;
}
