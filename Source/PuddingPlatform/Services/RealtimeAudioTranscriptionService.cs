using PuddingCode.Abstractions;
using PuddingSpeech;

namespace PuddingPlatform.Services;

/// <summary>Freezes the configured route once per capture; business code only sees the streaming capability.</summary>
public sealed class RealtimeAudioTranscriptionService(VoiceProviderFileService configs, IVoiceProviderFactory factory)
{
    public async Task<IStreamingSpeechRecognizer> CreateAsync(CancellationToken ct)
    {
        var config = await configs.LoadForUseAsync(ct);
        try
        {
            return factory.CreateAsrProvider(config) as IStreamingSpeechRecognizer
                ?? throw new SpeechFailure("voice_streaming_unavailable", "当前语音模型不支持实时转写，请检查语音设置。", 503);
        }
        catch (InvalidOperationException)
        { throw new SpeechFailure("voice_model_unavailable", "实时语音模型未配置，请检查语音设置。", 503); }
    }

    public async Task<bool> IsAvailableAsync(CancellationToken ct)
    {
        try { await CreateAsync(ct); return true; }
        catch (Exception error) when (error is SpeechFailure or NotSupportedException) { return false; }
    }
}
