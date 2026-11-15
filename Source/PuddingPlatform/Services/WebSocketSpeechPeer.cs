using System.Net.WebSockets;
using System.Text.Json;
using PuddingSpeech;

namespace PuddingPlatform.Services;

/// <summary>Local browser protocol only: PCM bytes, finish/cancel controls and provider-neutral events.</summary>
public sealed class WebSocketSpeechPeer(WebSocket socket, ILogger logger) : IRealtimeSpeechPeer
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim sending = new(1, 1);

    public async Task<string?> ReadTicketAsync(CancellationToken ct)
    {
        var frame = await ReadAsync(ct);
        if (frame is null || frame.Value.Kind != WebSocketMessageType.Text) throw InvalidFrame();
        using var doc = JsonDocument.Parse(frame.Value.Bytes);
        var root = doc.RootElement;
        if (root.GetProperty("type").GetString() != "start" || root.GetProperty("format").GetString() != "pcm"
            || root.GetProperty("sampleRate").GetInt32() != 16000) throw InvalidFrame();
        return root.GetProperty("ticket").GetString();
    }

    public async Task<RealtimeSpeechInput?> ReceiveAsync(CancellationToken ct)
    {
        var frame = await ReadAsync(ct);
        if (frame is null) return null;
        if (frame.Value.Kind == WebSocketMessageType.Binary) return new(frame.Value.Bytes);
        using var doc = JsonDocument.Parse(frame.Value.Bytes);
        return new(Control: doc.RootElement.GetProperty("type").GetString());
    }

    public async Task SendAsync(RealtimeSpeechEvent message, CancellationToken ct)
    {
        if (message.Type == "failed") logger.LogWarning("[VoiceLive] errorId={ErrorId} code={Code}", message.ErrorId, message.Code);
        await sending.WaitAsync(ct);
        try { await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(message, Json).AsMemory(), WebSocketMessageType.Text, true, ct); }
        finally { sending.Release(); }
    }

    private async Task<(WebSocketMessageType Kind, byte[] Bytes)?> ReadAsync(CancellationToken ct)
    {
        using var bytes = new MemoryStream();
        var buffer = new byte[8192];
        ValueWebSocketReceiveResult part;
        do
        {
            part = await socket.ReceiveAsync(buffer.AsMemory(), ct);
            if (part.MessageType == WebSocketMessageType.Close) return null;
            if (bytes.Length + part.Count > (part.MessageType == WebSocketMessageType.Text ? 4096 : 32000)) throw InvalidFrame();
            bytes.Write(buffer, 0, part.Count);
        } while (!part.EndOfMessage);
        return (part.MessageType, bytes.ToArray());
    }

    private static SpeechFailure InvalidFrame() => new("voice_invalid_frame", "实时语音消息无效，请重新录音。", 400);
}
