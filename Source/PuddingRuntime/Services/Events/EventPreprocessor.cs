using PuddingCode.Abstractions;
using PuddingCode.Models;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PuddingRuntime.Services.Events;

/// <summary>
/// 事件预处理器 — 去重窗口 + 批处理窗口。
/// 使用内存滑动窗口，按 sourceId+eventType 分组。
/// </summary>
public class EventPreprocessor : IEventPreprocessor
{
    private readonly ILogger<EventPreprocessor> _logger;
    private PreprocessorConfig _config = new();

    // 滑动窗口缓存：Key = source/session/type/payload fingerprint, Value = (lastEvent, timestamp)
    private readonly ConcurrentDictionary<string, (RawEvent Event, long TimestampMs)> _dedupCache = new();
    private readonly ConcurrentDictionary<string, List<RawEvent>> _batchCache = new();

    public EventPreprocessor(ILogger<EventPreprocessor> logger)
    {
        _logger = logger;
    }

    public Task ConfigureAsync(PreprocessorConfig config, CancellationToken ct = default)
    {
        _config = config;
        _logger.LogInformation("[EventPreprocessor] Configured: dedup={DedupMs}ms batch={BatchMs}ms",
            config.DedupWindowMs, config.BatchWindowMs);
        return Task.CompletedTask;
    }

    public Task<ProcessedEvent[]> ProcessAsync(RawEvent[] rawEvents, CancellationToken ct = default)
    {
        if (rawEvents.Length == 0)
            return Task.FromResult(Array.Empty<ProcessedEvent>());

        var results = new List<ProcessedEvent>();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        foreach (var raw in rawEvents)
        {
            var dedupKey = BuildDedupKey(raw);

            // 去重：检查是否在窗口内已有相同事件
            if (_dedupCache.TryGetValue(dedupKey, out var cached) &&
                (now - cached.TimestampMs) < _config.DedupWindowMs)
            {
                // 更新缓存（保留最新）
                _dedupCache[dedupKey] = (raw, now);
                _logger.LogDebug("[EventPreprocessor] Dedup absorbed: {Key}", dedupKey);
                continue;
            }

            _dedupCache[dedupKey] = (raw, now);
            results.Add(new ProcessedEvent
            {
                EventId = raw.RawEventId,
                Type = raw.Type,
                Source = raw.Source,
                WorkspaceId = raw.WorkspaceId,
                AgentId = raw.AgentId,
                SessionId = raw.SessionId,
                Payload = raw.Payload,
                Timestamp = raw.Timestamp,
                MergeCount = 1,
                Trace = raw.Trace,
            });
        }

        // 清理过期的去重缓存条目
        CleanExpiredCache(now);

        return Task.FromResult(results.ToArray());
    }

    private void CleanExpiredCache(long now)
    {
        var expiredKeys = new List<string>();
        foreach (var kvp in _dedupCache)
        {
            if ((now - kvp.Value.TimestampMs) > _config.DedupWindowMs * 2)
                expiredKeys.Add(kvp.Key);
        }
        foreach (var key in expiredKeys)
            _dedupCache.TryRemove(key, out _);
    }

    private static string BuildDedupKey(RawEvent raw)
    {
        var payloadFingerprint = BuildPayloadFingerprint(raw.Payload);
        return string.Join(
            ":",
            raw.Source.SourceType,
            raw.Source.SourceId ?? "",
            raw.SessionId ?? "",
            raw.Type,
            payloadFingerprint);
    }

    private static string BuildPayloadFingerprint(object? payload)
    {
        var payloadText = payload switch
        {
            null => "",
            JsonElement element => element.GetRawText(),
            _ => JsonSerializer.Serialize(payload),
        };

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payloadText));
        return Convert.ToHexString(hash);
    }
}
