using PuddingCode.Models;

namespace PuddingCode.Abstractions;

/// <summary>
/// Provider-level adapter for realtime multimodal sessions. Implementations encapsulate
/// vendor WebSocket/WebRTC protocols, media upload, turn commit, response creation, and
/// provider event mapping.
/// </summary>
public interface IOmniRealtimeProvider
{
    string Provider { get; }

    OmniRealtimeProviderCapabilities Capabilities { get; }

    Task StartAsync(OmniRealtimeSessionRequest request, CancellationToken ct = default);

    Task SendInputAsync(OmniRealtimeInputFrame frame, CancellationToken ct = default);

    Task CommitAsync(string sessionId, CancellationToken ct = default);

    Task CreateResponseAsync(string sessionId, CancellationToken ct = default);

    Task CancelResponseAsync(string sessionId, string? responseId = null, CancellationToken ct = default);

    Task FinishAsync(string sessionId, CancellationToken ct = default);

    IAsyncEnumerable<OmniRealtimeStreamEvent> ReadEventsAsync(
        string sessionId,
        CancellationToken ct = default);
}
