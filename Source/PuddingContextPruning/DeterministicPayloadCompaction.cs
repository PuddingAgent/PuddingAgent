using PuddingCode.Models;

namespace PuddingContextPruning;

/// <summary>
/// 零 LLM、确定性、无副作用的载荷剪枝：在「花钱让模型总结」之前，先把明显冗余的字节拿掉。
///
/// 三条策略按固定顺序执行（顺序不可交换 —— 去重会让被去重的正文变短，从而使它们
/// 不再满足摘要阈值，这是刻意行为，避免对同一段文本重复改写）：
/// <list type="number">
///   <item>字节相同的工具结果去重（保留最新一条全文）；</item>
///   <item>非尾部超长工具结果 → 头尾摘要；</item>
///   <item>非尾部超长工具调用参数 → 截断。</item>
/// </list>
///
/// 不变量：受保护尾部按引用原样返回；tool_call / tool_result 配对永不被拆散；
/// 同一输入两次调用逐字相同；永不修改入参列表及其元素。
/// </summary>
public static class DeterministicPayloadCompaction
{
    /// <summary>所有生成文本的统一前缀，便于在会话事件流里反查「哪些内容是剪枝产物」。</summary>
    private const string MarkerPrefix = "[deterministic-prune] ";

    private const string UnknownValue = "unknown";

    /// <summary>
    /// 尝试剪枝。<paramref name="messages"/> 永不被修改；返回的
    /// <see cref="PruneOutcome.Messages"/> 在没有改动时**就是入参实例本身**。
    /// </summary>
    /// <param name="messages">待处理的消息列表。</param>
    /// <param name="options">配置；<c>null</c> 等价于默认值。</param>
    /// <param name="tokenCounter">token 估算器；<c>null</c> 用 <see cref="CharacterRatioTokenCounter.Default"/>。</param>
    /// <exception cref="ArgumentNullException"><paramref name="messages"/> 为 <c>null</c>。</exception>
    /// <exception cref="ArgumentOutOfRangeException">配置非法（由 <see cref="DeterministicPruneOptions.Validate"/> 抛出）。</exception>
    public static PruneOutcome TryApply(
        IReadOnlyList<ChatMessage> messages,
        DeterministicPruneOptions? options = null,
        ITextTokenCounter? tokenCounter = null)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var opts = options ?? new DeterministicPruneOptions();
        opts.Validate();
        var counter = tokenCounter ?? CharacterRatioTokenCounter.Default;

        // 短路径按「无操作原因」的优先级依次判定，先判的便宜、后判的贵。
        if (!opts.Enabled)
        {
            return NoOp(messages, PruneNoOpReason.Disabled);
        }

        if (messages.Count == 0)
        {
            return NoOp(messages, PruneNoOpReason.NoMessages);
        }

        var tailStart = ResolveTailStart(messages, opts.ProtectedTailMessages);
        if (tailStart == 0)
        {
            return NoOp(messages, PruneNoOpReason.TailOnly);
        }

        // 副本只承载「槽位替换」，未替换的槽位仍持有原元素引用（调用方可按引用判等）。
        var working = new List<ChatMessage>(messages);
        var deduplicated = DeduplicateToolResults(working, tailStart, opts);
        var summarized = SummarizeToolResults(working, tailStart, opts);
        var truncatedArguments = TruncateToolArguments(working, tailStart, opts);

        if (deduplicated == 0 && summarized == 0 && truncatedArguments == 0)
        {
            return NoOp(messages, PruneNoOpReason.NothingEligible);
        }

        var before = PruneTokenMath.CountMessages(messages, counter);
        var after = PruneTokenMath.CountMessages(working, counter);
        var reclaimed = before - after;

        // 准入地板（迟滞）：省得太少就不值当 —— 重写历史消息会让下游 warm prefix 失效。
        if (reclaimed < opts.MinReclaimTokens || reclaimed < before * opts.MinReclaimRatio)
        {
            return new PruneOutcome(
                messages,
                new PruneStats(deduplicated, summarized, truncatedArguments, before, before),
                PruneNoOpReason.ReclaimBelowMinimum);
        }

