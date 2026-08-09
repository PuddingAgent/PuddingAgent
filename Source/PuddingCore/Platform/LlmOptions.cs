using System.Collections.Concurrent;
using System.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.ML.Tokenizers;
using PuddingCode.Models;
using PuddingCode.Runtime;

namespace PuddingCode.Platform.Options
{
    /// <summary>LLM 连接配置（OpenAI-compatible）。</summary>
    public sealed record LlmOptions(
        string Endpoint,
        string ApiKey,
        string Model,
        double? Temperature = null,
        int? MaxTokens = null,
        string? ReasoningEffort = null,
        string? ThinkingMode = null);
}

namespace PuddingCode.Platform
{
    /// <summary>上下文组装快照存储（线程安全，供调试端点读取）。</summary>
        public sealed class ContextAssemblyStore
    {
        private readonly ConcurrentDictionary<string, ContextAssemblySnapshot> _snapshots = new();
        private const int MaxSnapshots = 10;

        public void Set(ContextAssemblySnapshot snapshot)
        {
            if (string.IsNullOrWhiteSpace(snapshot.SessionId))
                return;
            _snapshots[snapshot.SessionId] = snapshot;

            // LRU eviction: keep at most MaxSnapshots, evict oldest by AssembledAt
            if (_snapshots.Count > MaxSnapshots)
            {
                var oldest = _snapshots
                    .OrderBy(kv => kv.Value.AssembledAt)
                    .FirstOrDefault();
                if (oldest.Key is not null)
                    _snapshots.TryRemove(oldest.Key, out _);
            }
        }

        public bool TryGet(string sessionId, out ContextAssemblySnapshot? snapshot)
        {
            var ok = _snapshots.TryGetValue(sessionId, out var found);
            snapshot = found;
            return ok;
        }
    }

    /// <summary>上下文组装诊断快照。</summary>
    public class ContextAssemblySnapshot
    {
        public string SessionId { get; set; } = string.Empty;
        public DateTimeOffset AssembledAt { get; set; }
        public List<ContextLayerInfo> Layers { get; set; } = [];
                public int TotalTokens { get; set; }
        /// <summary>父代理最近 N 轮对话的剪枝消息（仅 user/assistant 正文）。</summary>
        public List<PrunedMessage> RecentMessages { get; set; } = [];
        /// <summary>静态上下文层（L0-L2）内容的 SHA-256 指纹（hex 小写）。用于 KV-cache 复用校验；未计算时为 null。</summary>
        public string? StaticLayersFingerprint { get; set; }
    }

    /// <summary>剪枝后的对话消息（移除工具调用、思维链、心跳等噪声）。</summary>
    public class PrunedMessage
    {
        public string Role { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public DateTimeOffset Timestamp { get; set; }
    }

        /// <summary>单层上下文诊断信息。</summary>
    public class ContextLayerInfo
    {
        public string LayerName { get; set; } = string.Empty;
        public int TokenCount { get; set; }
        public string ContentPreview { get; set; } = string.Empty;
        /// <summary>静态层（L0-L2）的全量文本内容。动态层为 null。</summary>
        public string? FullContent { get; set; }
        /// <summary>该层是否为静态层（L0-STATIC, L0-ENV, L0-AGENTS, L1-TOOLS, L2-SKILLS, L4-PINNED）。</summary>
        public bool IsStatic { get; set; }
    }

