using System.Text.Encodings.Web;
using System.Text.Json;
using PuddingCode.Diagnostics;
using PuddingCode.Models;
using PuddingCode.Platform;

namespace PuddingRuntime.Services;

/// <summary>
/// 独立于 token 窗口的文本上传预算。512 KiB 是客户端的保守运行预算，不是服务商 API 上限。
/// 统计正文、思考、续传状态及工具定义；图片二进制仍由视觉预检与 Files 上传预算管理。
/// </summary>
internal static class LlmRequestTextBudget
{
    internal const int DeepSeekResponsesLimit = 512 * 1024;
    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    internal static int ResolveLimit(RuntimeDispatchRequest request)
    {
        var protocol = request.CallerLlmSnapshot?.Protocol;
        if (!string.IsNullOrEmpty(protocol) && !protocol.Equals("responses", StringComparison.OrdinalIgnoreCase))
            return 0;
        var provider = request.CallerLlmSnapshot?.ProviderId ?? request.LlmProfile?.ProviderId;
        var endpoint = request.LlmConfig?.Endpoint;
        var isDeepSeek = string.Equals(provider, "deepseek", StringComparison.OrdinalIgnoreCase)
            || (Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
                && uri.Host.Equals("api.deepseek.com", StringComparison.OrdinalIgnoreCase));
        return isDeepSeek ? DeepSeekResponsesLimit : 0;
    }

    internal static long Measure(IReadOnlyList<ChatMessage> messages, IReadOnlyList<LlmToolDefinition>? tools)
        => MeasureMessages(messages) + MeasureTools(tools) + 1024; // request envelope reserve

    internal static long MeasureTools(IReadOnlyList<LlmToolDefinition>? tools)
        => JsonSerializer.SerializeToUtf8Bytes(tools ?? [], Json).LongLength;

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
