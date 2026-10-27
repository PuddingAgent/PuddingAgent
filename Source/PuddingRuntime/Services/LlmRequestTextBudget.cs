using System.Text.Encodings.Web;
using System.Text.Json;
using PuddingCode.Diagnostics;
using PuddingCode.Models;
using PuddingCode.Platform;

namespace PuddingRuntime.Services;

/// <summary>
/// 独立于 token 窗口的文本上传预算（客户端的保守运行预算，不是服务商 API 上限）。
/// 统计正文、思考、续传状态及工具定义；图片二进制仍由视觉预检与 Files 上传预算管理。
/// 2026-10-23：预算随真实输入窗口伸缩 —— 固定 512 KiB 在 1M 窗口下会被固定前缀（系统提示 + 工具定义 +
/// 技能目录 ≈ 506 KB）占满 98.6%，历史只剩约 6 KB 余量，于是每轮都触发压缩；而压缩调用缓存命中≈0
/// 且会作废整段前缀缓存 ⇒ 既白花钱又把可用上下文越压越小。
/// </summary>
internal static class LlmRequestTextBudget
{
    /// <summary>预算下限（旧版唯一固定值）：窗口未知或被压到很小时仍以它为基线。</summary>
    internal const int MinimumTextBudgetBytes = 512 * 1024;

    /// <summary>
    /// 字节/Token 折算系数：CJK 约 3 B/token，英文/代码约 4~4.5 B/token。
    ///
    /// 2026-10-25：**由 3 提高到 6**，依据是 12 次真实压缩的诊断记录（
    /// `D:\Data\logs\diagnostics\compaction\*.jsonl` 的 `plan.initialTextBytes / initialUsedTokens`）：
    /// 实测密度稳定在 5.8~6.6 B/token（本仓系统提示 + 工具定义 + 中文对话 + 工具结果 JSON 的实际形态）。
    ///
    /// 原来的 3 让字节网提前到 token 窗口的 28% 就触发：同批样本里 token 比例只有 0.28~0.35，
    /// 字节比例却已到 0.60~0.74——压缩完全是字节网在推，等于白丢 2/3 可用上下文，
    /// 而压缩本身又占缓存缺失 token 的六成。换算：系数 3 → 约 2.8 MB；系数 6 → 约 5.6 MB，
    /// 使字节网与 token 网在 ~56% 处大致对齐，而不是在 28% 处抢先。
    ///
    /// 取值仍偏保守（实测中位数 6.4，取 6）：字节网是“补充网”，宁可略早触线，
    /// 不可因密度估低而把报文放大到服务商侧才被拒。
    /// </summary>
    private const int ConservativeBytesPerToken = 6;
    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    internal static int ResolveLimit(RuntimeDispatchRequest request)
    {
        // 2026-10-23：口径统一。此前只有 responses 协议的 DeepSeek 路由拿字节预算，其余一律返回 0
        // （等于没有字节网）⇒ 同一段历史因路由不同被两套口径判定：主模型能上传的请求，
        // 切到 openai 兼容 / anthropic 路由就可能直接撞服务商硬拒。现在一律按窗口折算，
        // 唯一前提是「路由身份已知」（provider 或显式 endpoint）——否则无从判断服务商上限，
        // 不虚构预算，交回 token 口径守护（与历史行为一致）。
        var provider = request.CallerLlmSnapshot?.ProviderId ?? request.LlmProfile?.ProviderId;
        var hasKnownRoute = !string.IsNullOrWhiteSpace(provider)
            || !string.IsNullOrWhiteSpace(request.LlmConfig?.Endpoint);
        if (!hasKnownRoute) return 0;

        // 窗口与压缩判定同源（同一个 ResolveEffectiveInputLimit，含 reply 预留与 safety buffer），
        // 避免字节预算和 token 触发线各自走一套口径。
        var inputTokens = LlmRequestBudgetGuard.ResolveEffectiveInputLimit(
            request.LlmConfig, workUnitInputCapacity: request.UsageBudget?.MaxInputTokens);
        // 窗口未知（int.MaxValue）或不可用 ⇒ 不虚构缩放值，退回 512 KiB 下限（与历史行为一致）。
        if (inputTokens <= 0 || inputTokens == int.MaxValue) return MinimumTextBudgetBytes;
        var scaled = (long)inputTokens * ConservativeBytesPerToken;
        return (int)Math.Clamp(scaled, MinimumTextBudgetBytes, int.MaxValue);
    }

