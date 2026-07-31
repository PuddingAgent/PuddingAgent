using PuddingCode.Models;

namespace PuddingCode.Abstractions;

/// <summary>
/// Provider-neutral transcription entrypoint for bounded, materialized audio.
/// Provider/model selection remains outside Agent tools and channel adapters.
/// </summary>
public interface IAudioTranscriptionService
{
    Task<AudioTranscriptionResult> TranscribeAsync(
        AudioTranscriptionRequest request,
        CancellationToken ct = default);
}
