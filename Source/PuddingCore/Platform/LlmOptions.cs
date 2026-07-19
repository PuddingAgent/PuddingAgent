using System.Collections.Concurrent;
using System.Linq;
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
        public int MessageTokens { get; set; }
        public int ToolDefinitionTokens { get; set; }
        public int SystemMessageTokens { get; set; }
        public int HistoryMessageTokens { get; set; }
        public int MessageCount { get; set; }
        public int ToolCount { get; set; }
        public string Source { get; set; } = "unknown";
        public string Confidence { get; set; } = "estimated";
        public int? ProviderPromptTokens { get; set; }
        public int? ProviderCompletionTokens { get; set; }
        public int? ProviderTotalTokens { get; set; }
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
            foreach (var message in messages)
            {
                var tokenCount = CountTokens(message.Content, modelId) + 4;
                messageTokens += tokenCount;
                if (message.Role == ChatRole.System)
                    systemTokens += tokenCount;
                else
                    historyTokens += tokenCount;
            }

            var toolTokens = CountToolDefinitionTokens(tools, modelId);
            var snapshot = new ContextUsageSnapshot
            {
                SessionId = sessionId,
                RecordedAt = DateTimeOffset.UtcNow,
                UsedTokens = Math.Max(0, messageTokens + toolTokens),
                MessageTokens = messageTokens,
                ToolDefinitionTokens = toolTokens,
                SystemMessageTokens = systemTokens,
                HistoryMessageTokens = historyTokens,
                MessageCount = messages.Count,
                ToolCount = tools?.Count ?? 0,
                Source = "llm_request",
                Confidence = "estimated",
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

            // Preserve CaptureLlmRequest's accurate cumulative token count.
            // Single-request provider usage (TotalTokens) represents only the
            // most recent API call, not the full context window. Fall back to
            // provider usage only when no prior snapshot exists.
            var usedTokens = existing is { UsedTokens: > 0 }
                ? existing.UsedTokens
                : Math.Max(0, usage.TotalTokens ?? usage.PromptTokens ?? 0);

            var snapshot = new ContextUsageSnapshot
            {
                SessionId = sessionId,
                RecordedAt = DateTimeOffset.UtcNow,
                UsedTokens = usedTokens,
                MessageTokens = existing?.MessageTokens ?? 0,
                ToolDefinitionTokens = existing?.ToolDefinitionTokens ?? 0,
                SystemMessageTokens = existing?.SystemMessageTokens ?? 0,
                HistoryMessageTokens = existing?.HistoryMessageTokens ?? 0,
                MessageCount = existing?.MessageCount ?? 0,
                ToolCount = existing?.ToolCount ?? 0,
                Source = "provider_usage",
                Confidence = "provider_reported",
                ProviderPromptTokens = usage.PromptTokens,
                ProviderCompletionTokens = usage.CompletionTokens,
                ProviderTotalTokens = usage.TotalTokens,
            };
            Set(snapshot);
            return snapshot;
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
    }
}