    internal static long Measure(IReadOnlyList<ChatMessage> messages, IReadOnlyList<LlmToolDefinition>? tools)
        => MeasureMessages(messages) + MeasureTools(tools) + 1024; // request envelope reserve

    internal static long MeasureTools(IReadOnlyList<LlmToolDefinition>? tools)
        => JsonSerializer.SerializeToUtf8Bytes(tools ?? [], Json).LongLength;

    /// <summary>单个工具定义的序列化字节数（与 <see cref="MeasureTools"/> 同序列化口径）。</summary>
    internal static long MeasureTool(LlmToolDefinition tool)
        => JsonSerializer.SerializeToUtf8Bytes(tool, Json).LongLength;

    private static long MeasureMessages(IReadOnlyList<ChatMessage> messages)
        => messages.Sum(MeasureMessage);

    internal static long MeasureSummary(string text) => JsonSerializer.SerializeToUtf8Bytes(text, Json).LongLength;

    internal static long MeasureMessage(ChatMessage message)
        => JsonSerializer.SerializeToUtf8Bytes(new
        {
            message.Role,
            content = ChatMessageMultimodalNormalizer.GetEffectiveContentParts(message) is { Count: > 0 } parts
                ? string.Join("\n", parts.OfType<LlmTextPart>().Select(part => part.Text)) : message.Content,
            message.ToolCallId, message.ToolName, message.ToolCalls, message.ReasoningContent,
            message.ContinuationState,
        }, Json).LongLength;

    internal static bool Exceeds(RuntimeDispatchRequest request, IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<LlmToolDefinition> tools)
        => ResolveLimit(request) is > 0 and var limit && Measure(messages, tools) > limit;

    internal static Exception? Validate(RuntimeDispatchRequest request, IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<LlmToolDefinition> tools, string? compactionFailureReason = null,
        IReadOnlyDictionary<string, string>? compactionEvidence = null, bool contextProtectionRequired = false)
    {
        var limit = ResolveLimit(request);
        if (limit <= 0 && !contextProtectionRequired) return null;
        var bytes = Measure(messages, tools);
        if (bytes <= limit && !contextProtectionRequired) return null;
        var message = contextProtectionRequired
            ? "上下文已达到配置的压缩阈值，但整理尚未完成；已暂停模型请求，原始会话仍完整保留。"
            : "上下文整理未能将请求缩小到上传预算，已停止发送；原始会话仍完整保留。";
        var evidence = new Dictionary<string, string>
        {
            ["estimated_request_text_bytes"] = bytes.ToString(),
            ["request_text_budget_bytes"] = limit.ToString(),
            ["request_text_budget_exceeded"] = (limit > 0 && bytes > limit).ToString().ToLowerInvariant(),
            ["compaction_protection_required"] = contextProtectionRequired.ToString().ToLowerInvariant(),
            ["tool_definition_bytes"] = MeasureTools(tools).ToString(),
            ["system_message_bytes"] = MeasureMessages(messages.Where(m => m.Role == ChatRole.System).ToArray()).ToString(),
            ["compaction_reason"] = compactionFailureReason ?? "no_applicable_compaction_span",
            ["message_count"] = messages.Count.ToString(),
            ["tool_count"] = tools.Count.ToString(),
            ["dispatch_count"] = "0",
            ["history_preserved"] = "true",
        };
        if (compactionEvidence is not null)
            foreach (var pair in compactionEvidence)
                evidence.TryAdd(pair.Key, pair.Value);
        return LlmFailureDiagnostics.Attach(new InvalidOperationException(message), new DiagnosticCause
        {
            Code = DiagnosticCauseCode.ContextCompactionRequired,
            Category = "local", Phase = DiagnosticPhaseKind.Build, Retryable = false,
            UserMessage = message,
            RemediationHint = "重试会重新尝试压缩；若仍失败，请检查本轮压缩日志及工具定义、系统提示词或受保护消息的体积。",
            Rule = contextProtectionRequired ? "configured_compaction_threshold" : "request_text_budget", Evidence = evidence,
        });
    }
}
