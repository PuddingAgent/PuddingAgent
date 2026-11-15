using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace PuddingSpeech;

public interface ISpeechSocket : IAsyncDisposable
{
    Task SendTextAsync(string text, CancellationToken ct);
    Task SendAudioAsync(ReadOnlyMemory<byte> audio, CancellationToken ct);
    Task<string?> ReceiveAsync(CancellationToken ct);
}

public sealed record TranscriptSegment(int Id, string Text, bool IsFinal);

public sealed class QwenAsr(Func<CancellationToken, Task<ISpeechSocket>> connect)
{
    public const string Model = "qwen-audio-3.1-asr-flash-message";

    public async IAsyncEnumerable<TranscriptSegment> RecognizeAsync(
        byte[] wave, string? language = null, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var pcm = PcmWave.ReadPcm16KhzMono(wave);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lifetime.CancelAfter(TimeSpan.FromSeconds(150));
        await using var socket = await connect(lifetime.Token);
        var taskId = Guid.NewGuid().ToString("N");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runTask = JsonSerializer.Serialize(new
        {
            header = new { action = "run-task", task_id = taskId, streaming = "duplex" },
            payload = new
            {
                task_group = "audio", task = "asr", function = "recognition", model = Model,
                parameters = new { format = "pcm", sample_rate = 16000, intermediate_result_enabled = true,
                    disfluency_removal_enabled = false, language_hints = string.IsNullOrWhiteSpace(language) ? Array.Empty<string>() : new[] { language } },
                input = new { },
            },
        });
        await socket.SendTextAsync(runTask, lifetime.Token);
        var sender = SendAudioAsync();
        var completed = false;
        try
        {
            while (await socket.ReceiveAsync(lifetime.Token) is { } json)
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var header = root.GetProperty("header");
                if (header.GetProperty("task_id").GetString() != taskId) throw SpeechFailure.Protocol();
                switch (header.GetProperty("event").GetString())
                {
                    case "task-started": started.TrySetResult(); break;
                    case "task-failed": throw SpeechFailure.FromProvider(502, json);
                    case "result-generated":
                        if (!started.Task.IsCompletedSuccessfully) throw SpeechFailure.Protocol();
                        var sentence = root.GetProperty("payload").GetProperty("output").GetProperty("sentence");
                        if (sentence.TryGetProperty("heartbeat", out var heartbeat) && heartbeat.ValueKind == JsonValueKind.True) break;
                        yield return new(sentence.GetProperty("sentence_id").GetInt32(),
                            sentence.GetProperty("text").GetString() ?? "",
                            sentence.TryGetProperty("sentence_end", out var final) && final.ValueKind == JsonValueKind.True);
                        break;
                    case "task-finished":
                        if (!started.Task.IsCompletedSuccessfully) throw SpeechFailure.Protocol();
                        await sender;
                        completed = true;
                        yield break;
                }
            }
            if (!completed) throw SpeechFailure.Protocol();
        }
        finally
        {
            await lifetime.CancelAsync();
            try { await sender; } catch (OperationCanceledException) { } catch when (!completed) { }
        }

        async Task SendAudioAsync()
        {
            try
            {
                await started.Task.WaitAsync(lifetime.Token);
                for (var offset = 0; offset < pcm.Length; offset += 3200)
                    await socket.SendAudioAsync(pcm.AsMemory(offset, Math.Min(3200, pcm.Length - offset)), lifetime.Token);
                await socket.SendTextAsync(JsonSerializer.Serialize(new
                {
                    header = new { action = "finish-task", task_id = taskId, streaming = "duplex" },
                    payload = new { input = new { } },
                }), lifetime.Token);
            }
            catch { await lifetime.CancelAsync(); throw; }
        }
    }
}

public sealed class ClientSpeechSocket : ISpeechSocket
{
    private readonly ClientWebSocket socket = new();
    public static async Task<ISpeechSocket> ConnectAsync(Uri endpoint, string apiKey, CancellationToken ct)
    {
        var connection = new ClientSpeechSocket();
        connection.socket.Options.SetRequestHeader("Authorization", $"Bearer {apiKey}");
        try { await connection.socket.ConnectAsync(endpoint, ct); return connection; }
        catch (WebSocketException) { await connection.DisposeAsync(); throw new SpeechFailure("voice_connection_failed", "无法连接语音服务，请检查网络、凭据和区域设置。", 503, true); }
    }
    public Task SendTextAsync(string text, CancellationToken ct) => socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, ct);
    public Task SendAudioAsync(ReadOnlyMemory<byte> audio, CancellationToken ct) => socket.SendAsync(audio, WebSocketMessageType.Binary, true, ct).AsTask();
    public async Task<string?> ReceiveAsync(CancellationToken ct)
    {
        using var stream = new MemoryStream();
        var buffer = new byte[8192];
        ValueWebSocketReceiveResult frame;
        do
        {
            frame = await socket.ReceiveAsync(buffer.AsMemory(), ct);
            if (frame.MessageType == WebSocketMessageType.Close) return null;
            if (frame.MessageType != WebSocketMessageType.Text || stream.Length + frame.Count > 1024 * 1024) throw SpeechFailure.Protocol();
            stream.Write(buffer, 0, frame.Count);
        } while (!frame.EndOfMessage);
        return Encoding.UTF8.GetString(stream.ToArray());
    }
    public ValueTask DisposeAsync() { socket.Abort(); socket.Dispose(); return ValueTask.CompletedTask; }
}
