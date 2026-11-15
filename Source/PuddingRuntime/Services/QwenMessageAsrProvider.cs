using PuddingCode.Abstractions;
using PuddingCode.Configuration;
using PuddingSpeech;

namespace PuddingRuntime.Services;

/// <summary>Maps the independently tested protocol into the existing neutral transcription port.</summary>
public sealed class QwenMessageAsrProvider(PuddingVoiceProviderConfig config) : IAsrHttpRecognizer, IStreamingSpeechRecognizer
{
    public IAsyncEnumerable<TranscriptSegment> RecognizePcmAsync(IAsyncEnumerable<ReadOnlyMemory<byte>> audio,
        string? language = null, CancellationToken ct = default) => CreateClient().RecognizePcmAsync(audio, language, ct);

    private QwenAsr CreateClient()
    {
        var endpoint = new UriBuilder(new Uri(config.Endpoint))
        { Scheme = "wss", Port = -1, Path = "/api-ws/v1/inference", Query = "" }.Uri;
        return new(token => ClientSpeechSocket.ConnectAsync(endpoint, config.ApiKey, token));
    }

    public async Task<AsrRecognizeResult> RecognizeAsync(byte[] audioData, string format, string? language, CancellationToken ct)
    {
        if (!string.Equals(format, "wav", StringComparison.OrdinalIgnoreCase))
            throw new SpeechFailure("voice_unsupported_audio", "请使用 PCM WAV 格式录音。", 415);
        var client = CreateClient();
        var sentences = new SortedDictionary<int, string>();
        try
        {
            await foreach (var segment in client.RecognizeAsync(audioData, language, ct))
                if (segment.IsFinal) sentences[segment.Id] = segment.Text;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new SpeechFailure("voice_timeout", "语音识别超时，请重试。", 504, true); }
        var text = string.Concat(sentences.Values).Trim();
        if (text.Length == 0) throw new SpeechFailure("voice_no_speech", "没有识别到声音，请靠近麦克风再试。", 400);
        return new(text, null);
    }
}