    /// <summary>最近一次发往 LLM 的上下文占用快照。</summary>
    public sealed class ContextUsageSnapshot
    {
        public string SessionId { get; set; } = string.Empty;
        public DateTimeOffset RecordedAt { get; set; }
        public int UsedTokens { get; set; }
        /// <summary>使用通用 Tokenizer 得到的原始请求估算，未应用 Provider 校准。</summary>
        public int RawEstimatedTokens { get; set; }
        public int MessageTokens { get; set; }
        public int ToolDefinitionTokens { get; set; }
        public int SystemMessageTokens { get; set; }
        public int HistoryMessageTokens { get; set; }
        public int MessageCount { get; set; }
        public int ToolCount { get; set; }
        /// <summary>规范化工具名称、描述和参数 schema 的稳定哈希。</summary>
        public string? ToolDefinitionHash { get; set; }
        public string Source { get; set; } = "unknown";
                public string Confidence { get; set; } = "estimated";
        /// <summary>System 消息层 gzip 压缩比（熵探针）。</summary>
        public double? SystemMessageEntropy { get; set; }
        /// <summary>历史消息层 gzip 压缩比（熵探针）。</summary>
        public double? HistoryMessageEntropy { get; set; }
        /// <summary>工具定义层 gzip 压缩比（熵探针）。</summary>
        public double? ToolDefinitionEntropy { get; set; }
        public int? ProviderPromptTokens { get; set; }
        public int? ProviderCompletionTokens { get; set; }
        public int? ProviderTotalTokens { get; set; }
        public string? ModelId { get; set; }
        public double PromptCalibrationRatio { get; set; } = 1.0;
    }

    /// <summary>
    /// 保存每个 Session 最近一次最终 LLM 请求的输入上下文估算。
    /// 该值用于保护下一轮发送，不是历史累计 token 账本。
    /// </summary>
    public sealed class ContextUsageSnapshotStore
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
        private static readonly ConcurrentDictionary<string, Tokenizer> Tokenizers = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, ContextUsageSnapshot> _snapshots = new();
        private readonly ConcurrentDictionary<string, double> _promptCalibrationRatios = new(StringComparer.OrdinalIgnoreCase);

        public void Set(ContextUsageSnapshot snapshot)
        {
            if (string.IsNullOrWhiteSpace(snapshot.SessionId))
                return;

            _snapshots[snapshot.SessionId] = snapshot;
        }

        public bool TryGet(string sessionId, out ContextUsageSnapshot? snapshot)
        {
            var ok = _snapshots.TryGetValue(sessionId, out var found);
            snapshot = found;
            return ok;
        }

        public ContextUsageSnapshot CaptureLlmRequest(
            string sessionId,
            IReadOnlyList<ChatMessage> messages,
            IReadOnlyList<LlmToolDefinition>? tools,
            string? modelId = null)
        {
                        var messageTokens = 0;
            var systemTokens = 0;
            var historyTokens = 0;
            var systemText = new StringBuilder();
            var historyText = new StringBuilder();
            foreach (var message in messages)
            {
                var content = message.Content ?? string.Empty;
                var tokenCount = CountMessageTokens(message, modelId);
                messageTokens += tokenCount;
                if (message.Role == ChatRole.System)
                {
                    systemTokens += tokenCount;
                    if (content.Length > 0)
                        systemText.AppendLine(content);
                }
                else
                {
                    historyTokens += tokenCount;
                    if (content.Length > 0)
                        historyText.AppendLine(content);
                }
            }

            var toolTokens = CountToolDefinitionTokens(tools, modelId);
            var toolText = tools is { Count: > 0 }
                ? JsonSerializer.Serialize(tools, JsonOptions)
                : null;
            var rawEstimatedTokens = Math.Max(0, messageTokens + toolTokens);
            var calibrationRatio = GetPromptCalibrationRatio(sessionId, modelId);
            var calibratedTokens = (int)Math.Min(
                int.MaxValue,
                Math.Ceiling(rawEstimatedTokens * calibrationRatio));
            var toolDefinitionHash = tools is { Count: > 0 }
                ? PrefixCacheSnapshotBuilder.Build(messages, tools).ToolSpecHash
                : null;
            var snapshot = new ContextUsageSnapshot
            {
                SessionId = sessionId,
                RecordedAt = DateTimeOffset.UtcNow,
                UsedTokens = calibratedTokens,
                RawEstimatedTokens = rawEstimatedTokens,
                MessageTokens = messageTokens,
                ToolDefinitionTokens = toolTokens,
                SystemMessageTokens = systemTokens,
                HistoryMessageTokens = historyTokens,
                MessageCount = messages.Count,
                ToolCount = tools?.Count ?? 0,
                ToolDefinitionHash = toolDefinitionHash,
                Source = calibrationRatio > 1.0001 ? "llm_request_calibrated" : "llm_request",
                Confidence = calibrationRatio > 1.0001 ? "provider_calibrated" : "estimated",
                ModelId = modelId,
                PromptCalibrationRatio = calibrationRatio,
                SystemMessageEntropy = EntropyProbe.ComputeGzipRatio(systemText.ToString()),
                HistoryMessageEntropy = EntropyProbe.ComputeGzipRatio(historyText.ToString()),
                ToolDefinitionEntropy = EntropyProbe.ComputeGzipRatio(toolText),
            };
            Set(snapshot);
            return snapshot;
        }

