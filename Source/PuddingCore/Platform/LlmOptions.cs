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
        DateTimeOffset RecordedAtUtc,
        /// <summary>
        /// 该实测所对应的**冻结请求序号**（单调）。投影按序号推进而不是按到达时间，
        /// 否则一次迟到的旧请求会把「最近主实测」拉回去（R1 门禁：最近 measurement 不得倒退）。
        /// 未配对/无序号时为 0。
        /// </summary>
        long RequestSequence = 0)
    {
        /// <summary>Provider <c>Total</c> 含 completion，**不得**当作「实测输入」使用。</summary>
        public bool HasProviderUsage =>
            ProviderPromptTokens is > 0 || ProviderTotalTokens is > 0;
    }

    /// <summary>
    /// **不可变的冻结请求记录**：最终 dispatch 前冻结的「这一次请求」的准备事实。
    /// <para>
    /// 为什么必须有它（R1 缺陷）：Provider usage 迟到时，若只按 session 取「当前最新准备快照」，
    /// 就会拿**另一次请求**（甚至另一个路由）的 raw 去算校准 —— 实测探针里
    /// A 的实测输入(2504)被 B 的 raw(6) 除，得到 417.33 倍并被写到 B 的模型校准键上，
    /// 既污染展示，又会抬高安全上界、重新制造硬压缩等待。
    /// </para>
    /// <para>
    /// 因此 usage 必须按 <see cref="InvocationId"/>/<see cref="AttemptId"/> 找到**它自己那次**冻结记录，
    /// 用那条记录的 raw 与 model 做校准；找不到身份的 usage 只进独立账本，
    /// 既不参与校准也不改写准备槽位。
    /// </para>
    /// </summary>
    public sealed record PreparedRequestRecord(
        string SessionId,
        string Purpose,
        string? InvocationId,
        string? AttemptId,
        long RequestSequence,
        string? ModelId,
        int RawEstimatedTokens,
        int CalibratedUpperBoundTokens,
        DateTimeOffset PreparedAtUtc);

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
        /// <summary>
        /// 校准系数向实测值回落的比例。取值 0.7 ⇒ 被抬到 1.6 的系数约 3 轮回到 1.0；
        /// 回落而非直接取观测值，是为了避免单次抖动把系数一把拉到底。
        /// </summary>
        private const double CalibrationRelaxFactor = 0.7;

        /// <summary>最近一次**主请求**的 Provider 实测（与 prepared 快照分离的槽位）。</summary>
        private readonly ConcurrentDictionary<string, MeasuredRequestUsage> _lastMeasuredRequests = new(StringComparer.Ordinal);

        /// <summary>最近一次辅助/摘要调用的实测；单独归因，永不写进上面的槽位。</summary>
        private readonly ConcurrentDictionary<string, MeasuredRequestUsage> _lastAuxiliaryMeasurements = new(StringComparer.Ordinal);

        /// <summary>会话内已冻结请求（key = 身份键）→ 不可变记录；用于按身份配对 usage。</summary>
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, PreparedRequestRecord>> _preparedRequests
            = new(StringComparer.Ordinal);

        /// <summary>
        /// invocation → 该 invocation 下的各 attempt（按请求序号）。仅用于「未给 attempt」时的
        /// **唯一性**判据：只有唯一一次冻结请求才允许明确约定的降级匹配（复核 P1）。
        /// </summary>
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, ConcurrentDictionary<long, PreparedRequestRecord>>> _preparedByInvocation
            = new(StringComparer.Ordinal);

        /// <summary>会话内单调请求序号（冻结时分配）。</summary>
        private readonly ConcurrentDictionary<string, long> _requestSequences = new(StringComparer.Ordinal);

        /// <summary>身份无法配对到冻结记录的 usage：只留证，不参与校准、不改写任何准备槽位。</summary>
        private readonly ConcurrentDictionary<string, MeasuredRequestUsage> _unpairedMeasurements = new(StringComparer.Ordinal);

        /// <summary>每个会话保留的冻结记录条数上限（按序号淘汰最旧）。</summary>
        private const int MaxFrozenRequestsPerSession = 16;

        public void Set(ContextUsageSnapshot snapshot)
        {
            if (string.IsNullOrWhiteSpace(snapshot.SessionId))
                return;

            _snapshots[snapshot.SessionId] = snapshot;
        }

        /// <summary>
        /// 冻结并发布一次请求准备事实：既登记不可变记录（供 usage 按身份配对），
        /// 也把它设为 session 的 <c>currentPreparedRequest</c>。
        /// <para>
        /// 只有这里（准备/冻结路径）允许改写准备槽位；Provider usage **不再**改它（R1）。
        /// </para>
        /// </summary>
        public PreparedRequestRecord PublishPreparedRequest(
            ContextUsageSnapshot snapshot,
            string purpose = LlmCallPurposes.Agent,
            string? invocationId = null,
            string? attemptId = null)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            var record = FreezePreparedRequest(snapshot, purpose, invocationId, attemptId);
            Set(snapshot);
            return record;
        }

        /// <summary>登记冻结记录（不改写准备槽位）；序号在此单调分配。</summary>
        public PreparedRequestRecord FreezePreparedRequest(
            ContextUsageSnapshot snapshot,
            string purpose = LlmCallPurposes.Agent,
            string? invocationId = null,
            string? attemptId = null)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            var sessionId = snapshot.SessionId;
            var sequence = string.IsNullOrWhiteSpace(sessionId)
                ? 0
                : _requestSequences.AddOrUpdate(sessionId, 1, (_, current) => current + 1);

            var record = new PreparedRequestRecord(
                sessionId,
                purpose,
                invocationId,
                attemptId,
                sequence,
                snapshot.ModelId,
                snapshot.RawEstimatedTokens,
                snapshot.UsedTokens,
                snapshot.RecordedAt == default ? DateTimeOffset.UtcNow : snapshot.RecordedAt);

            if (string.IsNullOrWhiteSpace(sessionId))
                return record;

            var perSession = _preparedRequests.GetOrAdd(
                sessionId,
                _ => new ConcurrentDictionary<string, PreparedRequestRecord>(StringComparer.Ordinal));

            foreach (var key in IdentityKeys(record))
                perSession[key] = record;

            // invocation → 该 invocation 下的各 attempt（仅未给 attempt 的降级匹配需要「唯一性」判据）。
            if (!string.IsNullOrWhiteSpace(invocationId))
            {
                var byInvocation = _preparedByInvocation.GetOrAdd(
                    sessionId,
                    _ => new ConcurrentDictionary<string, ConcurrentDictionary<long, PreparedRequestRecord>>(StringComparer.Ordinal));
                var attempts = byInvocation.GetOrAdd(
                    invocationId,
                    _ => new ConcurrentDictionary<long, PreparedRequestRecord>());
                attempts[sequence] = record;
            }

            // 有界：只保留序号最大的若干条，防止长会话无界增长。
            if (perSession.Count > MaxFrozenRequestsPerSession * 2)
            {
                var keep = perSession.Values
                    .OrderByDescending(r => r.RequestSequence)
                    .Take(MaxFrozenRequestsPerSession)
                    .Select(r => r.RequestSequence)
                    .ToHashSet();
                foreach (var pair in perSession.Where(p => !keep.Contains(p.Value.RequestSequence)).ToList())
                    perSession.TryRemove(pair.Key, out _);

                if (_preparedByInvocation.TryGetValue(sessionId, out var byInvocationIndex))
                {
                    foreach (var attempts in byInvocationIndex.Values)
                    {
                        foreach (var stale in attempts.Keys.Where(s => !keep.Contains(s)).ToList())
                            attempts.TryRemove(stale, out _);
                    }
                }
            }

            return record;
        }

        /// <summary>
        /// 按身份找该次请求的冻结记录。**配对规则（复核 P1）**：
        /// <list type="bullet">
        /// <item>明确给了 <paramref name="attemptId"/> ⇒ 必须**精确命中该 attempt**；
        /// 命中记录的 invocation 还必须与传入的 <paramref name="invocationId"/> 一致（给了才算）。
        /// 不允许「attempt 查不到就退到 invocation」——那会让未知/已淘汰 attempt 的 usage 用
        /// **另一次请求**的估算参与校准（复核探针：attempt-never-prepared 匹配到了 attempt-known）。</item>
        /// <item>未给 attempt、给了 invocation ⇒ 仅当该 invocation 对应**唯一**一次冻结请求时才允许
        /// 这一明确约定的降级匹配；有多次重试（歧义）⇒ 未配对。</item>
        /// </list>
        /// </summary>
        public bool TryGetPreparedRequest(
            string sessionId,
            string? invocationId,
            string? attemptId,
            out PreparedRequestRecord? record)
        {
            record = null;
            if (string.IsNullOrWhiteSpace(sessionId)
                || !_preparedRequests.TryGetValue(sessionId, out var perSession))
            {
                return false;
            }

            // ① 明确给了 attempt：只认精确 attempt 键。
            if (!string.IsNullOrWhiteSpace(attemptId))
            {
                if (!perSession.TryGetValue(AttemptIdentityKey(sessionId, attemptId), out var byAttempt))
                    return false;

                // 身份一致性：调用方给了 invocation 就必须对得上，否则视为身份冲突（未配对）。
                if (!string.IsNullOrWhiteSpace(invocationId)
                    && !string.Equals(byAttempt.InvocationId, invocationId, StringComparison.Ordinal))
                {
                    return false;
                }

                record = byAttempt;
                return true;
            }

            // ② 未给 attempt：仅当 invocation → 唯一一次冻结请求时才降级匹配。
            if (string.IsNullOrWhiteSpace(invocationId))
                return false;

            if (!_preparedByInvocation.TryGetValue(sessionId, out var byInvocation)
                || !byInvocation.TryGetValue(invocationId, out var attempts)
                || attempts.IsEmpty)
            {
                return false;
            }

            if (attempts.Count != 1)
                return false; // 同一 invocation 有多次 attempt ⇒ 歧义，不得猜

            record = attempts.Values.First();
            return true;
        }

        /// <summary>身份无法配对的 usage（只留证）。</summary>
        public bool TryGetUnpairedMeasurement(string sessionId, out MeasuredRequestUsage? measured)
        {
            var ok = _unpairedMeasurements.TryGetValue(sessionId, out var found);
            measured = found;
            return ok;
        }

        private static IEnumerable<string> IdentityKeys(PreparedRequestRecord record)
            => IdentityKeys(record.SessionId, record.InvocationId, record.AttemptId);

        private static string AttemptIdentityKey(string sessionId, string attemptId)
            => $"{sessionId}\u001fattempt\u001f{attemptId}";

        private static IEnumerable<string> IdentityKeys(string sessionId, string? invocationId, string? attemptId)
        {
            if (!string.IsNullOrWhiteSpace(attemptId))
                yield return AttemptIdentityKey(sessionId, attemptId);
            if (!string.IsNullOrWhiteSpace(invocationId))
                yield return $"{sessionId}\u001finvocation\u001f{invocationId}";
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
            => CaptureLlmRequest(
                sessionId, messages, tools, modelId, LlmCallPurposes.Agent, invocationId: null, attemptId: null);

        /// <summary>
        /// 计量并发布一次**准备请求**，同时按 invocation/attempt 冻结身份（R1）。
        /// 生产路径必须带上身份，否则该请求的 Provider usage 会因配不上身份而只进未配对账本
        /// （安全，但失去校准与最近实测投影）。
        /// </summary>
        public ContextUsageSnapshot CaptureLlmRequest(
            string sessionId,
            IReadOnlyList<ChatMessage> messages,
            IReadOnlyList<LlmToolDefinition>? tools,
            string? modelId,
            string purpose,
            string? invocationId,
            string? attemptId)
        {
            var snapshot = MeasureLlmRequest(sessionId, messages, tools, modelId);
            PublishPreparedRequest(snapshot, purpose, invocationId, attemptId);
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
                // 辅助 raw 也必须来自**它自己**的冻结请求（借主槽位就是 R1 的同类错误）。
                // 复核 P1：还须核验 purpose —— 主请求的冻结记录不得被辅助 usage 借用（反之亦然）。
                var auxRecordFound = TryGetPreparedRequest(sessionId, invocationId, attemptId, out var auxRecord)
                    && auxRecord is not null
                    && !LlmCallPurposes.IsMainCall(auxRecord.Purpose);
                var auxiliaryRaw = auxRecordFound ? auxRecord!.RawEstimatedTokens : 0;
                _lastAuxiliaryMeasurements[sessionId] = new MeasuredRequestUsage(
                    sessionId,
                    purpose,
                    invocationId,
                    attemptId,
                    usage.PromptTokens,
                    usage.CompletionTokens,
                    usage.TotalTokens,
                    auxiliaryRaw,
                    DateTimeOffset.UtcNow,
                    auxRecord?.RequestSequence ?? 0);

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

            // ── R1：主请求实测必须按 invocation/attempt 配到**它自己那次**冻结请求 ──
            // 配不到身份的 usage 只进独立账本：既不参与校准，也不改写 currentPreparedRequest。
            // 这样一次迟到的旧请求不会用新请求的 raw 去算校准，也不会把准备槽位改成 Provider Total。
            // 复核 P1：还须核验 purpose —— 辅助调用的冻结记录不得被主 usage 借用。
            if (!TryGetPreparedRequest(sessionId, invocationId, attemptId, out var frozen)
                || frozen is null
                || !LlmCallPurposes.IsMainCall(frozen.Purpose))
            {
                _unpairedMeasurements[sessionId] = new MeasuredRequestUsage(
                    sessionId,
                    purpose,
                    invocationId,
                    attemptId,
                    usage.PromptTokens,
                    usage.CompletionTokens,
                    usage.TotalTokens,
                    RawEstimatedTokens: 0,
                    DateTimeOffset.UtcNow,
                    RequestSequence: 0);

                return existing ?? new ContextUsageSnapshot
                {
                    SessionId = sessionId,
                    RecordedAt = DateTimeOffset.UtcNow,
                    Source = "provider_usage_unpaired",
                    Confidence = "provider_reported",
                    ProviderPromptTokens = usage.PromptTokens,
                    ProviderCompletionTokens = usage.CompletionTokens,
                    ProviderTotalTokens = usage.TotalTokens,
                };
            }

            if (frozen.RawEstimatedTokens > 0 && providerPromptTokens > 0)
            {
                // 用**该请求自己的** raw 与 model 校准 ⇒ 校准天然按路由隔离。
                var observedRatio = Math.Max(1.0, providerPromptTokens / (double)frozen.RawEstimatedTokens);
                var calibrationKey = BuildCalibrationKey(sessionId, frozen.ModelId);
                // 允许校准系数向实测值**收敛**，而不是只保留历史最大值。
                // 只增不减会带来一个隐蔽后果：一次 provider「输入超限」失败把系数抬到 1.6 之后，
                // 即便之后每轮实测都只有原始估算的 0.62 倍（observedRatio=1.0），系数也永远停在 1.6，
                // 于是 usedTokens 被永久放大 1.6 倍、有效窗口却没变 ⇒ 压缩被提前触发。
                // 规则：实测更高就立即抬高（防超窗）；实测更低则逐轮按系数回落，但永不低于 1.0
                // （低于 1.0 意味着估算低于原始估算，会让请求超出 provider 输入上限）。
                _promptCalibrationRatios.AddOrUpdate(
                    calibrationKey,
                    observedRatio,
                    (_, current) => observedRatio >= current
                        ? observedRatio
                        : Math.Max(1.0, current * CalibrationRelaxFactor));
            }

            // Provider 完全没报数（Prompt/Completion/Total 全空）⇒ 不产生实测、不推进投影。
            // 不允许用 0 伪造一次实测（旧实现同样在这里提前返回）。
            var hasAnyProviderNumbers =
                usage.PromptTokens is not null || usage.CompletionTokens is not null || usage.TotalTokens is not null;
            if (!hasAnyProviderNumbers)
                return existing ?? new ContextUsageSnapshot
                {
                    SessionId = sessionId,
                    RecordedAt = DateTimeOffset.UtcNow,
                    Source = "provider_usage_missing",
                    Confidence = "unknown",
                };

            // 投影按**请求序号**单调推进（不是到达时间）：迟到的旧实测不得把最近主实测拉回去。
            // Total 缺失时按 prompt+completion 推导（Provider 没给 Total 不代表这次调用没有总用量）；
            // 该推导只进实测槽位，不参与「准备槽位」的改写。
            var measuredTotal = usage.TotalTokens is > 0
                ? usage.TotalTokens
                : usage.PromptTokens is not null
                    ? (int?)Math.Min(int.MaxValue, (long)providerPromptTokens + Math.Max(0, usage.CompletionTokens ?? 0))
                    : null;
            var measurement = new MeasuredRequestUsage(
                sessionId,
                purpose,
                // 记录**配对到的那次冻结请求**的身份：调用方可能只给 invocation（唯一时降级匹配），
                // 此时实测记录必须指向真正的 attempt，展示与诊断才不会有歧义。
                frozen.InvocationId,
                frozen.AttemptId,
                usage.PromptTokens,
                usage.CompletionTokens,
                measuredTotal,
                frozen.RawEstimatedTokens,
                DateTimeOffset.UtcNow,
                frozen.RequestSequence);
            _lastMeasuredRequests.AddOrUpdate(
                sessionId,
                measurement,
                (_, current) => measurement.RequestSequence >= current.RequestSequence ? measurement : current);

            // 关键：**不 Set**。准备槽位只由准备/冻结路径改写；Provider usage 不再覆盖它。
            return existing ?? new ContextUsageSnapshot
            {
                SessionId = sessionId,
                RecordedAt = DateTimeOffset.UtcNow,
                Source = "provider_usage",
                Confidence = "provider_reported",
                ProviderPromptTokens = usage.PromptTokens,
                ProviderCompletionTokens = usage.CompletionTokens,
                ProviderTotalTokens = usage.TotalTokens,
            };
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