        return new PruneOutcome(
            working,
            new PruneStats(deduplicated, summarized, truncatedArguments, before, after),
            PruneNoOpReason.None);
    }

    private static PruneOutcome NoOp(IReadOnlyList<ChatMessage> messages, PruneNoOpReason reason)
        => new(messages, PruneStats.Zero, reason);

    /// <summary>
    /// 计算受保护尾部的起始下标。边界若正好落在 tool 结果上，向前回退到它的 assistant 发起方，
    /// 否则「保护了结果却没保护调用参数」会让配对在半截上被改坏。
    /// </summary>
    private static int ResolveTailStart(IReadOnlyList<ChatMessage> messages, int protectedTailMessages)
    {
        if (protectedTailMessages <= 0)
        {
            return messages.Count; // 无受保护区：全部消息都是候选
        }

        var tailStart = Math.Max(0, messages.Count - protectedTailMessages);
        while (tailStart > 0
               && tailStart < messages.Count
               && messages[tailStart].Role == ChatRole.Tool)
        {
            tailStart--;
        }

        return tailStart;
    }

    /// <summary>可参与工具结果类策略的消息：Tool 角色、非上下文摘要、非多模态载荷。</summary>
    private static bool IsPrunableToolResult(ChatMessage message)
        => message.Role == ChatRole.Tool
           && !message.IsContextSummary
           && message.ContentParts is null;

    /// <summary>策略一：分组键 = (ToolName, Content)，同组保留 index 最大的一条全文。</summary>
    private static int DeduplicateToolResults(
        List<ChatMessage> working,
        int tailStart,
        DeterministicPruneOptions opts)
    {
        var groups = new Dictionary<(string ToolName, string Content), List<int>>();
        for (var index = 0; index < tailStart; index++)
        {
            var message = working[index];
            if (!IsPrunableToolResult(message))
            {
                continue;
            }

            var content = message.Content;
            if (content is null || content.Length < opts.MinDuplicateChars)
            {
                continue;
            }

            var key = (message.ToolName ?? string.Empty, content);
            if (!groups.TryGetValue(key, out var bucket))
            {
                bucket = new List<int>();
                groups[key] = bucket;
            }

            bucket.Add(index); // 升序填充 ⇒ bucket[^1] 即最新一条
        }

        var replaced = 0;
        foreach (var bucket in groups.Values)
        {
            if (bucket.Count < 2)
            {
                continue;
            }

            var keptIndex = bucket[^1];
            var keptId = working[keptIndex].ToolCallId ?? UnknownValue;

            for (var slot = 0; slot < bucket.Count - 1; slot++)
            {
                var index = bucket[slot];
                var original = working[index];
                var originalChars = original.Content?.Length ?? 0;
                working[index] = original with
                {
                    // ToolCallId / ToolName / ReasoningContent 等字段保持原样，配对不能断。
                    Content = $"{MarkerPrefix}与 toolCallId={keptId} 的工具结果内容完全相同（{originalChars} 字符），已省略重复正文。",
                };
                replaced++;
            }
        }

        return replaced;
    }

    /// <summary>策略二：非尾部超长工具结果 → 头 + 标记 + 尾。</summary>
    private static int SummarizeToolResults(
        List<ChatMessage> working,
        int tailStart,
        DeterministicPruneOptions opts)
    {
        var replaced = 0;
        for (var index = 0; index < tailStart; index++)
        {
            var message = working[index];
            if (!IsPrunableToolResult(message))
            {
                continue;
            }

            var content = message.Content;
            if (content is null || content.Length < opts.MinToolResultChars)
            {
                continue;
            }

            var marker =
                $"{MarkerPrefix}工具结果已确定性摘要：原文 {content.Length} 字符，" +
                $"完整内容仍可在会话原始事件流 conversation_events 中查证" +
                $"（tool={message.ToolName ?? UnknownValue}, toolCallId={message.ToolCallId ?? UnknownValue}）。";

            // 只有确实变短才替换；否则「摘要」会变成「加长」，那是静默退化。
            if (content.Length <= opts.ToolResultHeadChars + opts.ToolResultTailChars + marker.Length)
            {
                continue;
            }

            var head = content[..opts.ToolResultHeadChars];
            var tail = content[^opts.ToolResultTailChars..];
            working[index] = message with { Content = head + "\n\n" + marker + "\n\n" + tail };
            replaced++;
        }

        return replaced;
    }

    /// <summary>策略三：非尾部 Assistant 的超长工具调用参数 → 保留 Id/Name，只截断参数。</summary>
    private static int TruncateToolArguments(
        List<ChatMessage> working,
        int tailStart,
        DeterministicPruneOptions opts)
    {
        var replaced = 0;
        for (var index = 0; index < tailStart; index++)
        {
            var message = working[index];
            if (message.Role != ChatRole.Assistant
                || message.IsContextSummary
                || message.ToolCalls is not { Count: > 0 } toolCalls)
            {
                continue;
            }

            var hasOverLimit = false;
            foreach (var call in toolCalls)
            {
                if (IsOverLimitArguments(call, opts))
                {
                    hasOverLimit = true;
                    break;
                }
            }

            if (!hasOverLimit)
            {
                continue;
            }

            var rebuilt = new List<ToolCall>(toolCalls.Count);
            foreach (var call in toolCalls)
            {
                rebuilt.Add(IsOverLimitArguments(call, opts) ? TruncateArguments(call, opts) : call);
            }

            working[index] = message with { ToolCalls = rebuilt };
            replaced++;
        }

        return replaced;
    }

    private static bool IsOverLimitArguments(ToolCall call, DeterministicPruneOptions opts)
        => call.ArgumentsJson is not null && call.ArgumentsJson.Length > opts.MinToolArgumentChars;

    private static ToolCall TruncateArguments(ToolCall call, DeterministicPruneOptions opts)
    {
        var argumentsJson = call.ArgumentsJson!;
        var head = argumentsJson[..opts.ToolArgumentHeadChars];
        var marker = $"{MarkerPrefix}工具参数已截断：原文 {argumentsJson.Length} 字符。";
        return call with { ArgumentsJson = head + "\n\n" + marker };
    }
}
