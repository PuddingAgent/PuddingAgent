using System.Globalization;
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
                        _llmMs += Math.Max(0, epoch - started);
                        _currentLlmStartMs = null;
                    }
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

    private static double Percent(long part, long total) =>
        total > 0 ? Math.Round((double)part / total * 100, 1) : 0;
}
