using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace PuddingCode.SubAgents;

/// <summary>
/// 2026-11-14：把 <c>events.jsonl</c> 的事件行折叠成延迟分解。
///
/// <para>
/// 抽出来的原因：这段逻辑原先只存在于 <c>SubAgentDiagnosticsService.GetRunLatencyBreakdownAsync</c>，
/// 而 <c>pool_action=logs</c> 也需要同一份统计。复制一份会让两处定义漂移
/// （例如轮次到底数 <c>llm.completed</c> 还是 <c>round.completed</c>），所以收敛到一处。
/// </para>
///
/// <para>
/// 调用方逐行喂 <see cref="AddEventLine"/>（流式，不要求整份日志进内存），最后调 <see cref="Build"/>。
/// 畸形行不计入、不抛。
/// </para>
/// </summary>
public sealed class SubAgentLatencyAggregator
{
    private long _llmMs;
    private long _toolMs;
    private int _rounds;
    private int _toolCalls;
    private long? _firstTimestampMs;
    private long? _lastTimestampMs;
    private long? _currentLlmStartMs;

    /// <summary>每次 LLM 调用的耗时，用于分位统计（不参与总额，总额仍由 _llmMs 单点累加）。</summary>
    private readonly List<long> _llmDurations = new();

    private long _promptTokens;
    private long _completionTokens;
    private long _cacheHitTokens;
    private long _cacheMissTokens;
    private long _totalTokens;

    /// <summary>折叠一行事件。行畸形或缺少必要字段时静默跳过。</summary>
    public void AddEventLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            // 事件行的字段是 eventType（不是 type）。读错会静默得到全 0 的合法对象。
            var type = root.TryGetProperty("eventType", out var t) ? t.GetString() : null;
            var stamp = root.TryGetProperty("timestamp", out var ts) ? ts.GetString() : null;
            if (type is null || stamp is null)
                return;
            if (!DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.None, out var timestamp))
                return;

            var epoch = timestamp.ToUnixTimeMilliseconds();
            _firstTimestampMs ??= epoch;
            _lastTimestampMs = epoch;

            switch (type)
            {
                case "subagent.llm.started":
                    _currentLlmStartMs = epoch;
                    break;
                case "subagent.llm.completed":
                    // 只做 LLM 耗时累加；轮次由 round.completed 单点计数，避免两处定义漂移。
                    if (_currentLlmStartMs is { } started)
                    {
                        var llmElapsed = Math.Max(0, epoch - started);
                        _llmMs += llmElapsed;
                        _llmDurations.Add(llmElapsed);
                        _currentLlmStartMs = null;
                    }
                    AccumulateTokens(root);
                    break;
                case "subagent.round.completed":
                    _rounds++;
                    break;
                case "subagent.tool.completed":
                case "subagent.tool.failed":
                    _toolMs += Math.Max(0, ReadDurationMs(root));
                    _toolCalls++;
                    break;
            }
        }
        catch (JsonException)
        {
            // 畸形行不计入。
        }
    }

    /// <summary>结算成对外的分解对象。</summary>
    public SubAgentLatencyBreakdown Build(string runId)
    {
        var totalMs = _firstTimestampMs is { } first && _lastTimestampMs is { } last
            ? Math.Max(0, last - first)
            : 0L;
        var overheadMs = Math.Max(0, totalMs - _llmMs - _toolMs);

        return new SubAgentLatencyBreakdown
        {
            RunId = runId,
            TotalDurationMs = totalMs,
            LlmDurationMs = _llmMs,
            ToolDurationMs = _toolMs,
            OverheadMs = overheadMs,
            RoundCount = _rounds,
            ToolCallCount = _toolCalls,
            LlmPct = Percent(_llmMs, totalMs),
            ToolPct = Percent(_toolMs, totalMs),
            OverheadPct = Percent(overheadMs, totalMs),
            LlmCallCount = _llmDurations.Count,
            LlmMaxMs = _llmDurations.Count == 0 ? 0 : _llmDurations.Max(),
            LlmP50Ms = Percentile(_llmDurations, 50),
            LlmP95Ms = Percentile(_llmDurations, 95),
            PromptTokens = _promptTokens,
            CompletionTokens = _completionTokens,
            CacheHitTokens = _cacheHitTokens,
            CacheMissTokens = _cacheMissTokens,
            TotalTokens = _totalTokens,
            CacheHitPct = Percent(_cacheHitTokens, _cacheHitTokens + _cacheMissTokens),
        };
    }

    /// <summary>优先 payload.duration_ms（当前格式），兼容根级旧格式。</summary>
    private static long ReadDurationMs(JsonElement root)
    {
        if (root.TryGetProperty("payload", out var payload)
            && payload.TryGetProperty("duration_ms", out var nested)
            && nested.TryGetInt64(out var nestedValue))
        {
            return nestedValue;
        }

        return root.TryGetProperty("duration_ms", out var flat) && flat.TryGetInt64(out var flatValue)
            ? flatValue
            : 0L;
    }

    /// <summary>从 llm.completed 的 payload 累加 token 与缓存命中。字段缺失按 0 计。</summary>
    private void AccumulateTokens(JsonElement root)
    {
        if (!root.TryGetProperty("payload", out var payload))
            return;

        _promptTokens += ReadLong(payload, "prompt_tokens");
        _completionTokens += ReadLong(payload, "completion_tokens");
        _cacheHitTokens += ReadLong(payload, "cache_hit_tokens");
        _cacheMissTokens += ReadLong(payload, "cache_miss_tokens");
        _totalTokens += ReadLong(payload, "total_tokens");
    }

    private static long ReadLong(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var value) && value.TryGetInt64(out var parsed) ? parsed : 0L;

    /// <summary>
    /// 最近秩分位（nearest-rank）：排序后取 ceil(p/100 × n) 位（1 基）。
    /// 样本为空返回 0；样本量小时 P95 会等于最大值，这是预期行为，不做插值。
    /// </summary>
    private static long Percentile(List<long> samples, int p)
    {
        if (samples.Count == 0)
            return 0L;

        var sorted = samples.OrderBy(v => v).ToList();
        var rank = (int)Math.Ceiling(p / 100.0 * sorted.Count);
        if (rank < 1)
            rank = 1;
        if (rank > sorted.Count)
            rank = sorted.Count;
        return sorted[rank - 1];
    }

    private static double Percent(long part, long total) =>
        total > 0 ? Math.Round((double)part / total * 100, 1) : 0;
}
