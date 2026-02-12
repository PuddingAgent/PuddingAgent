using PuddingCode.Models;

namespace PuddingCode.Abstractions;

/// <summary>
/// Business-level voice recognition entrypoint. It owns microphone-session policy,
/// privacy, turn handling, and conversion of final transcripts into message inputs.
/// </summary>
public interface IVoiceRecognitionService
{
    IAsyncEnumerable<VoiceRecognitionStreamEvent> StartAsync(
        VoiceRecognitionRequest request,
        IAsyncEnumerable<VoiceAudioFrame> audioFrames,
        CancellationToken ct = default);

    Task<VoiceRecognitionResult> RecognizeAsync(
        VoiceRecognitionRequest request,
        IAsyncEnumerable<VoiceAudioFrame> audioFrames,
        CancellationToken ct = default);
}
