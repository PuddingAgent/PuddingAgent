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
        /// <summary>系统提示词层（Role=System 且非压缩摘要）token 估算。</summary>
        public int SystemPromptTokens { get; set; }
        /// <summary>
        /// 压缩摘要层 token 估算。识别标记见 <see cref="PuddingCode.Runtime.ContextSummaryMarkers"/>：
        /// 持久摘要 <c>&lt;compact_summary&gt;</c> 与 warm-prefix checkpoint <c>&lt;compacted-summary&gt;</c> 同源计入本桶。
        /// </summary>
        public int CompactionSummaryTokens { get; set; }
        /// <summary>对话消息层（Role=User/Assistant 且非压缩摘要）token 估算。</summary>
        public int ConversationTokens { get; set; }
        /// <summary>工具结果层（Role=Tool 且非压缩摘要）token 估算。</summary>
        public int ToolResultTokens { get; set; }
        /// <summary>思维链层：各消息 ReasoningContent 部分的 token 估算（已从角色桶中扣除）。</summary>
        public int ReasoningTokens { get; set; }
        public int MessageCount { get; set; }
        public int ToolCount { get; set; }
        /// <summary>规范化工具名称、描述和参数 schema 的稳定哈希。</summary>
        public string? ToolDefinitionHash { get; set; }
        /// <summary>工具 schema 的原始 UTF-8 字节数，仅用于归因，不保存正文。</summary>
        public long ToolDefinitionUtf8Bytes { get; set; }
        /// <summary>工具 schema 的 GZIP 字节数，仅用于归因，不保存正文。</summary>
        public long ToolDefinitionGzipBytes { get; set; }
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
    /// LLM 调用的用途。它决定这次 Provider usage 是否可以改写 session 的
    /// <c>currentPreparedRequest</c> 与安全校准（ADR-095 D6）。
    /// </summary>
    public static class LlmCallPurposes
    {
        /// <summary>主 Agent 调用：唯一允许改写 prepared 快照并参与安全校准的用途。</summary>
        public const string Agent = "agent";

        /// <summary>压缩摘要调用：单独归因，不得改写主请求的估算或校准。</summary>
        public const string Compaction = "compaction";

        /// <summary>审批/分类等辅助调用。</summary>
        public const string Approval = "approval";

        /// <summary>其它辅助调用。</summary>
        public const string Auxiliary = "auxiliary";

        /// <summary>是否属于「主请求」用途（缺省视为主请求，保持既有调用者语义）。</summary>
        public static bool IsMainCall(string? purpose)
            => string.IsNullOrWhiteSpace(purpose)
               || string.Equals(purpose, Agent, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 一次**已完成**的 Provider 调用实测，存放在与「本轮准备估算」分离的槽位里。
    /// <para>
    /// 归因纪律（ADR-095 D6）：<c>currentPreparedRequest</c>（本次准备，随请求身份）与
    /// <c>lastMeasuredRequest</c>（最近一次**主请求**实测）必须分开保存；迟到、交错、取消
    /// 或辅助调用的 usage 只能写自己的槽位，不得覆盖更晚的准备快照，也不得参与出站安全校准。
    /// </para>
    /// </summary>
    public sealed record MeasuredRequestUsage(
        string SessionId,
        string Purpose,
        string? InvocationId,
        string? AttemptId,
        int? ProviderPromptTokens,
        int? ProviderCompletionTokens,
        int? ProviderTotalTokens,
        int RawEstimatedTokens,
        DateTimeOffset RecordedAtUtc)
    {
        /// <summary>Provider <c>Total</c> 含 completion，**不得**当作「实测输入」使用。</summary>
        public bool HasProviderUsage =>
            ProviderPromptTokens is > 0 || ProviderTotalTokens is > 0;
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

        /// <summary>最近一次**主请求**的 Provider 实测（与 prepared 快照分离的槽位）。</summary>
        private readonly ConcurrentDictionary<string, MeasuredRequestUsage> _lastMeasuredRequests = new(StringComparer.Ordinal);

        /// <summary>最近一次辅助/摘要调用的实测；单独归因，永不写进上面的槽位。</summary>
        private readonly ConcurrentDictionary<string, MeasuredRequestUsage> _lastAuxiliaryMeasurements = new(StringComparer.Ordinal);

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
            var snapshot = MeasureLlmRequest(sessionId, messages, tools, modelId);
            Set(snapshot);
            return snapshot;
        }

        /// <summary>
        /// 只计量、**不发布**：用于候选/多方案测量，避免把测量结果写成 session 的
        /// <c>currentPreparedRequest</c>。
        /// <para>
        /// 为什么必须分开（诊断 2026-10-07 §5.1 的归因缺陷）：软压缩候选在挑选过程中会对
        /// 「移除后的保留列表」反复计量；若这些计量顺带发布，session 快照就会被留在**压缩后**
        /// 的形状上，随后压缩请求的 `FreezeRequestContext` 取到的就是压缩后的 shape
        /// （DB 实测 176,873 ≈ 326,392/1.845，而判据输入是 583,691），并且摘要调用的
        /// `RecordProviderUsage` 会拿 Provider 报数去比这个错误的 raw。
        /// </para>
        /// </summary>
        public ContextUsageSnapshot MeasureLlmRequest(
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
            var systemPromptTokens = 0;
            var compactionSummaryTokens = 0;
            var conversationTokens = 0;
            var toolResultTokens = 0;

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

                // 分层归因（上下文用量进度条）：各桶互斥且穷尽 —— ChatRole 只有
                // System/User/Assistant/Tool，压缩摘要按内容标记识别（不依赖角色，
                // 注入路径可能落在 System 或 User 上）。
                // 摘要身份来自共享合同（ContextSummaryMarkers）：持久摘要 <compact_summary> 与
                // warm-prefix checkpoint <compacted-summary> 必须同源识别，否则后者被误计入对话桶。
                if (ContextSummaryMarkers.ContainsMarker(content))
                {
                    compactionSummaryTokens += tokenCount;
                }
                else if (message.Role == ChatRole.System)
                {
                    systemPromptTokens += tokenCount;
                }
                else if (message.Role == ChatRole.Tool)
                {
                    toolResultTokens += tokenCount;
                }
                else
                {
                    conversationTokens += tokenCount;
                }
            }

            // 思维链层（第五桶）：Assistant/User 消息的 ReasoningContent 部分单独计量，
            // 再从对话消息桶里扣除，保证五个桶互斥且合计 = MessageTokens。
            var reasoningTokens = 0;
            foreach (var message in messages)
            {
                if (message.ContinuationState is { OutputItemsJson.Count: > 0 })
                    continue;
                if (message.Role is not (ChatRole.Assistant or ChatRole.User))
                    continue;
                if (ContextSummaryMarkers.ContainsMarker(message.Content))
                    continue;
                reasoningTokens += CountTokens(message.ReasoningContent, modelId);
            }
            conversationTokens = Math.Max(0, conversationTokens - reasoningTokens);

            var toolTokens = CountToolDefinitionTokens(tools, modelId);
            var toolText = tools is { Count: > 0 }
                ? JsonSerializer.Serialize(tools, JsonOptions)
                : null;
            var toolMetrics = EntropyProbe.Measure(toolText);
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
                SystemPromptTokens = systemPromptTokens,
                CompactionSummaryTokens = compactionSummaryTokens,
                ConversationTokens = conversationTokens,
                ToolResultTokens = toolResultTokens,
                ReasoningTokens = reasoningTokens,
                MessageCount = messages.Count,
                ToolCount = tools?.Count ?? 0,
                ToolDefinitionHash = toolDefinitionHash,
                ToolDefinitionUtf8Bytes = toolMetrics.RawUtf8Bytes,
                ToolDefinitionGzipBytes = toolMetrics.GzipBytes,
                Source = calibrationRatio > 1.0001 ? "llm_request_calibrated" : "llm_request",
                Confidence = calibrationRatio > 1.0001 ? "provider_calibrated" : "estimated",
                ModelId = modelId,
                PromptCalibrationRatio = calibrationRatio,
                SystemMessageEntropy = EntropyProbe.ComputeGzipRatio(systemText.ToString()),
                HistoryMessageEntropy = EntropyProbe.ComputeGzipRatio(historyText.ToString()),
                ToolDefinitionEntropy = EntropyProbe.ComputeGzipRatio(toolText),
            };
            return snapshot;
        }

        /// <summary>
        /// 记录一次已完成的 Provider 调用实测。
        /// <para>
        /// 只有**主请求**用途（<see cref="LlmCallPurposes.IsMainCall"/>）才允许：
        /// ① 用 Provider 报数改写 session 的 <c>currentPreparedRequest</c>；
        /// ② 更新安全校准系数；③ 写入 <c>lastMeasuredRequest</c>。
        /// 辅助/摘要调用（压缩、审批……）单独归因：它们既不能改写主请求的估算，
        /// 也不能抬高出站硬门禁用的保守系数（ADR-095 D6）。
        /// </para>
        /// </summary>
        /// <param name="purpose">调用用途；缺省 = 主请求，保持既有调用者语义。</param>
        /// <param name="invocationId">逻辑调用身份（重试复用）。</param>
        /// <param name="attemptId">物理尝试身份（重试产生新值）。</param>
        public ContextUsageSnapshot RecordProviderUsage(
            string sessionId,
            TokenUsageDto usage,
            string purpose = LlmCallPurposes.Agent,
            string? invocationId = null,
            string? attemptId = null)
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

            if (!LlmCallPurposes.IsMainCall(purpose))
            {
                // 辅助/摘要调用：只写自己的槽位。不发布、不改写、不校准。
                _lastAuxiliaryMeasurements[sessionId] = new MeasuredRequestUsage(
                    sessionId,
                    purpose,
                    invocationId,
                    attemptId,
                    usage.PromptTokens,
                    usage.CompletionTokens,
                    usage.TotalTokens,
                    existing?.RawEstimatedTokens ?? 0,
                    DateTimeOffset.UtcNow);

                return existing ?? new ContextUsageSnapshot
                {
                    SessionId = sessionId,
                    RecordedAt = DateTimeOffset.UtcNow,
                    Source = "provider_usage_auxiliary",
                    Confidence = "provider_reported",
                    ProviderPromptTokens = usage.PromptTokens,
                    ProviderCompletionTokens = usage.CompletionTokens,
                    ProviderTotalTokens = usage.TotalTokens,
                };
            }

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

            // Total usage estimates the next input including this assistant response.
            // A local overestimate is not provider-reported usage. Preserve conservative
            // calibration for the outbound hard-limit guard, not in measured telemetry.
            var providerTokens = Math.Max(0, usage.TotalTokens is > 0
                ? usage.TotalTokens.Value
                : (usage.PromptTokens is not null
                    ? (int)Math.Min(int.MaxValue, (long)providerPromptTokens + Math.Max(0, usage.CompletionTokens ?? 0))
                    : 0));
            var hasProviderUsage = providerTokens > 0;
            if (!hasProviderUsage && existing is not null)
                return existing;
            var usedTokens = hasProviderUsage ? providerTokens : existing?.UsedTokens ?? 0;

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
                SystemPromptTokens = existing?.SystemPromptTokens ?? 0,
                CompactionSummaryTokens = existing?.CompactionSummaryTokens ?? 0,
                ConversationTokens = existing?.ConversationTokens ?? 0,
                ToolResultTokens = existing?.ToolResultTokens ?? 0,
                ReasoningTokens = existing?.ReasoningTokens ?? 0,
                MessageCount = existing?.MessageCount ?? 0,
                ToolCount = existing?.ToolCount ?? 0,
                ToolDefinitionHash = existing?.ToolDefinitionHash,
                ToolDefinitionUtf8Bytes = existing?.ToolDefinitionUtf8Bytes ?? 0,
                ToolDefinitionGzipBytes = existing?.ToolDefinitionGzipBytes ?? 0,
                Source = hasProviderUsage ? "provider_usage" : existing?.Source ?? "unknown",
                Confidence = hasProviderUsage ? "provider_reported" : existing?.Confidence ?? "estimated",
                ProviderPromptTokens = usage.PromptTokens,
                ProviderCompletionTokens = usage.CompletionTokens,
                ProviderTotalTokens = usage.TotalTokens,
                ModelId = existing?.ModelId,
                PromptCalibrationRatio = calibrationRatio,
            };
            Set(snapshot);
            // 实测与准备分离：主请求实测写入自己的槽位，不回写 raw estimate，
            // 也不因为迟到而覆盖更晚的准备快照（两者本就不同槽位）。
            _lastMeasuredRequests[sessionId] = new MeasuredRequestUsage(
                sessionId,
                purpose,
                invocationId,
                attemptId,
                usage.PromptTokens,
                usage.CompletionTokens,
                usage.TotalTokens,
                rawEstimatedTokens,
                DateTimeOffset.UtcNow);
            return snapshot;
        }

        /// <summary>最近一次**主请求**的 Provider 实测；null = 尚未观察到（不得用 0 冒充）。</summary>
        public bool TryGetLastMeasuredRequest(string sessionId, out MeasuredRequestUsage? measured)
        {
            var ok = _lastMeasuredRequests.TryGetValue(sessionId, out var found);
            measured = found;
            return ok;
        }

        /// <summary>最近一次辅助/摘要调用的实测；单独归因，不参与主请求口径。</summary>
        public bool TryGetLastAuxiliaryMeasurement(string sessionId, out MeasuredRequestUsage? measured)
        {
            var ok = _lastAuxiliaryMeasurements.TryGetValue(sessionId, out var found);
            measured = found;
            return ok;
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