        public ContextUsageSnapshot RecordProviderUsage(
            string sessionId,
            TokenUsageDto usage)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return new ContextUsageSnapshot
                {
                    SessionId = string.Empty,
                    RecordedAt = DateTimeOffset.UtcNow,
                    Source = "provider_usage",
                    Confidence = "provider_reported",
                };
            }

            var existing = TryGet(sessionId, out var found) ? found : null;
            var providerPromptTokens = Math.Max(0, usage.PromptTokens ?? 0);
            var rawEstimatedTokens = existing?.RawEstimatedTokens > 0
                ? existing.RawEstimatedTokens
                : existing?.UsedTokens ?? 0;
            var calibrationRatio = existing?.PromptCalibrationRatio ?? 1.0;
            if (providerPromptTokens > 0 && rawEstimatedTokens > 0)
            {
                var observedRatio = Math.Max(1.0, providerPromptTokens / (double)rawEstimatedTokens);
                var calibrationKey = BuildCalibrationKey(sessionId, existing?.ModelId);
                calibrationRatio = _promptCalibrationRatios.AddOrUpdate(
                    calibrationKey,
                    observedRatio,
                    (_, current) => Math.Max(current, observedRatio));
            }

            // Provider TotalTokens is the best lower bound for the next request:
            // the completion becomes the next assistant-history message. Never let
            // a provider-reported value be replaced by a smaller local estimate.
            var usedTokens = Math.Max(
                existing?.UsedTokens ?? 0,
                Math.Max(0, usage.TotalTokens ?? usage.PromptTokens ?? 0));

