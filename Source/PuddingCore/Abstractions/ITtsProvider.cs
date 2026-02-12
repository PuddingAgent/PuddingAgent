using PuddingCode.Models;

namespace PuddingCode.Abstractions;

/// <summary>
/// Provider-level TTS adapter. Implementations encapsulate vendor protocols such as
/// HTTP file synthesis, SSE chunking, or WebSocket duplex realtime synthesis.
/// </summary>
public interface ITtsProvider
{
    string Provider { get; }

    VoiceSynthesisProviderCapabilities Capabilities { get; }

    Task<VoiceSynthesisResult> SynthesizeAsync(
        VoiceSynthesisRequest request,
        CancellationToken ct = default);

    IAsyncEnumerable<VoiceSynthesisStreamEvent> StreamAsync(
        VoiceSynthesisRequest request,
        CancellationToken ct = default);
}
