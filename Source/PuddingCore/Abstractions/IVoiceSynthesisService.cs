using PuddingCode.Models;

namespace PuddingCode.Abstractions;

/// <summary>
/// Business-level voice synthesis entrypoint. It owns policy decisions such as provider
/// selection, privacy, caching, and whether a message should be spoken.
/// </summary>
public interface IVoiceSynthesisService
{
    Task<VoiceSynthesisResult> SynthesizeAsync(
        VoiceSynthesisRequest request,
        CancellationToken ct = default);

    IAsyncEnumerable<VoiceSynthesisStreamEvent> StreamAsync(
        VoiceSynthesisRequest request,
        CancellationToken ct = default);
}
