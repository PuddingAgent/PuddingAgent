using PuddingCode.Models;

namespace PuddingCode.Abstractions;

/// <summary>
/// Business-facing realtime multimodal entrypoint. It owns session policy, privacy,
/// browser media constraints, model selection, and projection of provider events into
/// the interaction OS.
/// </summary>
public interface IOmniRealtimeService
{
    IAsyncEnumerable<OmniRealtimeStreamEvent> StartAsync(
        OmniRealtimeSessionRequest request,
        IAsyncEnumerable<OmniRealtimeInputFrame> inputFrames,
        CancellationToken ct = default);
}