            var snapshot = new ContextUsageSnapshot
            {
                SessionId = sessionId,
                RecordedAt = DateTimeOffset.UtcNow,
                UsedTokens = usedTokens,
                RawEstimatedTokens = rawEstimatedTokens,
                MessageTokens = existing?.MessageTokens ?? 0,
                ToolDefinitionTokens = existing?.ToolDefinitionTokens ?? 0,
                SystemMessageTokens = existing?.SystemMessageTokens ?? 0,
                HistoryMessageTokens = existing?.HistoryMessageTokens ?? 0,
                MessageCount = existing?.MessageCount ?? 0,
                ToolCount = existing?.ToolCount ?? 0,
                ToolDefinitionHash = existing?.ToolDefinitionHash,
                Source = "provider_usage",
                Confidence = "provider_reported",
                ProviderPromptTokens = usage.PromptTokens,
                ProviderCompletionTokens = usage.CompletionTokens,
                ProviderTotalTokens = usage.TotalTokens,
                ModelId = existing?.ModelId,
                PromptCalibrationRatio = calibrationRatio,
            };
            Set(snapshot);
            return snapshot;
        }

        /// <summary>
        /// Learns a conservative tokenizer lower bound from a Provider input-length rejection.
        /// The Provider did not return the actual request size, but it proved that the current
        /// request is larger than <paramref name="maxInputTokens"/>.
        /// </summary>
        public void RecordProviderInputLimitFailure(string sessionId, int maxInputTokens)
        {
            if (maxInputTokens <= 0 || !TryGet(sessionId, out var snapshot) || snapshot is null)
                return;

            var rawEstimatedTokens = snapshot.RawEstimatedTokens > 0
                ? snapshot.RawEstimatedTokens
                : snapshot.UsedTokens;
            if (rawEstimatedTokens <= 0)
                return;

            var conservativeRatio = Math.Max(
                1.05,
                ((maxInputTokens + 1d) / rawEstimatedTokens) * 1.05);
            var calibrationKey = BuildCalibrationKey(sessionId, snapshot.ModelId);
            _promptCalibrationRatios.AddOrUpdate(
                calibrationKey,
                conservativeRatio,
                (_, current) => Math.Max(current, conservativeRatio));
        }

        public double GetPromptCalibrationRatio(string sessionId, string? modelId)
        {
            var calibrationKey = BuildCalibrationKey(sessionId, modelId);
            return _promptCalibrationRatios.TryGetValue(calibrationKey, out var ratio)
                ? Math.Max(1.0, ratio)
                : 1.0;
        }

        public static int CountTokens(string? text, string? modelId = null)
        {
            if (string.IsNullOrEmpty(text))
                return 0;

            return GetTokenizer(modelId).CountTokens(text);
        }

        private static int CountToolDefinitionTokens(IReadOnlyList<LlmToolDefinition>? tools, string? modelId)
        {
            if (tools is null || tools.Count == 0)
                return 0;

            var json = JsonSerializer.Serialize(tools, JsonOptions);
            return CountTokens(json, modelId);
        }

        private static int CountMessageTokens(ChatMessage message, string? modelId)
        {
            if (message.ContinuationState is { OutputItemsJson.Count: > 0 } continuation)
            {
                return continuation.OutputItemsJson.Sum(item => CountTokens(item, modelId)) + 4;
            }

            var tokenCount = CountTokens(message.Content, modelId)
                + CountTokens(message.ReasoningContent, modelId)
                + CountTokens(message.ToolCallId, modelId)
                + 4;
            if (message.ToolCalls is null)
                return tokenCount;

            foreach (var toolCall in message.ToolCalls)
            {
                tokenCount += CountTokens(toolCall.Id, modelId)
                    + CountTokens(toolCall.Name, modelId)
                    + CountTokens(toolCall.ArgumentsJson, modelId)
                    + 8;
            }

            return tokenCount;
        }

        private static Tokenizer GetTokenizer(string? modelId)
        {
            var key = ResolveTokenizerKey(modelId);
            return Tokenizers.GetOrAdd(key, static tokenizerKey =>
                tokenizerKey is "o200k_base" or "cl100k_base"
                    ? TiktokenTokenizer.CreateForEncoding(tokenizerKey)
                    : TiktokenTokenizer.CreateForModel(tokenizerKey));
        }

        private static string ResolveTokenizerKey(string? modelId)
        {
            if (string.IsNullOrWhiteSpace(modelId))
                return "o200k_base";

            var normalized = modelId.Trim().ToLowerInvariant();
            if (normalized.Contains("gpt-4o", StringComparison.Ordinal)
                || normalized.Contains("o1", StringComparison.Ordinal)
                || normalized.Contains("o3", StringComparison.Ordinal)
                || normalized.Contains("o4", StringComparison.Ordinal)
                || normalized.Contains("deepseek", StringComparison.Ordinal))
            {
                return "o200k_base";
            }

            if (normalized.Contains("gpt-4", StringComparison.Ordinal)
                || normalized.Contains("gpt-3.5", StringComparison.Ordinal)
                || normalized.Contains("text-embedding-3", StringComparison.Ordinal))
            {
                return "cl100k_base";
            }

            return "o200k_base";
        }

        private static string BuildCalibrationKey(string sessionId, string? modelId)
            => $"{sessionId}\u001f{modelId?.Trim() ?? string.Empty}";
    }
}
