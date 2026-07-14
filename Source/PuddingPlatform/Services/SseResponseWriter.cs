using System.Text.Json;
using PuddingCode.Platform;

namespace PuddingPlatform.Services;

/// <summary>
/// 共享 SSE 响应工具 — 消除 SessionEventsController / WorkspaceNotificationsController / MessageIngressController
/// 中重复的 ConfigureSseResponse 和 WriteRawSseFrame 方法。
/// </summary>
public static class SseResponseWriter
{
    /// <summary>设置 SSE 响应头（text/event-stream, no-cache, keep-alive, X-Accel-Buffering: no）。</summary>
    public static void Configure(HttpResponse response)
    {
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        response.Headers.Connection = "keep-alive";
        response.Headers["X-Accel-Buffering"] = "no";
    }

    /// <summary>
    /// 写入一帧 SSE 事件到响应流。如果帧数据中包含 sequenceNum，自动写入 id: 行。
    /// </summary>
    public static async Task WriteFrameAsync(
        HttpResponse response,
        ServerSentEventFrame frame,
        CancellationToken ct)
    {
        var seq = TryReadSequenceNum(frame.Data);
        if (seq.HasValue)
            await response.WriteAsync($"id: {seq.Value}\n", ct);
        await response.WriteAsync($"event: {frame.Event}\n", ct);
        await response.WriteAsync($"data: {frame.Data}\n\n", ct);
    }

    /// <summary>写入并 flush，适用于不批处理的 SSE 流。</summary>
    public static async Task WriteFrameAndFlushAsync(
        HttpResponse response,
        ServerSentEventFrame frame,
        CancellationToken ct)
    {
        await WriteFrameAsync(response, frame, ct);
        await response.Body.FlushAsync(ct);
    }

    private static long? TryReadSequenceNum(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("sequenceNum", out var seq) &&
                seq.TryGetInt64(out var value))
                return value;
        }
        catch (JsonException) { }
        return null;
    }
}
