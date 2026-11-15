using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace PuddingSpeech;

public static class QwenTts
{
    public const string Qwen3Protocol = "qwen3-tts-http";
    public const string AudioProtocol = "qwen-audio-tts-http";

    public static string BuildRequest(string protocol, string model, string text, string voice,
        string format, int sampleRate, string language = "Auto", string? instructions = null)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 10000)
            throw new SpeechFailure("voice_invalid_text", "朗读内容为空或过长，请选择较短的内容。", 400);
        object input = protocol switch
        {
            Qwen3Protocol => new { text, voice, language_type = language, instructions },
            AudioProtocol => new { text, voice, format, sample_rate = sampleRate, instruction = instructions },
            _ => throw new SpeechFailure("voice_model_unavailable", "语音模型协议未配置。", 503),
        };
        return JsonSerializer.Serialize(new { model, input }, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
    }

    /// <summary>Yields audio as it arrives; EOF without a provider terminal is an error.</summary>
    public static async IAsyncEnumerable<byte[]> ReadAudioAsync(Stream stream, [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        var data = new StringBuilder();
        var completed = false;
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.StartsWith(':')) continue;
            if (line.StartsWith("data:", StringComparison.Ordinal)) { if (data.Length > 0) data.Append('\n'); data.Append(line[5..].TrimStart()); continue; }
            if (line.Length != 0 || data.Length == 0) continue;
            var json = data.ToString(); data.Clear();
            if (json == "[DONE]") { completed = true; break; }
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(code.GetString()))
                throw SpeechFailure.FromProvider(502, json);
            if (!root.TryGetProperty("output", out var output)) continue;
            if (output.TryGetProperty("audio", out var audio) && audio.TryGetProperty("data", out var audioData) && !string.IsNullOrEmpty(audioData.GetString()))
                yield return Convert.FromBase64String(audioData.GetString()!);
            if (output.TryGetProperty("finish_reason", out var reason) && reason.GetString() == "stop") { completed = true; break; }
        }
        if (!completed) throw SpeechFailure.Protocol();
    }
}
