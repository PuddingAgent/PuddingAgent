using PuddingCode.Models;

namespace PuddingCode.Abstractions;

/// <summary>
/// Converts short audio payloads without exposing operating-system codecs or
/// channel-specific formats to callers.
/// </summary>
public interface IAudioTranscoder
{
    Task<AudioTranscodingResult> TranscodeAsync(
        AudioTranscodingRequest request,
        CancellationToken ct = default);
}
